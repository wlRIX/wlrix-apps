using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wlrix.Files.Core;
using Wlrix.Files.Core.Dnd;
using Wlrix.Files.ViewModels;
using Location = Wlrix.Files.Core.Location;

namespace Wlrix.Files.Views;

/// <summary>Places rail beside the window's tabs.</summary>
/// <remarks>
/// The rail belongs to the window rather than to a tab, so it lives here and each tab's
/// listing lives in its own <see cref="ListingView"/>. The picker will want a rail beside one
/// listing and no tab strip, which is why the two are separate controls rather than one.
/// </remarks>
public partial class BrowserView : UserControl
{
    public BrowserView()
    {
        // No hand-written parameterless InitializeComponent: the generated
        // InitializeComponent(bool) both loads the XAML and assigns the x:Name fields, and an
        // override wins overload resolution, leaving every named control null.
        InitializeComponent();

        PlacesList.SelectionChanged += OnPlaceSelected;
        BookmarksList.SelectionChanged += OnPlaceSelected;
        DevicesList.SelectionChanged += OnDeviceSelected;

        // The two verbs are one item each rather than one item that changes its word, because
        // a menu whose entries move under the pointer is how somebody unmounts a disk they
        // meant to open.
        MountItem.Click += (_, _) => Open(DevicesList.SelectedItem as DeviceViewModel);
        UnmountItem.Click += (_, _) => Unmount(DevicesList.SelectedItem as DeviceViewModel);
        DevicesList.ContextMenu!.Opening += OnDeviceMenuOpening;
        DeviceNewTabItem.Click += (_, _) => OpenInNewTab((DevicesList.SelectedItem as DeviceViewModel)?.Location);

        // The rail's right-click acts on the entry under the pointer, and deliberately does
        // *not* select it: selecting a place navigates to it, so a right-click meaning "open
        // this somewhere else" would have gone there first and then opened a second tab.
        // Recorded on the way down instead, and the menu is refused when the pointer was over
        // the rail's background rather than an entry.
        // Devices are in this too, though they have no context place: the flag it records is
        // what stops a right-click acting on the row, and a disk's row acts harder than a
        // bookmark's -- selecting an unmounted one mounts it.
        foreach (var list in new Control[] { PlacesList, BookmarksList, DevicesList })
            list.AddHandler(PointerPressedEvent, OnRailPointerPressed, RoutingStrategies.Tunnel);

        foreach (var (list, item) in new[]
                 {
                     ((Control)PlacesList, PlaceNewTabItem),
                     (BookmarksList, BookmarkNewTabItem),
                 })
        {
            var owner = list;
            list.ContextMenu!.Opening += (_, e) =>
            {
                // A pointer names its own target; the Menu key has none, so fall back to the
                // entry that has focus. Without this the menu opened from the keyboard would
                // be cancelled and the item would look broken rather than unavailable.
                _contextPlace ??= PlaceUnder(TopLevel.GetTopLevel(owner)?.FocusManager?.GetFocusedElement());
                e.Cancel = _contextPlace is null;
            };
            item.Click += (_, _) => OpenInNewTab(_contextPlace?.Location);
        }

        // Middle-click closes a tab, as it does in every browser and in Dolphin. On the tunnel
        // and marked handled, so the press never reaches the TabItem: otherwise the tab would
        // be selected on the way to being closed, which for a tab that is not the active one
        // means switching to it and then away again.
        TabStrip.AddHandler(PointerPressedEvent, OnTabPointerPressed, RoutingStrategies.Tunnel);

        // The rail takes drops: dragging onto Home files something away without navigating
        // there first, which is most of the point of having the rail. Dropping on the rail
        // itself, rather than on an entry, bookmarks what was dragged.
        foreach (var list in new Control[] { PlacesList, BookmarksList })
        {
            DragDrop.SetAllowDrop(list, true);
            list.AddHandler(DragDrop.DragOverEvent, OnPlacesDragOver);
            list.AddHandler(DragDrop.DropEvent, OnPlacesDrop);
        }
    }

    private MainWindowViewModel? Model => DataContext as MainWindowViewModel;

    /// <summary>The rail entry the context menu was opened over. See where it is recorded.</summary>
    private PlaceViewModel? _contextPlace;

    /// <summary>
    /// True while a right-press is being handled, so the selection it causes does not act.
    /// </summary>
    /// <remarks>
    /// A ListBox selects on any button, including the right one, and both rails act on
    /// SelectionChanged -- so a right-click navigated away, or mounted a disk, before its own
    /// context menu had finished opening. Read and cleared by whichever handler the selection
    /// reaches; set again by the next press either way, so it cannot stay true for long.
    /// </remarks>
    private bool _railRightPress;

    private void OnRailPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _railRightPress = sender is Control control
            && e.GetCurrentPoint(control).Properties.IsRightButtonPressed;

