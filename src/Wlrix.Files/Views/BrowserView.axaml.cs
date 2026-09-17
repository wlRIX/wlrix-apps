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

    private static PlaceViewModel? PlaceUnder(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors()
            .OfType<Control>()
            .Select(control => control.DataContext)
            .OfType<PlaceViewModel>()
            .FirstOrDefault();

    /// <summary>Clicking a disk opens it, mounting it first if it is not mounted.</summary>
    private void OnDeviceSelected(object? sender, SelectionChangedEventArgs e)
    {
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
    }

    private void OnPlaceSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (Model is not { } model || sender is not ListBox list || list.SelectedItem is not PlaceViewModel place)
            return;

        // Cleared so that clicking the same place again re-navigates. Without this, going
        // Home, browsing away and clicking Home once more does nothing. It also keeps the two
        // sections from both showing a highlighted row.
        list.SelectedItem = null;
        _ = model.NavigateActiveTabAsync(place.Location);
    }
}
