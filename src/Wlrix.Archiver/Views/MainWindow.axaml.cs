using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Wayland;
using Wlrix.Archiver.Localization;
using Wlrix.Archiver.ViewModels;
using Wlrix.Avalonia.Dialogs;

namespace Wlrix.Archiver.Views;

public partial class MainWindow : Window
{
    /// <summary>How far the pointer must travel with the button down before it is a drag.</summary>
    /// <remarks>
    /// Without a threshold, every click that wobbles a pixel starts a drag — which here means
    /// extracting the selection to a scratch directory, so the cost of getting it wrong is real
    /// work rather than a stray cursor.
    /// </remarks>
    private const double DragThreshold = 6;

    /// <summary>Shown over the listing while a drag's entries are being extracted.</summary>
    private static readonly Cursor WaitCursor = new(StandardCursorType.Wait);

    private string? _openOnStartup;
    private Point? _pressedAt;

    /// <summary>
    /// The press that may become a drag.
    /// </summary>
    /// <remarks>
    /// Held rather than the move event that crosses the threshold, and not only because
    /// <c>DoDragDropAsync</c> asks for a <see cref="PointerPressedEventArgs"/>: the Wayland
    /// backend reads the input serial off it, and <c>wl_data_device.start_drag</c> is validated
    /// against the serial of the press that began the implicit grab. Any other serial is
    /// refused, which looks exactly like drag-and-drop not working.
    /// </remarks>
    private PointerPressedEventArgs? _press;

    /// <summary>What was selected when the press landed, before the tree collapsed it.</summary>
    private List<EntryNodeViewModel> _pressedRows = [];

    /// <summary>Whether the button that pressed is still down.</summary>
    private bool _buttonHeld;

    /// <summary>Whether a drag that started here is in flight.</summary>
    private bool _dragging;

    public MainWindow()
    {
        // No hand-written `InitializeComponent()` here on purpose. The generated one
        // (InitializeComponent(bool loadXaml = true)) loads the XAML *and* assigns the x:Name
        // fields; a parameterless override wins overload resolution, leaving EntryTree null.
        InitializeComponent();

        EntryTree.SelectionChanged += OnTreeSelectionChanged;
        EntryTree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        EntryTree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel);
        EntryTree.AddHandler(PointerReleasedEvent, OnTreePointerReleased, RoutingStrategies.Tunnel);
        EntryTree.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        EntryTree.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>Whether the external 7z tool was found; the About box says so when it was not.</summary>
    public bool SevenZipAvailable { get; set; }

