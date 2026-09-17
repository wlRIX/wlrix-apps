using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using System.Reactive;
using ReactiveUI;
using System.Collections.ObjectModel;
using Wlrix.Desks.Localization;
using Wlrix.Desks.Models;
using Wlrix.Desks.Services;

namespace Wlrix.Desks.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IDeskFeed _feed;
    private DeskViewModel? _selectedDesk;
    private DeskViewModel? _globalDesk;
    private bool _showGlobalDesk = true;
    private bool _showSnapshots = true;
    private DeskSnapshot? _lastSnapshot;
    private long? _hoveredWindowId;
    private long? _selectedWindowId;
    private Rect _world = new(0, 0, 1920, 1080);

    /// <summary>Design-time / previewer constructor: shows the sample layout.</summary>
    public MainWindowViewModel() : this(new SampleDeskFeed())
    {
        if (Design.IsDesignMode)
            Start();
    }

    public MainWindowViewModel(IDeskFeed feed)
    {
        _feed = feed;
        _feed.SnapshotReceived += OnSnapshot;
        _feed.Unavailable += OnUnavailable;

        NewDesk = ReactiveCommand.CreateFromTask(NewDeskAsync);
        GotoSelected = ReactiveCommand.CreateFromTask(GotoSelectedAsync);
        DeleteSelected = ReactiveCommand.CreateFromTask(DeleteSelectedAsync);
        MinimizeAll = ReactiveCommand.CreateFromTask(MinimizeAllAsync);
        RestoreAll = ReactiveCommand.CreateFromTask(RestoreAllAsync);
        AddSelectedToGlobal = ReactiveCommand.CreateFromTask(AddSelectedToGlobalAsync);
        RemoveSelectedFromDesk = ReactiveCommand.CreateFromTask(RemoveSelectedFromDeskAsync);
        MinimizeSelected = ReactiveCommand.CreateFromTask(MinimizeSelectedAsync);
        RestoreSelected = ReactiveCommand.CreateFromTask(RestoreSelectedAsync);
        RaiseSelected = ReactiveCommand.CreateFromTask(RaiseSelectedAsync);
        LowerSelected = ReactiveCommand.CreateFromTask(LowerSelectedAsync);
    }

    /// <summary>Raised (on the UI thread) the first time the compositor can't be reached.</summary>
    public event Action? FeedUnavailable;

    /// <summary>Raised (on the UI thread) when a command is rejected or fails, with a message.</summary>
    public event Action<string>? CommandFailed;

    /// <summary>The scrollable, selectable desks (excludes the Global desk).</summary>
    public ObservableCollection<DeskViewModel> Desks { get; } = [];

    /// <summary>The pinned Global desk tile (id 0), shown when <see cref="ShowGlobalDesk"/>.</summary>
    public DeskViewModel? GlobalDesk
    {
        get => _globalDesk;
        private set => this.RaiseAndSetIfChanged(ref _globalDesk, value);
    }

    public DeskViewModel? SelectedDesk
    {
        get => _selectedDesk;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedDesk, value);
            this.RaisePropertyChanged(nameof(CanOperateOnSelected));
        }
    }

    /// <summary>Goto / Delete apply only to a selected, non-Global desk.</summary>
    public bool CanOperateOnSelected => SelectedDesk is { IsGlobal: false };

    /// <summary>
    /// The window under the pointer, by snapshot id, shared by every tile: a window that shows
    /// on more than one desk (anything on the Global desk) is drawn hovered in all of them.
    /// </summary>
    public long? HoveredWindowId
    {
        get => _hoveredWindowId;
        set => this.RaiseAndSetIfChanged(ref _hoveredWindowId, value);
    }

    /// <summary>The selected window, by snapshot id — one at a time, across all desks. The
    /// Window menu acts on this.</summary>
    public long? SelectedWindowId
    {
        get => _selectedWindowId;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedWindowId, value);
            RaiseWindowMenuStates();
        }
    }

    /// <summary>The selected window as the last snapshot described it, or null.</summary>
    public WindowInfo? SelectedWindow =>
        SelectedWindowId is { } id ? _lastSnapshot?.Windows.FirstOrDefault(w => w.Id == id) : null;

    /// <summary>Most of the Window menu needs a selected window.</summary>
    public bool HasSelectedWindow => SelectedWindow is not null;

    public bool CanMinimizeSelected => SelectedWindow is { Minimized: false };

    public bool CanRestoreSelected => SelectedWindow is { Minimized: true };

    public bool ShowGlobalDesk
    {
        get => _showGlobalDesk;
        set
        {
            this.RaiseAndSetIfChanged(ref _showGlobalDesk, value);
            this.RaisePropertyChanged(nameof(GlobalDeskMenuHeader));
        }
    }

    public bool ShowSnapshots
    {
        get => _showSnapshots;
        set
        {
            this.RaiseAndSetIfChanged(ref _showSnapshots, value);
            this.RaisePropertyChanged(nameof(SnapshotsMenuHeader));
        }
    }

    /// <summary>
    /// The virtual-screen bounds the window rectangles are scaled from, set by the view from
    /// Avalonia's <see cref="Avalonia.Platform.Screen"/> layout. Re-applies the last snapshot
    /// so the previews rescale when the screen layout changes.
    /// </summary>
    public Rect World
    {
        get => _world;
        set
        {
            _world = value;
            if (_lastSnapshot is not null)
                ApplySnapshot(_lastSnapshot);
        }
    }

    public string GlobalDeskMenuHeader => Strings.GlobalDeskToggle(ShowGlobalDesk);

    public string SnapshotsMenuHeader => Strings.SnapshotsToggle(ShowSnapshots);


    // ── The menu's commands ─────────────────────────────────────────────────────────────
    //
    // Commands rather than Click handlers, because a keyboard shortcut needs something to
    // invoke. A MenuItem's InputGesture only draws the shortcut and its HotKey does not fire
    // from a submenu that has never been opened, so the accelerators live in the window's
    // KeyBindings — and a KeyBinding takes an ICommand and nothing else.
    //
    // None of them carries a canExecute: each underlying method already checks its own
    // preconditions and answers a completed task when they do not hold, so a shortcut pressed
    // with nothing selected does nothing rather than throwing. That is what lets the menu's
    // IsEnabled stay a matter of appearance.
    //
    // Rename is deliberately absent. It opens an inline editor and then has to focus it once
    // the template has realized the field, which is view work and stays in the view — and it
    // advertises no shortcut to need this.

    public ReactiveCommand<Unit, Unit> NewDesk { get; }
    public ReactiveCommand<Unit, Unit> GotoSelected { get; }
    public ReactiveCommand<Unit, Unit> DeleteSelected { get; }
    public ReactiveCommand<Unit, Unit> MinimizeAll { get; }
    public ReactiveCommand<Unit, Unit> RestoreAll { get; }
    public ReactiveCommand<Unit, Unit> AddSelectedToGlobal { get; }
    public ReactiveCommand<Unit, Unit> RemoveSelectedFromDesk { get; }
    public ReactiveCommand<Unit, Unit> MinimizeSelected { get; }
    public ReactiveCommand<Unit, Unit> RestoreSelected { get; }
    public ReactiveCommand<Unit, Unit> RaiseSelected { get; }
    public ReactiveCommand<Unit, Unit> LowerSelected { get; }

    public void Start() => _feed.Start();

    public Task NewDeskAsync() => Guard(() => _feed.CreateAsync(NextDeskName()));

    public Task GotoSelectedAsync() =>
        SelectedDesk is { IsGlobal: false } d ? Guard(() => _feed.SwitchAsync(d.Id)) : Task.CompletedTask;

    /// <summary>Opens the inline name editor on the selected desk.</summary>
    public void BeginRenameSelected()
    {
        if (SelectedDesk is { IsGlobal: false } d)
            d.BeginEdit();
    }

    /// <summary>
    /// Closes the inline editor and sends the rename. A blank or unchanged name is treated as
    /// "leave it alone", so committing an untouched field can't wipe a desk's name.
    /// </summary>
    public Task CommitRenameAsync(DeskViewModel desk)
    {
        if (!desk.IsEditing)
            return Task.CompletedTask;

        desk.EndEdit();
        var name = desk.EditName.Trim();
        return name.Length == 0 || name == desk.Name || desk.IsGlobal
            ? Task.CompletedTask
            : Guard(() => _feed.RenameAsync(desk.Id, name));
    }

    /// <summary>Closes the inline editor, discarding the draft.</summary>
    public void CancelRename(DeskViewModel desk) => desk.EndEdit();

    public Task DeleteSelectedAsync() =>
        SelectedDesk is { IsGlobal: false } d ? Guard(() => _feed.RemoveAsync(d.Id)) : Task.CompletedTask;

    // ── Window menu ─────────────────────────────────────────────────────────────────────

    /// <summary>Minimizes the active desk's own windows. Global-desk windows are left alone:
    /// they are on every desk, and they are the furniture (toolchest, background, this app).</summary>
    public Task MinimizeAllAsync() =>
        ForEachOnActiveDesk(w => !w.Minimized, w => _feed.MinimizeWindowAsync(w.Id));

    /// <summary>Restores the active desk's own minimized windows.</summary>
    public Task RestoreAllAsync() =>
        ForEachOnActiveDesk(w => w.Minimized, w => _feed.RestoreWindowAsync(w.Id));

    /// <summary>Moves the selected window onto the Global desk, so it shows on every desk.</summary>
    public Task AddSelectedToGlobalAsync() =>
        SelectedWindow is { } w && w.DeskId != 0
            ? Guard(() => _feed.MoveWindowToDeskAsync(w.Id, 0))
            : Task.CompletedTask;

    /// <summary>
    /// Takes the selected window off the Global desk, leaving it on the active desk only.
    /// A window that is already on a single ordinary desk has no second copy to remove, so
    /// this does nothing for it.
    /// </summary>
    public Task RemoveSelectedFromDeskAsync() =>
        SelectedWindow is { DeskId: 0 } w && ActiveDeskId is { } desk
            ? Guard(() => _feed.MoveWindowToDeskAsync(w.Id, desk))
            : Task.CompletedTask;

    public Task MinimizeSelectedAsync() =>
        SelectedWindow is { Minimized: false } w
            ? Guard(() => _feed.MinimizeWindowAsync(w.Id))
            : Task.CompletedTask;

    public Task RestoreSelectedAsync() =>
        SelectedWindow is { Minimized: true } w
            ? Guard(() => _feed.RestoreWindowAsync(w.Id))
            : Task.CompletedTask;

    public Task RaiseSelectedAsync() =>
        SelectedWindow is { } w ? Guard(() => _feed.RaiseWindowAsync(w.Id)) : Task.CompletedTask;

    public Task LowerSelectedAsync() =>
        SelectedWindow is { } w ? Guard(() => _feed.LowerWindowAsync(w.Id)) : Task.CompletedTask;

    public void Dispose()
    {
        _feed.SnapshotReceived -= OnSnapshot;
        _feed.Unavailable -= OnUnavailable;
        _feed.Dispose();
    }

    /// <summary>The active desk, or null before the first snapshot.</summary>
    private int? ActiveDeskId => _lastSnapshot?.Desks.FirstOrDefault(d => d.Active)?.Id;

    private async Task ForEachOnActiveDesk(Func<WindowInfo, bool> match, Func<WindowInfo, Task> action)
    {
        if (_lastSnapshot is not { } snapshot || ActiveDeskId is not { } deskId)
            return;

        foreach (var w in snapshot.Windows.Where(w => w.DeskId == deskId && match(w)).ToList())
            await Guard(() => action(w));
    }

    private void RaiseWindowMenuStates()
    {
        this.RaisePropertyChanged(nameof(SelectedWindow));
        this.RaisePropertyChanged(nameof(HasSelectedWindow));
        this.RaisePropertyChanged(nameof(CanMinimizeSelected));
        this.RaisePropertyChanged(nameof(CanRestoreSelected));
    }

    private async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            CommandFailed?.Invoke(ex.Message);
        }
    }

    private void OnSnapshot(DeskSnapshot snapshot) => Dispatcher.UIThread.Post(() => ApplySnapshot(snapshot));

    private void OnUnavailable() => Dispatcher.UIThread.Post(() => FeedUnavailable?.Invoke());

    private void ApplySnapshot(DeskSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        var world = _world;
        var globalWindows = snapshot.Windows.Where(w => w.DeskId == 0).ToList();
        var selectedId = SelectedDesk?.Id;

        // A closed window must not leave its id behind: the feed restarts its ids from 1 when
        // it reconnects, so a stale one could later point at an unrelated window.
        var live = snapshot.Windows.Select(w => w.Id).ToHashSet();
        if (SelectedWindowId is { } selectedWindow && !live.Contains(selectedWindow))
            SelectedWindowId = null;
        if (HoveredWindowId is { } hoveredWindow && !live.Contains(hoveredWindow))
            HoveredWindowId = null;

        // The pinned Global desk (id 0).
        if (snapshot.Desks.FirstOrDefault(d => d.Id == 0) is { } globalInfo)
        {
            var g = GlobalDesk ?? new DeskViewModel(0);
            g.Update(globalInfo, globalWindows, world);
            GlobalDesk = g;
        }

        // Reconcile the scrollable desks, reusing existing tiles by id so selection survives.
        var byId = Desks.ToDictionary(d => d.Id);
        var ordered = new List<DeskViewModel>();
        foreach (var info in snapshot.Desks.Where(d => d.Id != 0))
        {
            if (!byId.TryGetValue(info.Id, out var vm))
                vm = new DeskViewModel(info.Id);

            var windows = snapshot.Windows.Where(w => w.DeskId == info.Id).Concat(globalWindows).ToList();
            vm.Update(info, windows, world);
            ordered.Add(vm);
        }

        // Sync the collection in place rather than Clear + re-Add: snapshots arrive on every
        // window move, and rebuilding the list would drop the ListBox containers — taking an
        // open inline rename editor, and the keyboard focus in it, down with them.
        for (var i = 0; i < ordered.Count; i++)
        {
            if (i >= Desks.Count)
            {
                Desks.Add(ordered[i]);
            }
            else if (!ReferenceEquals(Desks[i], ordered[i]))
            {
                var existing = Desks.IndexOf(ordered[i]);
                if (existing >= 0)
                    Desks.Move(existing, i);
                else
                    Desks.Insert(i, ordered[i]);
            }
        }

        while (Desks.Count > ordered.Count)
            Desks.RemoveAt(Desks.Count - 1);

        SelectedDesk = selectedId is null ? null : ordered.FirstOrDefault(d => d.Id == selectedId);

        // The selected window's own state (minimized, which desk it is on) rides on the
        // snapshot, so the Window menu's enablement has to be re-evaluated with it.
        RaiseWindowMenuStates();
    }

    // Smallest "Desk N" (N >= 1) not already in use.
    private string NextDeskName()
    {
        var names = Desks.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        for (var n = 1; ; n++)
        {
            var candidate = Strings.DefaultDeskName(n);
            if (!names.Contains(candidate))
                return candidate;
        }
    }

}
