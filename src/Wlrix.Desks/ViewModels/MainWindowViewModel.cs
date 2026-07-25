using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ReactiveUI;
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

    public string GlobalDeskMenuHeader => ShowGlobalDesk ? "Hide Global Desk" : "Show Global Desk";

    public string SnapshotsMenuHeader => ShowSnapshots ? "Hide Snapshots" : "Show Snapshots";

    public void Start() => _feed.Start();

    public Task NewDeskAsync() => Guard(() => _feed.CreateAsync(NextDeskName()));

    public Task GotoSelectedAsync() =>
        SelectedDesk is { IsGlobal: false } d ? Guard(() => _feed.SwitchAsync(d.Id)) : Task.CompletedTask;

    public Task DeleteSelectedAsync() =>
        SelectedDesk is { IsGlobal: false } d ? Guard(() => _feed.RemoveAsync(d.Id)) : Task.CompletedTask;

    public void Dispose()
    {
        _feed.SnapshotReceived -= OnSnapshot;
        _feed.Unavailable -= OnUnavailable;
        _feed.Dispose();
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

        Desks.Clear();
        foreach (var vm in ordered)
            Desks.Add(vm);

        SelectedDesk = selectedId is null ? null : ordered.FirstOrDefault(d => d.Id == selectedId);
    }

    // Smallest "Desk N" (N >= 1) not already in use.
    private string NextDeskName()
    {
        var names = Desks.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        for (var n = 1; ; n++)
        {
            var candidate = $"Desk {n}";
            if (!names.Contains(candidate))
                return candidate;
        }
    }

}
