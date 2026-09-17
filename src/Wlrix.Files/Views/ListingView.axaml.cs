using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Wayland;
using Wlrix.Files.Controls;
using Wlrix.Files.Core;
using Wlrix.Files.Core.Dnd;
using Wlrix.Files.Core.Thumbnails;
using Wlrix.Files.ViewModels;
using Location = Wlrix.Files.Core.Location;

namespace Wlrix.Files.Views;

/// <summary>One tab's listing: the icon grid, the details table, and the pointer work.</summary>
/// <remarks>
/// Its data context is a <see cref="TabViewModel"/>, because the tab strip gives each tab one
/// of these; what it needs from the window it reaches through <c>Tab.Window</c>. The places
/// rail stays in <see cref="BrowserView"/>, since a rail belongs to a window and the future
/// picker wants one beside a listing with no tabs at all.
/// </remarks>
public partial class ListingView : UserControl
{
    /// <summary>Icon sizes for the two views.</summary>
    /// <remarks>
    /// Both are sizes real themes ship artwork at, so a lookup finds an exact directory
    /// rather than scaling the nearest one.
    /// </remarks>
    private const int DetailsIconSize = 16;
    private const int GridIconSize = 48;

    /// <summary>How far the pointer must move before a press becomes a band drag.</summary>
    /// <remarks>
    /// The same six pixels the archiver uses to start a file drag. Without it, a click that
    /// wobbles by one pixel would clear the selection it was meant to make.
    /// </remarks>
    private const double BandThreshold = 6;

    /// <summary>How far the pointer must move over a row before the press becomes a drag.</summary>
    private const double DragThreshold = 6;

    /// <summary>How often the drag timer runs, in milliseconds.</summary>
    /// <remarks>
    /// A frame. Both things it drives — edge scrolling and the spring-load clock — have to
    /// keep going while the pointer is perfectly still, and drag-over events do not: Wayland
    /// only sends motion when there is motion.
    /// </remarks>
    private const int DragTickMs = 16;

    private Point? _bandOrigin;
    private bool _banding;

    private readonly SpringLoad _spring = new();
    private readonly DispatcherTimer _dragTimer;

    private Point? _pressedAt;
    private ListBox? _pressedIn;

    /// <summary>
    /// The press that may become a drag.
    /// </summary>
    /// <remarks>
    /// Held rather than the move event that crosses the threshold, and not only because
    /// <c>DoDragDropAsync</c> asks for a <see cref="PointerPressedEventArgs"/>: the Wayland
    /// backend reads the input serial off it, and <c>wl_data_device.start_drag</c> is
    /// validated against the serial of the press that began the implicit grab. Any other
    /// serial is refused, which looks exactly like drag and drop not working.
    ///
    /// <para>
    /// It is good for exactly one drag. Consuming the press nulls the seat behind it, so a
    /// second attempt with the same args fails silently as <c>DragDropEffects.None</c> —
    /// which is also what an ordinary refusal looks like, so it carries no clue at all. It is
    /// cleared the moment it is used and a fresh press is required.
    /// </para>
    /// </remarks>
    private PointerPressedEventArgs? _press;

    /// <summary>
    /// What was selected when the press landed, before the ListBox collapsed it.
    /// </summary>
    /// <remarks>
    /// Pressing an already-selected row with no modifier sets the selection to just that row,
    /// which would make dragging a multi-file selection impossible: by the time the pointer
    /// has moved far enough to be a drag, there is one file left. Snapshotting on the way past
    /// in the tunnel — rather than handling the press to stop the collapse — leaves the
    /// double-tap gesture and focus handling alone, which suppressing the event would not.
    /// </remarks>
    private List<FileEntryViewModel> _pressedSelection = [];

    private ScrollViewer? _dragScroll;
    private Point _dragPoint;
    private FileEntryViewModel? _dropRow;
    private DateTimeOffset _lastTick;

