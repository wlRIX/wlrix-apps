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
    private WindowLabel _windowLabel = WindowLabel.Title;
    private int _presetScale = DeskScale.Default;
    private int? _userDefinedScale;
    private bool _useUserDefinedScale;
    private DeskSnapshot? _lastSnapshot;
    private long? _hoveredWindowId;
    private long? _selectedWindowId;
    private Rect _world = new(0, 0, 1920, 1080);
    private IReadOnlyList<ScreenArea> _screens = [];
    private SharedBackground _sharedBackground;
    private readonly WallpaperPictures _pictures = new();

    /// <summary>Design-time / previewer constructor: shows the sample layout.</summary>
    public MainWindowViewModel() : this(new SampleDeskFeed())
    {
        if (Design.IsDesignMode)
            Start();
    }

    /// <param name="initial">What the overview looked like when it was last closed, as
    /// amended by this run's command line. Null takes the defaults, which is what the
    /// previewer wants and what keeps it off the user's own settings file.</param>
    /// <param name="sharedBackground">What <c>background.toml</c> says, for the desks without a
    /// background of their own. Null reads the file; a test passes its own.</param>
    public MainWindowViewModel(IDeskFeed feed, DesksSettings? initial = null, SharedBackground? sharedBackground = null)
    {
        _sharedBackground = sharedBackground ?? SharedBackground.Load();
        // A picture that finishes decoding is drawn the next time the snapshots are laid out,
        // which is now: there may be no snapshot coming for a while.
        _pictures.Decoded += () => Dispatcher.UIThread.Post(Relayout);

        if (initial is { } settings)
        {
            _showGlobalDesk = settings.ShowGlobalDesk;
            _showSnapshots = settings.ShowSnapshots;
            _windowLabel = settings.WindowLabel;
            _presetScale = settings.PresetScale;
            _userDefinedScale = settings.UserDefinedScale;
            _useUserDefinedScale = settings.UseUserDefinedScale && settings.UserDefinedScale is not null;
        }

        _feed = feed;
        _feed.SnapshotReceived += OnSnapshot;
        _feed.Unavailable += OnUnavailable;

        NewDesk = ReactiveCommand.CreateFromTask(NewDeskAsync);
        RenameSelected = ReactiveCommand.Create(BeginRenameSelected);
        Exit = ReactiveCommand.Create(() => ExitRequested?.Invoke());
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

    /// <summary>
    /// Raised when something the overview remembers between runs has changed.
    /// </summary>
    /// <remarks>
    /// An event rather than a <c>WhenAnyValue</c> subscription, to match the two above: this
    /// class is otherwise plain properties, and a reactive pipeline here would bring a
    /// subscription lifetime with it for three setters.
    /// </remarks>
    public event Action? UiStateChanged;

    /// <summary>
    /// Raised when a desk's inline name editor has been opened, so the view can focus the
    /// field once the template has realized it.
    /// </summary>
    public event Action<DeskViewModel>? RenameStarted;

    /// <summary>Raised by the Overview menu's Exit; closing the window is the view's.</summary>
    public event Action? ExitRequested;

    /// <summary>
    /// Raised when the snapshot scale changes, with how much taller (or, negative, shorter) a
    /// snapshot became, so the window can grow or shrink with its tiles.
    /// </summary>
    public event Action<double>? ScaleChanged;

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
            var changed = value != _showGlobalDesk;
            this.RaiseAndSetIfChanged(ref _showGlobalDesk, value);
            this.RaisePropertyChanged(nameof(GlobalDeskMenuHeader));
            if (changed)
                UiStateChanged?.Invoke();
        }
    }

    public bool ShowSnapshots
    {
        get => _showSnapshots;
        set
        {
            var changed = value != _showSnapshots;
            this.RaiseAndSetIfChanged(ref _showSnapshots, value);
            this.RaisePropertyChanged(nameof(SnapshotsMenuHeader));

            // With no previews there is nothing left to click, so a selection made before they
            // were hidden would sit there invisibly and the Window menu would keep acting on
            // it.
            if (!value)
            {
                SelectedWindowId = null;
                HoveredWindowId = null;
            }

            if (changed)
                UiStateChanged?.Invoke();
        }
    }

    /// <summary>What a miniature window says when the pointer passes over it.</summary>
    public WindowLabel WindowLabel
    {
        get => _windowLabel;
        set
        {
            var changed = value != _windowLabel;
            this.RaiseAndSetIfChanged(ref _windowLabel, value);
            this.RaisePropertyChanged(nameof(ShowsWindowNames));
            this.RaisePropertyChanged(nameof(ShowsApplicationIds));
            this.RaisePropertyChanged(nameof(ShowsNoNames));
            if (changed)
                UiStateChanged?.Invoke();
        }
    }

    // The Overview menu's three radio items, which need a bool each to draw their check mark.
    public bool ShowsWindowNames => WindowLabel == WindowLabel.Title;

    public bool ShowsApplicationIds => WindowLabel == WindowLabel.AppId;

    public bool ShowsNoNames => WindowLabel == WindowLabel.None;

    /// <summary>The Scaling submenu's preset, whether or not it is the one in use.</summary>
    public int PresetScale => _presetScale;

    /// <summary>What the Custom dialog was last set to, or null if it never has been.</summary>
    public int? UserDefinedScale => _userDefinedScale;

    public bool UseUserDefinedScale => _useUserDefinedScale;

    /// <summary>How large the snapshots are drawn, as a percentage of the screen layout.</summary>
    public int Scale => _useUserDefinedScale && _userDefinedScale is { } user ? user : _presetScale;

    /// <summary>The preset whose radio item is checked: none while the user-defined amount is
    /// in use, even when it happens to equal one of them.</summary>
    public int? CheckedPresetScale => _useUserDefinedScale ? null : _presetScale;

    /// <summary>The submenu lists the user-defined amount once there is one to list.</summary>
    public bool HasUserDefinedScale => _userDefinedScale is not null;

    public string UserDefinedScaleMenuHeader =>
        _userDefinedScale is { } user ? Strings.UserDefinedScale(user) : string.Empty;

    /// <summary>Draws the snapshots at one of the Scaling submenu's presets.</summary>
    public void ChoosePresetScale(int percent)
    {
        if (DeskScale.IsPreset(percent))
            SetScale(percent, _userDefinedScale, useUserDefined: false);
    }

    /// <summary>Goes back to the user-defined amount, if there is one.</summary>
    public void ChooseUserDefinedScale()
    {
        if (_userDefinedScale is not null)
            SetScale(_presetScale, _userDefinedScale, useUserDefined: true);
    }

    /// <summary>Sets the user-defined amount and draws the snapshots at it: the Custom
    /// dialog's OK and Apply.</summary>
    public void SetUserDefinedScale(int percent) =>
        SetScale(_presetScale, Math.Clamp(percent, DeskScale.Minimum, DeskScale.Maximum), useUserDefined: true);

    private void SetScale(int preset, int? userDefined, bool useUserDefined)
    {
        var before = Scale;
        var remembered = (_presetScale, _userDefinedScale, _useUserDefinedScale);
        _presetScale = preset;
        _userDefinedScale = userDefined;
        _useUserDefinedScale = useUserDefined;
        if (remembered == (_presetScale, _userDefinedScale, _useUserDefinedScale))
            return;

        this.RaisePropertyChanged(nameof(PresetScale));
        this.RaisePropertyChanged(nameof(UserDefinedScale));
        this.RaisePropertyChanged(nameof(UseUserDefinedScale));
        this.RaisePropertyChanged(nameof(Scale));
        this.RaisePropertyChanged(nameof(CheckedPresetScale));
        this.RaisePropertyChanged(nameof(HasUserDefinedScale));
        this.RaisePropertyChanged(nameof(UserDefinedScaleMenuHeader));

        if (Scale != before)
        {
            if (_lastSnapshot is not null)
                ApplySnapshot(_lastSnapshot);
            ScaleChanged?.Invoke(Math.Round(_world.Height * Scale / 100.0)
                                 - Math.Round(_world.Height * before / 100.0));
        }

        UiStateChanged?.Invoke();
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
            Relayout();
        }
    }

    /// <summary>
    /// The screens, with their connector names, which a desk's background is laid out over
    /// one by one: <c>background.toml</c> can give each monitor its own. Set by the view from
    /// Avalonia's screens alongside <see cref="World"/>.
    /// </summary>
    public IReadOnlyList<ScreenArea> Screens
    {
        get => _screens;
        set
        {
            _screens = value;
            Relayout();
        }
    }

    /// <summary>Reads <c>background.toml</c> again, after it changed on disk.</summary>
    public void ReloadSharedBackground()
    {
        _sharedBackground = SharedBackground.Load();
        // A picture can be replaced under the same name, which is how Background Settings'
        // own copies are named.
        _pictures.Clear();
        Relayout();
    }

    private void Relayout()
    {
        if (_lastSnapshot is not null)
            ApplySnapshot(_lastSnapshot);
    }

    /// <summary>
    /// A desk's background laid out in its preview: one area per screen, each with the desk's
    /// own background or, for a desk without one, what <c>background.toml</c> gives that screen.
    /// </summary>
    private IReadOnlyList<PreviewWallpaper> BackgroundsFor(Wallpaper? own, Rect world, double scale)
    {
        // Before the view has said what the screens are, the whole layout is one unnamed screen.
        var screens = _screens.Count > 0 ? _screens : [new ScreenArea(world, null)];
        var result = new List<PreviewWallpaper>(screens.Count);
        foreach (var screen in screens)
        {
            var wallpaper = own ?? _sharedBackground.Resolve(screen.Name);
            var picture = wallpaper.Image is { } path ? _pictures.Get(path) : null;
            var bounds = screen.Bounds;
            result.Add(new PreviewWallpaper(
                new Rect((bounds.X - world.X) * scale, (bounds.Y - world.Y) * scale,
                    bounds.Width * scale, bounds.Height * scale),
                wallpaper.Mode,
                wallpaper.Color,
                picture?.Bitmap,
                picture is null ? default : new Size(picture.NativeSize.Width * scale, picture.NativeSize.Height * scale)));
        }
        return result;
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
    // Rename and Exit end in view work — focusing the editor once the template has realized
    // it, closing the window — so their commands do the model's half and raise an event for
    // the rest.

    public ReactiveCommand<Unit, Unit> NewDesk { get; }
    public ReactiveCommand<Unit, Unit> RenameSelected { get; }
    public ReactiveCommand<Unit, Unit> Exit { get; }
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
    /// <remarks>
    /// An editor that is already open is left alone: the shortcut reaches the window even from
    /// inside the field, and reopening it would throw away what has been typed.
    /// </remarks>
    public void BeginRenameSelected()
    {
        if (SelectedDesk is not { IsGlobal: false, IsEditing: false } d)
            return;

        d.BeginEdit();
        RenameStarted?.Invoke(d);
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

    /// <summary>
    /// Drops a window dragged across the overview: onto <paramref name="desk"/>, with its
    /// top-left corner at <paramref name="previewTopLeft"/> in that desk's preview.
    /// </summary>
    /// <remarks>
    /// The point is scaled back up to the screen layout and kept inside it, so a window let go
    /// with its corner hanging off a snapshot's edge lands against the screen's edge rather
    /// than off it.
    /// </remarks>
    public Task DropWindowAsync(long windowId, DeskViewModel desk, Point previewTopLeft)
    {
        if (_lastSnapshot?.Windows.FirstOrDefault(w => w.Id == windowId) is not { Minimized: false } window)
            return Task.CompletedTask;

        var scale = Scale / 100.0;
        var x = Math.Clamp(_world.X + previewTopLeft.X / scale, _world.X, Math.Max(_world.X, _world.Right - window.W));
        var y = Math.Clamp(_world.Y + previewTopLeft.Y / scale, _world.Y, Math.Max(_world.Y, _world.Bottom - window.H));
        return Guard(() => _feed.PlaceWindowAsync(window.Id, desk.Id, (int)Math.Round(x), (int)Math.Round(y)));
    }

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

    /// <summary>
    /// Unminimizes this application's own window, if the last snapshot says it is minimized.
    /// </summary>
    /// <remarks>
    /// For the second launch that asks the overview to come forward. Raising a window through
    /// <c>xdg_activation_v1</c> does exactly that and no more — the compositor's handler raises
    /// the surface without clearing the minimized flag — so an overview the user had minimized
    /// would be brought to the top of a stack it is not in. This is the one application that
    /// can read its own window's state out of its own feed, so it restores itself first.
    /// </remarks>
    public Task RestoreOwnWindowIfMinimizedAsync() =>
        _lastSnapshot?.Windows.FirstOrDefault(w => w.Minimized && w.AppId == Program.AppId) is { } own
            ? Guard(() => _feed.RestoreWindowAsync(own.Id))
            : Task.CompletedTask;

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
            g.Update(globalInfo, globalWindows, world, Scale, BackgroundsFor(null, world, Scale / 100.0));
            GlobalDesk = g;
        }

        // Reconcile the scrollable desks, reusing existing tiles by id so selection survives.
        var byId = Desks.ToDictionary(d => d.Id);
        var ordered = new List<DeskViewModel>();
        foreach (var info in snapshot.Desks.Where(d => d.Id != 0))
        {
            if (!byId.TryGetValue(info.Id, out var vm))
                vm = new DeskViewModel(info.Id);

            // One pass over the snapshot rather than the desk's windows followed by the global
            // desk's, so the two stay interleaved in the stacking order the feed gives them in.
            var windows = snapshot.Windows.Where(w => w.DeskId == info.Id || w.DeskId == 0).ToList();
            vm.Update(info, windows, world, Scale, BackgroundsFor(info.Background, world, Scale / 100.0));
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