    /// <summary>Opens <paramref name="path"/> once the window is up.</summary>
    /// <remarks>
    /// Deferred rather than done here: the view model raises errors through an event this window
    /// only subscribes to in <see cref="OnOpened"/>, so a failure during construction would have
    /// nowhere to go.
    /// </remarks>
    public void OpenOnStartup(string path) => _openOnStartup = path;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is not MainWindowViewModel model)
            return;

        model.OpenFileRequested += OnOpenFileRequested;
        model.DestinationRequested += OnDestinationRequested;
        model.ConfirmRequested += OnConfirmRequested;
        model.PasswordRequested += OnPasswordRequested;
        model.NewArchiveRequested += OnNewArchiveRequested;
        model.NewPasswordRequested += OnNewPasswordRequested;
        model.ErrorRaised += OnErrorRaised;
        model.AboutRequested += OnAboutRequested;
        model.ExitRequested += Close;

        if (_openOnStartup is { } path)
        {
            _openOnStartup = null;
            _ = model.OpenAsync(path);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is MainWindowViewModel model)
        {
            model.OpenFileRequested -= OnOpenFileRequested;
            model.DestinationRequested -= OnDestinationRequested;
            model.ConfirmRequested -= OnConfirmRequested;
            model.PasswordRequested -= OnPasswordRequested;
            model.NewArchiveRequested -= OnNewArchiveRequested;
            model.NewPasswordRequested -= OnNewPasswordRequested;
            model.ErrorRaised -= OnErrorRaised;
            model.AboutRequested -= OnAboutRequested;
            model.ExitRequested -= Close;
            model.Dispose();
        }

        base.OnClosed(e);
    }

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel model)
            model.SelectionChanged(EntryTree.SelectedItems.OfType<EntryNodeViewModel>());
    }

    /// <summary>The folder holding the open archive, where every picker here starts.</summary>
    /// <remarks>
    /// It is the folder the user was just working in. Null, which the portal takes as home,
    /// when nothing is open or that folder has gone away since.
    /// </remarks>
    private async Task<IStorageFolder?> ArchiveFolderAsync()
    {
        if (DataContext is not MainWindowViewModel { Archive.Path: { } archivePath }
            || Path.GetDirectoryName(archivePath) is not { Length: > 0 } directory
            || !Directory.Exists(directory))
        {
            return null;
        }

        return await StorageProvider.TryGetFolderFromPathAsync(directory);
    }

    private async Task<string?> OnOpenFileRequested()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.OpenTitle,
            AllowMultiple = false,
            SuggestedStartLocation = await ArchiveFolderAsync(),
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.FilterArchives)
                {
                    Patterns =
                    [
                        "*.zip", "*.jar", "*.tar", "*.tar.gz", "*.tgz", "*.tar.bz2", "*.tbz2",
                        "*.tar.xz", "*.txz", "*.tar.zst", "*.gz", "*.bz2", "*.xz", "*.lz",
                        "*.7z", "*.rar",
                    ],
                },
                new FilePickerFileType(Strings.FilterAllFiles) { Patterns = ["*"] },
            ],
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private async Task<string?> OnDestinationRequested()
    {
        // Extracting next to the archive is the common case.
        var start = await ArchiveFolderAsync();
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.ExtractTitle,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    /// <summary>Asks where a new archive should go, and what kind it should be.</summary>
    /// <remarks>
    /// The type list is what decides the format: the archiver identifies a format from the name,
    /// so picking "7z archive" and getting <c>.7z</c> appended is the whole mechanism. Zip is
    /// first because it is the one every other system can open.
    /// </remarks>
    private async Task<string?> OnNewArchiveRequested()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.NewTitle,
            SuggestedStartLocation = await ArchiveFolderAsync(),
            DefaultExtension = "zip",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("Zip") { Patterns = ["*.zip"] },
                new FilePickerFileType("7z") { Patterns = ["*.7z"] },
                new FilePickerFileType("Tar") { Patterns = ["*.tar"] },
                new FilePickerFileType("Tar + gzip") { Patterns = ["*.tar.gz"] },
                new FilePickerFileType("Tar + xz") { Patterns = ["*.tar.xz"] },
                new FilePickerFileType("Tar + zstd") { Patterns = ["*.tar.zst"] },
            ],
        });

        return file?.TryGetLocalPath();
    }

    /// <summary>
    /// Offers a password for a new archive. Null cancels; empty means no encryption.
    /// </summary>
    private Task<NewArchiveEncryption?> OnNewPasswordRequested(string name) =>
        PasswordDialog.ShowForNewAsync(this, Strings.NewPasswordTitle,
            Strings.NewPasswordPrompt(name));

    private Task<string?> OnPasswordRequested(string name, bool retry) =>
        PasswordDialog.ShowAsync(this, Strings.PasswordTitle, Strings.PasswordPrompt(name, retry));

    private async Task<bool> OnConfirmRequested(string message)
    {
        var answer = await MessageDialog.ShowAsync(this, DialogType.Question, message,
            buttons: DialogButtons.OkCancel, title: Strings.ConfirmRemoveTitle);
        return answer == DialogResult.Ok;
    }

    private void OnErrorRaised(string message) => _ = MessageDialog.ShowAsync(this,
        DialogType.Error, message, buttons: DialogButtons.Ok, title: Strings.ErrorTitle);

    private void OnAboutRequested()
    {
        var version = GetType().Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        _ = MessageDialog.ShowAsync(this, DialogType.Information,
            Strings.About(version, SevenZipAvailable), buttons: DialogButtons.Ok,
            title: Strings.AboutTitle);
    }

    // ---- Drag out -------------------------------------------------------------------------

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressedAt = null;
        _press = null;
        _pressedRows = [];
        if (!e.GetCurrentPoint(EntryTree).Properties.IsLeftButtonPressed)
            return;

        // A press on the empty well below the rows, or on the scrollbar, carries nothing.
        if ((e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)
                ?.DataContext is not EntryNodeViewModel row)
        {
            return;
        }

        _pressedAt = e.GetPosition(EntryTree);
        _press = e;
        _buttonHeld = true;

        // Snapshotted on the way past in the tunnel, before the tree sees the press: a press on
        // an already-selected row collapses the selection to that row, so by the time the
        // pointer has moved far enough to be a drag there would be one entry left to carry.
        var selected = EntryTree.SelectedItems.OfType<EntryNodeViewModel>().ToList();
        _pressedRows = selected.Contains(row) ? selected : [row];
    }

    private void OnTreePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pressedAt = null;
        _press = null;
        _buttonHeld = false;
    }

    private void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedAt is not { } origin
            || !e.GetCurrentPoint(EntryTree).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var delta = e.GetPosition(EntryTree) - origin;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            return;

        // Cleared before the await, not after: staging can take a while on a large selection,
        // and a second move event arriving meanwhile would start the same drag again. The press
        // is good for exactly one start_drag, so it goes with it.
        _pressedAt = null;
        if (_press is { } press)
        {
            _press = null;
            _ = StartDragAsync(press, _pressedRows);
        }
    }

    private async Task StartDragAsync(PointerPressedEventArgs trigger,
        IReadOnlyList<EntryNodeViewModel> rows)
    {
        if (DataContext is not MainWindowViewModel model || rows.Count == 0)
            return;

        // Put back what the press collapsed, so the rows being carried are the rows that look
        // selected -- and stay selected for whatever the user does after the drop.
        if (rows.Count > 1)
        {
            EntryTree.SelectedItems.Clear();
            foreach (var row in rows)
                EntryTree.SelectedItems.Add(row);
        }

        // Entries inside an archive have no path on disk, so they are extracted to a scratch
        // directory and *those* files are what the drop target receives. It has to happen
        // before the drag rather than at the drop: a receiving window reads the file list as
        // soon as the drag enters it, to decide whether to accept, and it reads it on its UI
        // thread. Extracting then would hang the window being dragged over.
        // The drag image's icon renders alongside the extraction rather than after it.
        var icon = model.IconAsync(rows[0].Entry, DragImageRenderer.IconSize);

        IReadOnlyList<string> staged;
        EntryTree.Cursor = WaitCursor;
        try
        {
            staged = await model.StageForDragAsync(rows);
        }
        finally
        {
            EntryTree.Cursor = null;
        }

        // Let go while extracting: the gesture is over, and the compositor would refuse a drag
        // started from a press whose button is already up.
        if (staged.Count == 0 || !_buttonHeld)
            return;

        // Awaited, never blocked on. The storage provider answers on this thread, so waiting
        // for it synchronously here is a deadlock -- which is what froze the window before.
        // And the two accessors are separate: a folder asked for as a file comes back null and
        // silently drops out of the drag.
        var items = new List<IStorageItem>(staged.Count);
        foreach (var path in staged)
        {
            IStorageItem? item = Directory.Exists(path)
                ? await StorageProvider.TryGetFolderFromPathAsync(path)
                : await StorageProvider.TryGetFileFromPathAsync(path);
            if (item is not null)
                items.Add(item);
        }
        if (items.Count == 0)
            return;

        var transfer = new DataTransfer();
        foreach (var item in items)
            transfer.Add(DataTransferItem.Create(DataFormat.File, item));

        // The picture that follows the pointer. An in-process format, so the receiving side
        // sees exactly the file list it would have seen without it.
        if (DragImageRenderer.Render(rows, await icon, DragPalette(), RenderScaling) is { } image)
            transfer.Add(DataTransferItem.Create(WaylandDragImage.Format, image));

        _dragging = true;
        try
        {
            await DragDrop.DoDragDropAsync(trigger, transfer, DragDropEffects.Copy);
        }
        finally
        {
            _dragging = false;
        }
    }

    /// <summary>The brushes the listing draws its glyphs with, for the drag image.</summary>
    private DragImageRenderer.Palette DragPalette()
    {
        IBrush Brush(string key, IBrush fallback) =>
            this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush
                ? brush
                : fallback;

        return new DragImageRenderer.Palette(
            Brush("WlrixFace", Brushes.LightGray),
            Brush("WlrixTopShadow", Brushes.White),
            Brush("WlrixOuterLine", Brushes.Black));
    }

    // ---- Drop in --------------------------------------------------------------------------

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Not our own drag: dropping the entries back onto the listing would add the staged
        // copies to the archive they were just extracted from.
        var accepts = !_dragging
            && DataContext is MainWindowViewModel { CanAdd: true }
            && e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_dragging || DataContext is not MainWindowViewModel { CanAdd: true } model)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var paths = e.DataTransfer.TryGetFiles()?
            .Select(file => file.TryGetLocalPath())
            .OfType<string>()
            .ToList();
        if (paths is not { Count: > 0 })
            return;

        // Posted rather than awaited: the drop handler has to return before the compositor
        // finishes the transfer, and adding to an archive rewrites the whole file.
        Dispatcher.UIThread.Post(() => _ = model.AddAsync(paths));
    }
}