    /// <summary>Whether a drag-over arrived after the last drag-leave.</summary>
    /// <remarks>
    /// Drag-leave bubbles, and the drag device raises one every time the hit-tested element
    /// changes — which includes moving from one row to the next, and even from a row's icon
    /// to its own label. Ending the drag on it directly would reset the spring-load clock
    /// whenever the hand wobbled, so a folder would never sit still long enough to open.
    /// The leave is only real if no drag-over follows it in the same input event.
    /// </remarks>
    private bool _dragOverSinceLeave;

    public ListingView()
    {
        // No hand-written parameterless InitializeComponent: the generated
        // InitializeComponent(bool) both loads the XAML and assigns the x:Name fields, and an
        // override wins overload resolution, leaving every named control null.
        InitializeComponent();

        Listing.DoubleTapped += OnListingDoubleTapped;
        IconView.DoubleTapped += OnIconViewDoubleTapped;

        // Touching a listing makes its pane the one the window acts on. On the tunnel, so it
        // happens before the click does anything else — by the time a row has been selected or
        // a drag has begun, the window has to already agree about which pane that was in.
        AddHandler(PointerPressedEvent, OnActivatingPress, RoutingStrategies.Tunnel);

        // Return is the other way to open, and the Actions menu has advertised it since M3.
        // On the listing itself rather than as a window hot key, because it must not fire
        // while the path bar has focus — there, Return means "go to what I typed".
        Listing.KeyDown += OnListingKeyDown;
        IconView.KeyDown += OnListingKeyDown;

        // Selection lives in the control; the view model is told about it rather than owning
        // a second copy that would have to be kept in step.
        Listing.SelectionChanged += (_, _) => PublishSelection(Listing);
        IconView.SelectionChanged += (_, _) => PublishSelection(IconView);

        // Icons are requested as containers are realized rather than when the listing is
        // read. Virtualization means only the rows on screen ever ask, which is what keeps
        // opening a directory of a hundred thousand files from rendering a hundred thousand
        // icons nobody will look at.
        Listing.ContainerPrepared += (_, e) => RequestVisuals(e.Container, DetailsIconSize);
        IconView.ContainerPrepared += (_, e) => RequestVisuals(e.Container, GridIconSize);

        foreach (var view in Views)
        {
            // Tunnelling, so a press that lands on empty space is seen before the ListBox
            // decides it was a click on nothing and clears the selection.
            view.AddHandler(PointerPressedEvent, OnViewPointerPressed, RoutingStrategies.Tunnel);
            view.AddHandler(PointerMovedEvent, OnViewPointerMoved, RoutingStrategies.Tunnel);
            view.AddHandler(PointerReleasedEvent, OnViewPointerReleased, RoutingStrategies.Tunnel);

            DragDrop.SetAllowDrop(view, true);
            view.AddHandler(DragDrop.DragOverEvent, OnViewDragOver);
            view.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
            view.AddHandler(DragDrop.DropEvent, OnViewDrop);
        }

        _dragTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DragTickMs) };
        _dragTimer.Tick += OnDragTick;
    }

    /// <summary>Puts back what the tab had selected before it was switched away from.</summary>
    /// <remarks>
    /// Avalonia's TabControl has one content presenter and rebuilds it on every switch, so
    /// this control is new each time a tab comes back and the selection it had is only
    /// remembered on the tab. Deferred by a post because the items have not been bound yet
    /// when the data context arrives.
    /// </remarks>
    private MainWindowViewModel? _wired;

    /// <summary>Follows the window's "everything on screen is stale" signal.</summary>
    private void Rewire()
    {
        if (ReferenceEquals(_wired, Model))
            return;
        if (_wired is { } previous)
            previous.VisualsInvalidated -= RefreshVisuals;
        _wired = Model;
        if (_wired is { } current)
            current.VisualsInvalidated += RefreshVisuals;
    }

    private void RestoreSelection()
    {
        if (Pane is not { Selection.Count: > 0 } pane)
            return;

        var wanted = pane.Selection;
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(Pane, pane))
                return;
            var view = pane.IsIconView ? IconView : Listing;
            if (view.SelectedItems is not { } selected)
                return;
            selected.Clear();
            foreach (var row in wanted)
                selected.Add(row);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The pane this listing shows.</summary>
    private PaneViewModel? Pane => DataContext as PaneViewModel;

    /// <summary>The window behind it, which owns the icons, the drop policy and the queue.</summary>
    private MainWindowViewModel? Model => Pane?.Tab?.Window;

    private ListBox[] Views => [Listing, IconView];

    private void PublishSelection(ListBox source)
    {
        if (Pane is not { } pane)
            return;

        var selection = source.SelectedItems?.OfType<FileEntryViewModel>().ToArray() ?? [];
        pane.Selection = selection;

        // Only the active pane speaks for the window. Without this, merely rebuilding the
        // other listing's selection would redirect Cut, Delete and Paste to a directory
        // nobody clicked on.
        if (pane.IsActive && pane.Tab?.Window is { } model)
            model.Selection = selection;
    }

    /// <summary>Asks for a realized row's type icon and, if it can have one, its preview.</summary>
    /// <remarks>
    /// Both, and in that order. The icon arrives in milliseconds from a per-type cache and the
    /// thumbnail may take a hundred, so the row shows its type immediately and sharpens into
    /// the file itself rather than sitting blank while a photograph decodes.
    /// </remarks>
    private void RequestVisuals(Control container, int size)
    {
        if (Model is not { } model || container.DataContext is not FileEntryViewModel row)
            return;

        row.EnsureIcon(model.Icons, size);
        if (model.ThumbnailsEnabled)
            row.EnsureThumbnail(model.Thumbnails, ThumbnailCache.SizeFor(size));
    }

    /// <summary>
    /// Re-asks for every realized row, after a setting changed what one should show.
    /// </summary>
    /// <remarks>
    /// Rows ask as they are realized, so the ones already on screen have asked and been
    /// answered. Nothing else would reach them short of re-reading the directory, which for a
    /// hundred thousand entries is not a reasonable price for a checkbox.
    /// </remarks>
    private void RefreshVisuals()
    {
        foreach (var container in Listing.GetRealizedContainers())
            RequestVisuals(container, DetailsIconSize);
        foreach (var container in IconView.GetRealizedContainers())
            RequestVisuals(container, GridIconSize);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Rewire();
        RestoreSelection();
    }

    private void OnListingDoubleTapped(object? sender, TappedEventArgs e) =>
        Open(Listing.SelectedItem, e.KeyModifiers, middleButton: false);

    private void OnIconViewDoubleTapped(object? sender, TappedEventArgs e) =>
        Open(IconView.SelectedItem, e.KeyModifiers, middleButton: false);

    /// <summary>Makes this listing's pane the active one.</summary>
    /// <remarks>
    /// Cheap and unconditional: <see cref="TabViewModel.SetActivePane"/> ignores a pane that is
    /// already active, so this costs a reference comparison on every click in the common case
    /// of a tab that is not split at all.
    /// </remarks>
    private void OnActivatingPress(object? sender, PointerPressedEventArgs e) => Pane?.Activate();

    /// <summary>Return opens the selection, the same as a double-click.</summary>
    /// <remarks>
    /// The modifiers are read from the key event for the same reason they are read from the
    /// tap: Control and Shift decide between a tab, a window and navigating in place, and
    /// there is no reason the keyboard should offer less than the mouse.
    /// </remarks>
    private void OnListingKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Return or Key.Enter) || sender is not ListBox view)
            return;

        Open(view.SelectedItem, e.KeyModifiers, middleButton: false);
        e.Handled = true;
    }

    /// <summary>
    /// Opens a row, letting the router decide whether that is a navigation, a tab or a window.
    /// </summary>
    /// <remarks>
    /// The view reads the gesture and nothing else. Which of the three it becomes is the
    /// navigation mode's business, and keeping that decision out of here is what stops
    /// Classic and Modern from becoming two code paths through the whole application.
    /// </remarks>
    private void Open(object? selected, KeyModifiers modifiers, bool middleButton)
    {
        if (Model is not { } model || selected is not FileEntryViewModel row)
            return;

        var intent = NavigationPolicy.IntentFor(
            modifiers.HasFlag(KeyModifiers.Control),
            modifiers.HasFlag(KeyModifiers.Shift),
            middleButton);
        _ = model.OpenAsync(row, intent);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_wired is { } window)
        {
            window.VisualsInvalidated -= RefreshVisuals;
            _wired = null;
        }
        EndDrag();
        base.OnDetachedFromVisualTree(e);
    }

    // --- pointer: rubber band and the start of a drag --------------------

    private VirtualizingIconPanel? Grid => IconView.GetVisualDescendants().OfType<VirtualizingIconPanel>().FirstOrDefault();

    private void OnViewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _bandOrigin = null;
        _banding = false;
        _press = null;
        _pressedAt = null;
        _pressedIn = null;
        _pressedSelection = [];

        if (sender is not ListBox view || !e.GetCurrentPoint(view).Properties.IsLeftButtonPressed)
            return;

        if (RowUnder(e.Source) is { } row)
        {
            _press = e;
            _pressedAt = e.GetPosition(view);
            _pressedIn = view;
            // Snapshotted before the ListBox sees the press. See the field's comment: this is
            // what makes a multi-file drag possible at all.
            _pressedSelection = [.. view.SelectedItems?.OfType<FileEntryViewModel>() ?? []];
            if (!_pressedSelection.Contains(row))
                _pressedSelection = [row];
            return;
        }

        if (!ReferenceEquals(view, IconView) || Grid is not { } grid)
            return;

        var point = e.GetPosition(grid);
        // Only empty space starts a band. Pressing an icon is the start of a click or a
        // file drag, and IndexAt deliberately answers -1 for the gaps between cells so the
        // two can be told apart.
        if (grid.Metrics.IndexAt(point.X, point.Y, IconView.ItemCount, grid.Bounds.Width) >= 0)
            return;

        _bandOrigin = point;
    }

    private void OnViewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedAt is { } start && _pressedIn is { } view)
        {
            if (!e.GetCurrentPoint(view).Properties.IsLeftButtonPressed)
            {
                ClearPress();
            }
            else
            {
                var moved = e.GetPosition(view) - start;
                if (Math.Abs(moved.X) >= DragThreshold || Math.Abs(moved.Y) >= DragThreshold)
                {
                    // Cleared before the await, not after: staging a remote selection takes
                    // a while, and a second move event meanwhile would start the drag again
                    // with a press whose serial has already been consumed.
                    var press = _press;
                    var dragging = _pressedSelection;
                    ClearPress();
                    RestoreSelection(view, dragging);
                    if (press is not null)
                        _ = StartDragAsync(press, dragging);
                }
            }
            return;
        }

        if (_bandOrigin is not { } origin || Grid is not { } grid)
            return;

        var point = e.GetPosition(grid);
        if (!_banding)
        {
            if (Math.Abs(point.X - origin.X) < BandThreshold && Math.Abs(point.Y - origin.Y) < BandThreshold)
                return;
            _banding = true;
        }

        var rect = new Rect(origin, point).Normalize();
        ShowBand(grid, rect);

        var touched = grid.Metrics
            .IndicesIn(rect.X, rect.Y, rect.Width, rect.Height, IconView.ItemCount, grid.Bounds.Width)
            .ToList();

        // Rebuilt wholesale rather than diffed: the band's contents change on every motion
        // event and the set is bounded by what fits on screen.
        IconView.SelectedItems?.Clear();
        if (IconView.ItemsSource is System.Collections.IList items)
        {
            foreach (var index in touched)
            {
                if (index < items.Count)
                    IconView.SelectedItems?.Add(items[index]);
            }
        }

        e.Handled = true;
    }

    private void OnViewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // A middle click is a whole gesture on its own -- there is no second click to wait
        // for -- so it opens here rather than through the double-tap handler.
        if (e.InitialPressMouseButton == MouseButton.Middle && RowUnder(e.Source) is { } row)
        {
            Open(row, e.KeyModifiers, middleButton: true);
            e.Handled = true;
        }

        ClearPress();
        if (_banding)
            e.Handled = true;
        _bandOrigin = null;
        _banding = false;
        Band.IsVisible = false;
    }

    private void ClearPress()
    {
        _press = null;
        _pressedAt = null;
        _pressedIn = null;
    }

    /// <summary>Puts back the selection the press collapsed, now that it is a drag.</summary>
    private static void RestoreSelection(ListBox view, List<FileEntryViewModel> selection)
    {
        if (selection.Count < 2 || view.SelectedItems is not { } selected)
            return;
        selected.Clear();
        foreach (var row in selection)
            selected.Add(row);
    }

    /// <summary>Draws the band, translated from panel coordinates into the overlay's.</summary>
    /// <remarks>
    /// The overlay does not scroll with the content, so the scroll offset has to come off:
    /// without it the band would be drawn far below the pointer in a scrolled listing.
    /// </remarks>
    private void ShowBand(VirtualizingIconPanel grid, Rect rect)
    {
        var offset = grid.FindAncestorOfType<ScrollViewer>()?.Offset ?? default;
        Canvas.SetLeft(Band, rect.X - offset.X);
        Canvas.SetTop(Band, rect.Y - offset.Y);
        Band.Width = rect.Width;
        Band.Height = rect.Height;
        Band.IsVisible = true;
    }

    // --- drag out ---------------------------------------------------------

    private async Task StartDragAsync(PointerPressedEventArgs trigger, List<FileEntryViewModel> rows)
    {
        if (Model is not { } model || rows.Count == 0)
            return;
        if (TopLevel.GetTopLevel(this) is not { StorageProvider: { } storage })
            return;

        IReadOnlyList<string> paths;
        try
        {
            // Local selections come straight back; a remote one is copied down first, because
            // the application receiving the drop is handed files and a file on a share is not
            // one. Offering the smb:// URI instead would be purer and useless.
            paths = await model.StageForDragAsync([.. rows.Select(row => row.Location)]).ConfigureAwait(true);
        }
        catch (FileOperationException ex)
        {
            model.RaiseError(ex.Message);
            return;
        }

        var items = new List<IStorageItem>(paths.Count);
        foreach (var path in paths)
        {
            // The two accessors are separate and neither answers for the other kind, so the
            // choice has to be made here -- a folder asked for as a file comes back null and
            // silently drops out of the drag.
            IStorageItem? item = Directory.Exists(path)
                ? await storage.TryGetFolderFromPathAsync(path).ConfigureAwait(true)
                : await storage.TryGetFileFromPathAsync(path).ConfigureAwait(true);
            if (item is not null)
                items.Add(item);
        }

        if (items.Count == 0)
            return;

        var transfer = new DataTransfer();
        foreach (var item in items)
            transfer.Add(DataTransferItem.Create(DataFormat.File, item));

        // The picture that follows the pointer. An in-process format, so it never reaches the
        // other side of the drag -- a receiving application sees exactly the file list it
        // would have seen without it.
        if (DragImageRenderer.Render(rows, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0) is { } image)
            transfer.Add(DataTransferItem.Create(WaylandDragImage.Format, image));

        // All three are offered so the drop target's modifiers get to choose. What comes back
        // is the only feedback the drag had: the compositor gives no drag image to a client
        // that draws no drag surface, so the cursor shape is the entire channel.
        await DragDrop.DoDragDropAsync(trigger, transfer,
            DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link).ConfigureAwait(true);
    }

    // --- drop in ----------------------------------------------------------

    private void OnViewDragOver(object? sender, DragEventArgs e)
    {
        if (Model is not { } model || sender is not ListBox view)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var sources = DragTransfer.SourcesOf(e);
        if (sources.Count == 0)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var pane = Pane!;
        var row = RowUnder(e.Source);
        // Only a directory is a target of its own. Dragging onto a file means the directory
        // it is in, which is what the listing's background means too.
        var folder = row is { IsDirectory: true } ? row : null;
        Highlight(folder);

        e.DragEffects = DragTransfer.Effects(model.PreviewDrop(sources, folder?.Location ?? pane.Location, DragTransfer.Modifiers(e)));
        e.Handled = true;
        _dragOverSinceLeave = true;

        // Both of these keep working while the pointer is held still, which drag-over events
        // do not, so the timer owns them and this only feeds it a position.
        _dragScroll = view.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        _dragPoint = _dragScroll is null ? default : e.GetPosition(_dragScroll);
        BeginDragTicks();
    }

    private void OnViewDrop(object? sender, DragEventArgs e)
    {
        if (Model is not { } model)
            return;

        var sources = DragTransfer.SourcesOf(e);
        var row = RowUnder(e.Source);
        var target = row is { IsDirectory: true } ? row.Location : Pane!.Location;
        var modifiers = DragTransfer.Modifiers(e);
        EndDrag();

        if (sources.Count == 0)
            return;

        // Posted rather than awaited: the compositor needs the drop handler to return before
        // the transfer finishes, and this one may run for minutes.
        Dispatcher.UIThread.Post(() => _ = model.DropAsync(sources, target, modifiers));
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, RoutedEventArgs e)
    {
        // Deferred to after the current input event: the drag device raises leave and enter
        // for the new element back to back, so a leave with a drag-over behind it was only a
        // move between rows. See the field's comment.
        _dragOverSinceLeave = false;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_dragOverSinceLeave)
                EndDrag();
        }, DispatcherPriority.Background);
    }

    // --- edge auto-scroll and spring-loaded folders -----------------------

    private void BeginDragTicks()
    {
        if (_dragTimer.IsEnabled)
            return;
        _lastTick = DateTimeOffset.UtcNow;
        _dragTimer.Start();
    }

    private void OnDragTick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        var elapsed = now - _lastTick;
        _lastTick = now;

        if (_dragScroll is { } scroll)
        {
            var rate = EdgeScroll.Rate(_dragPoint.Y, scroll.Viewport.Height);
            if (rate != 0)
            {
                var limit = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
                var y = Math.Clamp(scroll.Offset.Y + EdgeScroll.Delta(rate, elapsed), 0, limit);
                scroll.Offset = scroll.Offset.WithY(y);
            }
        }

        // Hovering a folder for the delay opens it, so a drag can reach somewhere that is not
        // already on screen. Without it, drag and drop only works within one directory.
        if (_spring.Update(_dropRow is { IsDirectory: true } row ? row.Location : null, now) is { } open
            && Model is { } model)
        {
            _ = model.NavigateActiveTabAsync(open);
        }
    }

    private void EndDrag()
    {
        _dragTimer.Stop();
        _dragScroll = null;
        _spring.Reset();
        Highlight(null);
    }

    private void Highlight(FileEntryViewModel? row)
    {
        if (ReferenceEquals(_dropRow, row))
            return;
        if (_dropRow is { } previous)
            previous.IsDropTarget = false;
        _dropRow = row;
        if (row is not null)
            row.IsDropTarget = true;
    }

    // --- reading a drag ---------------------------------------------------

    /// <summary>The row an event landed on, or null for the background between them.</summary>
    /// <remarks>
    /// By data context rather than by container type, so it works for both listings: the
    /// details view's rows and the icon view's cells are different controls, and everything
    /// inside an item template inherits the row it is showing.
    /// </remarks>
    private static FileEntryViewModel? RowUnder(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors()
            .OfType<Control>()
            .Select(control => control.DataContext)
            .OfType<FileEntryViewModel>()
            .FirstOrDefault();
}
