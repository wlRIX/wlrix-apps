using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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

    private async Task<string?> OnOpenFileRequested()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.OpenTitle,
            AllowMultiple = false,
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
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.ExtractTitle,
            AllowMultiple = false,
        });

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

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
        if (!e.GetCurrentPoint(EntryTree).Properties.IsLeftButtonPressed)
            return;

        _pressedAt = e.GetPosition(EntryTree);
        _press = e;
    }

    private void OnTreePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pressedAt = null;
        _press = null;
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
        // and a second move event arriving meanwhile would start the same drag again.
        _pressedAt = null;
        if (_press is { } press)
            _ = StartDragAsync(press);
    }

    private async Task StartDragAsync(PointerPressedEventArgs trigger)
    {
        if (DataContext is not MainWindowViewModel model)
            return;

        // Entries inside an archive have no path on disk, so the selection is extracted to a
        // scratch directory and *those* files are what the drop target receives.
        var staged = await model.StageSelectionForDragAsync();
        if (staged.Count == 0)
            return;

        var files = staged
            .Select(path => StorageProvider.TryGetFileFromPathAsync(path).GetAwaiter().GetResult())
            .OfType<IStorageItem>()
            .ToArray();
        if (files.Length == 0)
            return;

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(DataFormat.File, files[0]));
        foreach (var file in files.Skip(1))
            transfer.Add(DataTransferItem.Create(DataFormat.File, file));

        await DragDrop.DoDragDropAsync(trigger, transfer, DragDropEffects.Copy);
    }

    // ---- Drop in --------------------------------------------------------------------------

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var accepts = DataContext is MainWindowViewModel { CanAdd: true }
            && e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel model)
            return;

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