        // The place the menu will act on, which is the entry under the pointer rather than the
        // selected one. Null for a device row, which has no PlaceViewModel and does not use it.
        _contextPlace = _railRightPress
            ? PlaceUnder(e.Source)
            : null;   // so the next keyboard invocation does not reuse a stale one
    }

    /// <summary>Opens a rail entry in a tab of its own.</summary>
    private void OpenInNewTab(Location? location)
    {
        if (Model is { } model && location is not null)
            model.OpenInNewTab(location);
    }

    /// <summary>Applies the remembered rail width once the window has a view model.</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Model is { SidebarWidth: > 0 } model)
            Layout.ColumnDefinitions[0].Width = new GridLength(model.SidebarWidth);
    }

    /// <summary>
    /// Records the rail's new width when the splitter is let go.
    /// </summary>
    /// <remarks>
    /// On completion rather than on every drag event, so one drag writes the session file
    /// once instead of on every frame of it.
    /// </remarks>
    private void OnSidebarResized(object? sender, VectorEventArgs e)
    {
        if (Model is { } model)
            model.SidebarWidth = Layout.ColumnDefinitions[0].ActualWidth;
    }

    private void OnPlacesDragOver(object? sender, DragEventArgs e)
    {
        var sources = DragTransfer.SourcesOf(e);
        if (Model is not { } model || sources.Count == 0)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        // On an entry, this is a file operation into that directory. On the empty space below
        // the entries it is a request to bookmark, which is a Link: nothing is copied and the
        // rail ends up pointing at what was dragged.
        e.DragEffects = PlaceUnder(e.Source) is { } place
            ? DragTransfer.Effects(model.PreviewDrop(sources, place.Location, DragTransfer.Modifiers(e)))
            : DragDropEffects.Link;
        e.Handled = true;
    }

    private void OnPlacesDrop(object? sender, DragEventArgs e)
    {
        var sources = DragTransfer.SourcesOf(e);
        if (Model is not { } model || sources.Count == 0)
            return;

        if (PlaceUnder(e.Source) is not { } place)
        {
            // Dropped on the rail rather than on an entry: each directory dragged becomes a
            // bookmark. Files are ignored — a bookmark to a file is not a thing the rail can
            // usefully do anything with.
            foreach (var source in sources.Where(IsDirectory))
                model.AddBookmark(source);
            e.Handled = true;
            return;
        }

        var modifiers = DragTransfer.Modifiers(e);
        // Posted rather than awaited: the compositor needs the drop handler to return before
        // the transfer finishes, and this one may run for minutes.
        Dispatcher.UIThread.Post(() => _ = model.DropAsync(sources, place.Location, modifiers));
        e.Handled = true;
    }

    /// <summary>Whether a dragged location is a directory. Only directories are bookmarkable.</summary>
    /// <remarks>
    /// Answered by touching the disk rather than by asking the listing, because the drag may
    /// well have come from another application and carry nothing this window has ever listed.
    /// </remarks>
    private static bool IsDirectory(Location location) =>
        location.TryGetLocalPath(out var path) && Directory.Exists(path);

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(TabStrip).Properties.IsMiddleButtonPressed)
            return;
        if (Model is not { } model || TabUnder(e.Source) is not { } tab)
            return;

        e.Handled = true;
        model.CloseTabAt(tab);
    }

    /// <summary>The tab a press landed on, or null if it landed anywhere else.</summary>
    /// <remarks>
    /// Anchored on the TabItem rather than on the first TabViewModel in the ancestry, which
    /// would be wrong in a way that only shows up in use: a tab's *content* carries the same
    /// view model as its header, so any middle-click inside the listing would have closed the
    /// tab it was in. The containers are the strip; the content presenter is not one of them.
    /// </remarks>
    private static TabViewModel? TabUnder(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors()
            .OfType<TabItem>()
            .Select(item => item.DataContext)
            .OfType<TabViewModel>()
            .FirstOrDefault();

    private static PlaceViewModel? PlaceUnder(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors()
            .OfType<Control>()
            .Select(control => control.DataContext)
            .OfType<PlaceViewModel>()
            .FirstOrDefault();

    /// <summary>Clicking a disk opens it, mounting it first if it is not mounted.</summary>
    private void OnDeviceSelected(object? sender, SelectionChangedEventArgs e)
    {
        // Unlike a place, the selection stays: OnDeviceMenuOpening reads SelectedItem to decide
        // which verb applies. Only the opening is skipped -- and it is the more important of
        // the two to skip, because opening an unmounted disk mounts it first, so a right-click
        // meant to reach Unmount could mount something instead.
        if (_railRightPress)
        {
            _railRightPress = false;
            return;
        }

        if (sender is ListBox { SelectedItem: DeviceViewModel device })
            Open(device);
    }

    private void Open(DeviceViewModel? device)
    {
        if (Model is { } model && device is not null)
            _ = model.OpenDeviceAsync(device);
    }

    private void Unmount(DeviceViewModel? device)
    {
        if (Model is { } model && device is not null)
            _ = model.UnmountDeviceAsync(device);
    }

    /// <summary>Shows only the verb that applies to the disk under the pointer.</summary>
    private void OnDeviceMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DevicesList.SelectedItem is not DeviceViewModel device)
        {
            e.Cancel = true;
            return;
        }

        MountItem.IsVisible = !device.IsMounted;
        UnmountItem.IsVisible = device.IsMounted;
        // An unmounted disk has no location to open. Mount is the entry that applies to it,
        // and it is directly above.
        DeviceNewTabItem.IsVisible = device.IsMounted;
    }

    private void OnPlaceSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (Model is not { } model || sender is not ListBox list || list.SelectedItem is not PlaceViewModel place)
            return;

        // Cleared so that clicking the same place again re-navigates. Without this, going
        // Home, browsing away and clicking Home once more does nothing. It also keeps the two
        // sections from both showing a highlighted row.
        list.SelectedItem = null;

        // A right-press selects as any press does, and that is all it may do here: the menu it
        // is opening acts on _contextPlace, and navigating first would take the window
        // somewhere the user was pointing at rather than asking about.
        if (_railRightPress)
        {
            _railRightPress = false;
            return;
        }

        _ = model.NavigateActiveTabAsync(place.Location);
    }
}
