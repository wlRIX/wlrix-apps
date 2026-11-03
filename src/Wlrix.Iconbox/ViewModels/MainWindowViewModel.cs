using System.Collections.ObjectModel;
using Avalonia.Threading;
using ReactiveUI;
using Wlrix.Iconbox.Models;
using Wlrix.Iconbox.Services;

namespace Wlrix.Iconbox.ViewModels;

/// <summary>
/// The iconbox: one tile per window on the showing desks, kept in step with the compositor.
/// </summary>
public sealed class MainWindowViewModel : ReactiveObject, IDisposable
{
    private readonly ToplevelFeed? _feed;
    private readonly TileArt _art;
    private bool _unavailable;

    public MainWindowViewModel(ToplevelFeed? feed, TileArt art)
    {
        _feed = feed;
        _art = art;
        if (_feed is not null)
        {
            _feed.SnapshotReceived += OnSnapshot;
            _feed.Unavailable += OnUnavailable;
            _feed.Lost += OnLost;
        }
    }

    /// <summary>
    /// The compositor has gone, and the iconbox should go with it. Raised on the feed's thread:
    /// with its display gone, Avalonia's UI thread no longer runs what is posted to it.
    /// </summary>
    public event Action? CompositorLost;

    /// <summary>For the XAML designer: an empty iconbox with nothing behind it.</summary>
    public MainWindowViewModel()
        : this(null, new TileArt([]))
    {
    }

    /// <summary>The tiles, in the order the windows were opened.</summary>
    public ObservableCollection<TileViewModel> Tiles { get; } = [];

    /// <summary>There is no compositor to ask, so the tiles say nothing.</summary>
    public bool Unavailable
    {
        get => _unavailable;
        private set => this.RaiseAndSetIfChanged(ref _unavailable, value);
    }

    /// <summary>
    /// A click on a tile: minimize the window if it is up, restore it if it is not. Restoring
    /// also raises and focuses it, which is what the compositor's own tiles do.
    /// </summary>
    public void Toggle(TileViewModel tile)
    {
        if (tile.Minimized)
            _feed?.Restore(tile.Id);
        else
            _feed?.Minimize(tile.Id);
    }

    /// <summary>A right press on a tile: the window's own menu, from the compositor.</summary>
    public void ShowMenu(TileViewModel tile) => _feed?.ShowMenu(tile.Id);

    /// <summary>Make <see cref="Tiles"/> match <paramref name="windows"/>, keeping the tiles that stay.</summary>
    internal void Apply(IReadOnlyList<ToplevelInfo> windows)
    {
        var visible = WindowFilter.Visible(windows, Program.AppId);
        var wanted = visible.Select(window => window.Id).ToHashSet();

        for (var i = Tiles.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Tiles[i].Id))
                Tiles.RemoveAt(i);
        }

        // Both lists are in id order, so a window that was not there goes in where the first
        // tile with a later id is.
        for (var i = 0; i < visible.Count; i++)
        {
            var window = visible[i];
            if (i >= Tiles.Count || Tiles[i].Id != window.Id)
                Tiles.Insert(i, new TileViewModel(window.Id));
            Tiles[i].Update(window, _art.Find(window.AppId));
        }
    }

    public void Dispose()
    {
        if (_feed is null)
            return;
        _feed.SnapshotReceived -= OnSnapshot;
        _feed.Unavailable -= OnUnavailable;
        _feed.Lost -= OnLost;
        _feed.Dispose();
    }

    private void OnSnapshot(IReadOnlyList<ToplevelInfo> windows) =>
        Dispatcher.UIThread.Post(() =>
        {
            Unavailable = false;
            Apply(windows);
        });

    private void OnLost() => CompositorLost?.Invoke();

    private void OnUnavailable() =>
        Dispatcher.UIThread.Post(() =>
        {
            Unavailable = true;
            Tiles.Clear();
        });
}
