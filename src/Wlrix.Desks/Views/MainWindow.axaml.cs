using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wlrix.Avalonia.Dialogs;
using Wlrix.Desks.Controls;
using Wlrix.Desks.Localization;
using Wlrix.Desks.Models;
using Wlrix.Desks.Services;
using Wlrix.Desks.ViewModels;

namespace Wlrix.Desks.Views;

public partial class MainWindow : Window
{
    // Watching background.toml, for the shared background the snapshots draw.
    private readonly List<FileSystemWatcher> _watchers = [];

    // The pointer carrying a dragged window, and the desk it would land on.
    private IPointer? _dragPointer;
    private DeskViewModel? _dropTarget;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DeskPreview.WindowDragDeltaEvent, OnWindowDragDelta);
        AddHandler(DeskPreview.WindowDragEndedEvent, OnWindowDragEnded);
        AddHandler(KeyDownEvent, OnDragKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Where what the overview remembers is written, or null when this process must not write
    /// it.
    /// </summary>
    /// <remarks>
    /// Null for an overview started with <c>--new</c>, which is a second copy that did not get
    /// the bus name: it is a throwaway view of the desks, and it should not be able to
    /// overwrite the settings the real one keeps.
    /// </remarks>
    public IDesksSettingsStore? Settings { get; init; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is not MainWindowViewModel vm)
            return;

        vm.FeedUnavailable += OnFeedUnavailable;
        vm.CommandFailed += OnCommandFailed;
        vm.UiStateChanged += SaveState;
        vm.ScaleChanged += OnScaleChanged;
        vm.RenameStarted += OnRenameStarted;
        vm.ExitRequested += Close;
        // The previews are scaled against the screen layout, which the compositor owns and
        // Avalonia already knows; keep it in step with the desks feed.
        UpdateWorld();
        if (Screens is { } screens)
            screens.Changed += OnScreensChanged;
        WatchSharedBackground();
        vm.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (Screens is { } screens)
            screens.Changed -= OnScreensChanged;
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
        if (DataContext is MainWindowViewModel vm)
        {
            vm.FeedUnavailable -= OnFeedUnavailable;
            vm.CommandFailed -= OnCommandFailed;
            vm.UiStateChanged -= SaveState;
            vm.ScaleChanged -= OnScaleChanged;
            vm.RenameStarted -= OnRenameStarted;
            vm.ExitRequested -= Close;
            // Before Dispose, and the only point at which the window's size can still be read.
            SaveState();
            vm.Dispose();
        }
    }

    /// <summary>
    /// Writes what this window is showing, so the next run opens looking the same.
    /// </summary>
    /// <remarks>
    /// Called on close, which is what "remember the state" means, and again whenever a menu
    /// item changes one of the toggles. The second path is not belt and braces: the session
    /// signals its children at logout and Avalonia installs no handler for that, so neither
    /// this window's Closed nor the lifetime's ShutdownRequested runs on the way out of a
    /// session. A toggle picked and then logged out of would otherwise be lost, which is the
    /// ordinary way this application ends rather than an edge case. The store ignores a save
    /// that would rewrite what is already on disk, so the overlap costs nothing.
    ///
    /// <para>
    /// The window's size is read from <see cref="Layoutable.Width"/> and
    /// <see cref="Layoutable.Height"/>, which Avalonia assigns from the platform's client size
    /// whenever the window is resized, so they follow the user rather than the markup.
    /// </para>
    /// </remarks>
    private void SaveState()
    {
        if (Settings is not { } store || DataContext is not MainWindowViewModel vm)
            return;

        store.Save(new DesksSettings
        {
            ShowSnapshots = vm.ShowSnapshots,
            ShowGlobalDesk = vm.ShowGlobalDesk,
            WindowLabel = vm.WindowLabel,
            PresetScale = vm.PresetScale,
            UserDefinedScale = vm.UserDefinedScale,
            UseUserDefinedScale = vm.UseUserDefinedScale,
            WindowWidth = Width,
            WindowHeight = Height,
        });
    }

    private void OnScreensChanged(object? sender, EventArgs e) => UpdateWorld();

    // The bounding box of every screen, in Avalonia's coordinate space (which matches the
    // compositor-logical geometry the wlrix-desks feed reports).
    private void UpdateWorld()
    {
        if (DataContext is not MainWindowViewModel vm || Screens?.All is not { Count: > 0 } all)
            return;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var screen in all)
        {
            var bounds = screen.Bounds;
            minX = Math.Min(minX, bounds.X);
            minY = Math.Min(minY, bounds.Y);
            maxX = Math.Max(maxX, bounds.X + bounds.Width);
            maxY = Math.Max(maxY, bounds.Y + bounds.Height);
        }

        vm.World = new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
        // On Wayland a screen's display name is its wl_output name -- the connector, which is
        // what background.toml's [[output]] sections are keyed by.
        vm.Screens = [.. all.Select(screen => new ScreenArea(
            new Rect(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height),
            screen.DisplayName))];
    }

    // background.toml is written by wlrix-settings-daemon, which renames a new file into place,
    // so the directory is what is watched rather than the file. Both directories wlrix-bg reads
    // from are, since a user's file appearing or going away changes which one applies.
    private void WatchSharedBackground()
    {
        foreach (var directory in SharedBackground.SearchPaths().Select(Path.GetDirectoryName).Distinct())
        {
            if (directory is null || !Directory.Exists(directory))
                continue;
            try
            {
                var watcher = new FileSystemWatcher(directory, "background.toml")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                };
                FileSystemEventHandler changed = (_, _) => Dispatcher.UIThread.Post(() =>
                    (DataContext as MainWindowViewModel)?.ReloadSharedBackground());
                watcher.Changed += changed;
                watcher.Created += changed;
                watcher.Deleted += changed;
                watcher.Renamed += (_, _) => changed(this, null!);
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Without a watcher the snapshots show the background as it was at startup,
                // which is a stale preview rather than anything worth failing over.
            }
        }
    }

    private void OnFeedUnavailable() => _ = MessageDialog.ShowAsync(this, DialogType.Error,
        Strings.CompositorUnreachable, buttons: DialogButtons.Ok);

    private void OnCommandFailed(string message) =>
        _ = MessageDialog.ShowAsync(this, DialogType.Error, message, buttons: DialogButtons.Ok);

    private void OnToggleGlobalDesk(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.ShowGlobalDesk = !vm.ShowGlobalDesk;
    }

    private void OnToggleSnapshots(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.ShowSnapshots = !vm.ShowSnapshots;
    }

    private void OnShowWindowNames(object? sender, RoutedEventArgs e) => SetWindowLabel(WindowLabel.Title);

    private void OnShowApplicationIds(object? sender, RoutedEventArgs e) => SetWindowLabel(WindowLabel.AppId);

    private void OnShowNoNames(object? sender, RoutedEventArgs e) => SetWindowLabel(WindowLabel.None);

    private void SetWindowLabel(WindowLabel label)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.WindowLabel = label;
    }

    private void OnPresetScale(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is MenuItem { Tag: string tag }
            && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent))
            vm.ChoosePresetScale(percent);
    }

    private void OnUserDefinedScale(object? sender, RoutedEventArgs e) =>
        (DataContext as MainWindowViewModel)?.ChooseUserDefinedScale();

    private void OnCustomScale(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            _ = CustomScaleDialog.ShowAsync(this, vm.Scale, vm.SetUserDefinedScale);
    }

    // The window grows and shrinks with its tiles, so a larger scale shows the same desks
    // rather than the top of them, and a smaller one leaves no empty strip below. Every row of
    // tiles changes by the same amount; the width is left alone, because the strip scrolls
    // sideways and more columns is what a wider window was for.
    private void OnScaleChanged(double heightDelta)
    {
        if (DataContext is not MainWindowViewModel { ShowSnapshots: true } || heightDelta == 0
            || !double.IsFinite(Height))
            return;

        Height = Math.Max(MinHeight, Height + heightDelta * RowsOfTiles());
    }

    // How many tiles are stacked in the desk list's first column; at least one, for the Global
    // tile beside an empty list.
    private int RowsOfTiles()
    {
        var tiles = DeskList.GetRealizedContainers().ToList();
        if (tiles.Count == 0)
            return 1;

        var firstColumn = tiles.Min(tile => tile.Bounds.X);
        return Math.Max(1, tiles.Count(tile => tile.Bounds.X == firstColumn));
    }

    private void OnProductInformation(object? sender, RoutedEventArgs e)
    {
        var version = GetType().Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        _ = MessageDialog.ShowAsync(this, DialogType.Information, Strings.About(version),
            title: Strings.AboutTitle, buttons: DialogButtons.Ok);
    }


    // The template only realizes the name field when IsEditing flips, so focusing it waits
    // for that layout pass.
    private void OnRenameStarted(DeskViewModel desk) =>
        Dispatcher.UIThread.Post(() => FocusNameEditor(desk), DispatcherPriority.Loaded);

    private void FocusNameEditor(DeskViewModel desk)
    {
        var editor = this.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(t => t.Name == "NameEditor" && ReferenceEquals(t.DataContext, desk));
        if (editor is null)
            return;

        editor.Focus();
        editor.SelectAll();
    }

    // Enter commits, Escape abandons the draft. Clicking away commits too (LostFocus), which is
    // how IRIX's inline edits behaved.
    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm ||
            sender is not TextBox { DataContext: DeskViewModel desk })
            return;

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                _ = vm.CommitRenameAsync(desk);
                DeskList.Focus();
                break;
            case Key.Escape:
                e.Handled = true;
                vm.CancelRename(desk);
                DeskList.Focus();
                break;
        }
    }

    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm &&
            sender is TextBox { DataContext: DeskViewModel desk })
            _ = vm.CommitRenameAsync(desk);
    }











    // ── Dragging a window between desks ─────────────────────────────────────────────────
    //
    // As IRIX's overview did it: the window's ghost follows the pointer, the desk it is over
    // turns blue, and letting go puts the window on that desk where the ghost was. Letting go
    // over the desk it is already on just moves it there.

    private void OnWindowDragDelta(object? sender, WindowDragEventArgs e)
    {
        _dragPointer = e.Pointer;

        var position = e.PositionIn(DragLayer) - e.GrabOffset;
        Ghost.Width = e.Window.W;
        Ghost.Height = e.Window.H;
        Canvas.SetLeft(Ghost, Math.Round(position.X));
        Canvas.SetTop(Ghost, Math.Round(position.Y));
        Ghost.IsVisible = true;

        SetDropTarget(TileUnder(e)?.DataContext as DeskViewModel);
    }

    private void OnWindowDragEnded(object? sender, WindowDragEventArgs e)
    {
        var tile = e.Canceled ? null : TileUnder(e);
        Ghost.IsVisible = false;
        SetDropTarget(null);
        _dragPointer = null;

        if (tile is { DataContext: DeskViewModel desk } && DataContext is MainWindowViewModel vm
            && tile.GetVisualDescendants().OfType<DeskPreview>().FirstOrDefault() is { } preview)
            _ = vm.DropWindowAsync(e.Window.Id, desk, e.PositionIn(preview) - e.GrabOffset);
    }

    // Escape puts the window back. Releasing the pointer is what ends the drag, and the preview
    // that holds it reports the end as canceled.
    private void OnDragKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _dragPointer is not { } pointer)
            return;

        e.Handled = true;
        pointer.Capture(null);
    }

    // The desk tile under the pointer -- its header as much as its snapshot, as the whole tile
    // lights up -- or null over anything else.
    private Border? TileUnder(WindowDragEventArgs e) =>
        (this.InputHitTest(e.PositionIn(this)) as Visual)?
            .GetSelfAndVisualAncestors()
            .OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("tile"));

    private void SetDropTarget(DeskViewModel? desk)
    {
        if (ReferenceEquals(desk, _dropTarget))
            return;
        _dropTarget?.IsDropTarget = false;
        _dropTarget = desk;
        _dropTarget?.IsDropTarget = true;
    }

    // Clicking empty space in the desk strip (not on a tile, not the scrollbar) clears the selection.
    private void OnDeskListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || e.Source is not Visual source)
            return;

        if (source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is null &&
            source.FindAncestorOfType<ScrollBar>(includeSelf: true) is null)
            vm.SelectedDesk = null;
    }

    // Double-clicking a desk tile selects it and switches to it.
    private async void OnDeskDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm &&
            DeskItemFrom(e.Source)?.DataContext is DeskViewModel { IsEditing: false } desk)
        {
            vm.SelectedDesk = desk;
            await vm.GotoSelectedAsync();
        }
    }

    // The desk tile (ListBoxItem) an event originated from, or null when it was empty space.
    private static ListBoxItem? DeskItemFrom(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);

    // No hand-written `InitializeComponent()` here on purpose. The generated one
    // (InitializeComponent(bool loadXaml = true)) loads the XAML *and* assigns the x:Name
    // fields; a parameterless override wins overload resolution, leaving DeskList null.
}
