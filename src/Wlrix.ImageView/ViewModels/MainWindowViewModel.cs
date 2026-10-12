// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reactive;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReactiveUI;
using Wlrix.ImageView.Controls;
using Wlrix.ImageView.Localization;
using Wlrix.ImageView.Models;
using Wlrix.ImageView.Services;
using ZLogger;

namespace Wlrix.ImageView.ViewModels;

/// <summary>The window: the open image, how it is being viewed, and the menu and palette commands.</summary>
public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly TransformHistory _history = new(ViewTransform.Identity);
    private readonly List<IDisposable> _subscriptions = [];

    private ImageDocument? _document;
    private Bitmap? _displayImage;
    private ViewTransform _transform = ViewTransform.Identity;
    private ViewTool _tool = ViewTool.Hand;
    private bool _isBusy;
    private string? _busyText;
    private bool _canUndo;
    private bool _canRedo;
    private bool _isModified;

    /// <summary>The design-time constructor. Only the XAML previewer calls this.</summary>
    public MainWindowViewModel()
        : this(NullLogger<MainWindowViewModel>.Instance)
    {
    }

    public MainWindowViewModel(ILogger<MainWindowViewModel> logger)
    {
        _logger = logger;

        var notBusy = this.WhenAnyValue(model => model.IsBusy, busy => !busy);
        var hasImage = this.WhenAnyValue(model => model.HasImage, model => model.IsBusy,
            (image, busy) => image && !busy);
        var canSave = this.WhenAnyValue(model => model.IsModified, model => model.IsBusy,
            (modified, busy) => modified && !busy);

        Open = ReactiveCommand.CreateFromTask(OpenRequestedAsync, notBusy);
        Save = ReactiveCommand.CreateFromTask(SaveRequestedAsync, canSave);
        SaveAs = ReactiveCommand.CreateFromTask(SaveAsRequestedAsync, hasImage);
        Quit = ReactiveCommand.Create(() => ExitRequested?.Invoke());
        Center = ReactiveCommand.Create(() => Apply(_history.Current.WithPan(default)), hasImage);
        Undo = ReactiveCommand.Create(UndoStep,
            this.WhenAnyValue(model => model.CanUndo, model => model.IsBusy, (can, busy) => can && !busy));
        Redo = ReactiveCommand.Create(RedoStep,
            this.WhenAnyValue(model => model.CanRedo, model => model.IsBusy, (can, busy) => can && !busy));

        // The palette's dialogs open with or without an image, so Info and Histogram can stay
        // up while images are opened one after another.
        ShowProperties = ReactiveCommand.Create(() => ToolWindowRequested?.Invoke(ToolWindow.Properties));
        ShowDetail = ReactiveCommand.Create(() => ToolWindowRequested?.Invoke(ToolWindow.Detail));
        ShowHistogram = ReactiveCommand.Create(() => ToolWindowRequested?.Invoke(ToolWindow.Histogram));
        ShowInfo = ReactiveCommand.Create(() => ToolWindowRequested?.Invoke(ToolWindow.Info));
        ShowAbout = ReactiveCommand.Create(() => AboutRequested?.Invoke());

        // A command that throws has nowhere to put the exception and rethrows it on the
        // scheduler, taking the process with it. Everything reports here instead.
        foreach (var command in new IHandleObservableErrors[]
                 {
                     Open, Save, SaveAs, Quit, Center, Undo, Redo, ShowProperties, ShowDetail,
                     ShowHistogram, ShowInfo, ShowAbout,
                 })
        {
            _subscriptions.Add(command.ThrownExceptions.Subscribe(OnCommandFailed));
        }
    }

    public ReactiveCommand<Unit, Unit> Open { get; }
    public ReactiveCommand<Unit, Unit> Save { get; }
    public ReactiveCommand<Unit, Unit> SaveAs { get; }
    public ReactiveCommand<Unit, Unit> Quit { get; }
    public ReactiveCommand<Unit, Unit> Center { get; }
    public ReactiveCommand<Unit, Unit> Undo { get; }
    public ReactiveCommand<Unit, Unit> Redo { get; }
    public ReactiveCommand<Unit, Unit> ShowProperties { get; }
    public ReactiveCommand<Unit, Unit> ShowDetail { get; }
    public ReactiveCommand<Unit, Unit> ShowHistogram { get; }
    public ReactiveCommand<Unit, Unit> ShowInfo { get; }
    public ReactiveCommand<Unit, Unit> ShowAbout { get; }

    /// <summary>Asks the view for an image to open. Null if the user canceled.</summary>
    public event Func<Task<string?>>? OpenFileRequested;

    /// <summary>
    /// Asks the view where to save, given a suggested file name and the format to offer first.
    /// Null if the user canceled.
    /// </summary>
    public event Func<string, ImageFormat, Task<string?>>? SaveFileRequested;

    /// <summary>Asks the view to confirm discarding unsaved work.</summary>
    public event Func<string, Task<bool>>? ConfirmRequested;

    /// <summary>Puts an error in front of the user.</summary>
    public event Action<string>? ErrorRaised;

    public event Action? AboutRequested;

    /// <summary>Raised when the window should close. The view still asks about unsaved work.</summary>
    public event Action? ExitRequested;

    /// <summary>Raised to open, or bring forward, one of the palette's dialogs.</summary>
    public event Action<ToolWindow>? ToolWindowRequested;

    /// <summary>The Histogram dialog's state. Kept here so it follows the open image.</summary>
    public HistogramViewModel Histogram { get; } = new();

    public ImageDocument? Document
    {
        get => _document;
        private set
        {
            this.RaiseAndSetIfChanged(ref _document, value);
            this.RaisePropertyChanged(nameof(HasImage));
            this.RaisePropertyChanged(nameof(Title));
            RaiseInfo();
        }
    }

    public bool HasImage => Document is not null;

    /// <summary>What the canvas draws: the document's pixels, premultiplied.</summary>
    public Bitmap? DisplayImage
    {
        get => _displayImage;
        private set => this.RaiseAndSetIfChanged(ref _displayImage, value);
    }

    /// <summary>
    /// How the image is shown right now. The canvas writes this on every pointer move of a
    /// drag. Only <see cref="CommitTransform"/> records a step.
    /// </summary>
    public ViewTransform Transform
    {
        get => _transform;
        set
        {
            this.RaiseAndSetIfChanged(ref _transform, value);
            this.RaisePropertyChanged(nameof(ZoomText));
            this.RaisePropertyChanged(nameof(RotationText));
            this.RaisePropertyChanged(nameof(PanText));
        }
    }

    /// <summary>The size of the image well, which the view keeps up to date.</summary>
    /// <remarks>Used to pick the zoom a newly opened image starts at.</remarks>
    public Size ViewportSize { get; set; }

    public ViewTool Tool
    {
        get => _tool;
        set
        {
            this.RaiseAndSetIfChanged(ref _tool, value);
            this.RaisePropertyChanged(nameof(IsHandTool));
            this.RaisePropertyChanged(nameof(IsRotateTool));
            this.RaisePropertyChanged(nameof(IsZoomTool));
            this.RaisePropertyChanged(nameof(Status));
        }
    }

    // The three tool buttons are a radio group made of ToggleButtons. Clicking the one already
    // down would raise it and leave no tool selected, so a false write is refused: the change
    // is raised again on the next turn of the dispatcher, which pushes the button back down.
    public bool IsHandTool
    {
        get => Tool == ViewTool.Hand;
        set => SelectTool(ViewTool.Hand, value, nameof(IsHandTool));
    }

    public bool IsRotateTool
    {
        get => Tool == ViewTool.Rotate;
        set => SelectTool(ViewTool.Rotate, value, nameof(IsRotateTool));
    }

    public bool IsZoomTool
    {
        get => Tool == ViewTool.Zoom;
        set => SelectTool(ViewTool.Zoom, value, nameof(IsZoomTool));
    }

    private void SelectTool(ViewTool tool, bool selected, string property)
    {
        if (selected)
            Tool = tool;
        else
            Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(property));
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isBusy, value);
            this.RaisePropertyChanged(nameof(Status));
        }
    }

    /// <summary>The status line: what the left button does, or what is being loaded or saved.</summary>
    public string Status => IsBusy && _busyText is { } busy
        ? busy
        : Tool switch
        {
            ViewTool.Hand => Strings.HintHand,
            ViewTool.Rotate => Strings.HintRotate,
            _ => Strings.HintZoom,
        };

    public string Title => Document is { } document ? Strings.WindowTitle(document.FileName) : Strings.AppName;

    public string EmptyText => Strings.EmptyNoImage;

    public bool CanUndo
    {
        get => _canUndo;
        private set => this.RaiseAndSetIfChanged(ref _canUndo, value);
    }

    public bool CanRedo
    {
        get => _canRedo;
        private set => this.RaiseAndSetIfChanged(ref _canRedo, value);
    }

    /// <summary>
    /// Whether the image carries a rotation that has not been saved. Pan and zoom never count:
    /// Save does not write them, so they are nothing to lose.
    /// </summary>
    public bool IsModified
    {
        get => _isModified;
        private set => this.RaiseAndSetIfChanged(ref _isModified, value);
    }

    // What the Info dialog shows. Kept as text here so the dialog is nothing but labels.
    public string InfoFile => Document?.FileName ?? Strings.NoValue;
    public string InfoFolder => Document?.Folder ?? Strings.NoValue;

    public string InfoFormat => Document switch
    {
        null => Strings.NoValue,
        { Compressed: true } document => Strings.Rle(ImageFormats.DisplayName(document.Format)),
        { Compressed: false } document => Strings.Verbatim(ImageFormats.DisplayName(document.Format)),
        var document => ImageFormats.DisplayName(document.Format),
    };

    public string InfoDimensions => Document is { } document
        ? Strings.Dimensions(document.Width, document.Height)
        : Strings.NoValue;

    public string InfoChannels => Document is { } document ? Strings.Channels(document.Channels) : Strings.NoValue;
    public string InfoDepth => Document is { } document ? document.BitsPerChannel.ToString() : Strings.NoValue;
    public string InfoFrames => Document is { FrameCount: > 1 } document ? Strings.Frames(document.FrameCount) : "1";
    public string InfoFileSize => Document is { } document ? Strings.FileSize(document.FileSize) : Strings.NoValue;
    public string InfoModified => Document is { } document ? document.Modified.ToString("g") : Strings.NoValue;

    public string ZoomText => $"{Transform.Zoom * 100:0.#}%";
    public string RotationText => $"{Transform.Rotation:0.#}°";
    public string PanText => Strings.Pan(Transform.Pan.X, Transform.Pan.Y);

    private void RaiseInfo()
    {
        foreach (var property in new[]
                 {
                     nameof(InfoFile), nameof(InfoFolder), nameof(InfoFormat), nameof(InfoDimensions),
                     nameof(InfoChannels), nameof(InfoDepth), nameof(InfoFrames), nameof(InfoFileSize),
                     nameof(InfoModified),
                 })
        {
            this.RaisePropertyChanged(property);
        }
    }

    /// <summary>Records the gesture the canvas just finished as one undoable step.</summary>
    public void CommitTransform()
    {
        if (_history.Commit(Transform))
            UpdateHistoryState();
    }

    /// <summary>Commits <paramref name="transform"/> as a step of its own, as Center does.</summary>
    private void Apply(ViewTransform transform)
    {
        Transform = transform;
        CommitTransform();
    }

    private void UndoStep()
    {
        if (_history.Undo())
            Transform = _history.Current;
        UpdateHistoryState();
    }

    private void RedoStep()
    {
        if (_history.Redo())
            Transform = _history.Current;
        UpdateHistoryState();
    }

    private void UpdateHistoryState()
    {
        CanUndo = _history.CanUndo;
        CanRedo = _history.CanRedo;
        IsModified = Document is not null && _history.Current.IsRotated;
    }

    /// <summary>
    /// Asks before unsaved work is thrown away. True when there is none, or the user agreed to
    /// lose it.
    /// </summary>
    public async Task<bool> ConfirmDiscardAsync()
    {
        if (!IsModified || Document is not { } document || ConfirmRequested is not { } confirm)
            return true;
        return await confirm(Strings.ConfirmDiscard(document.FileName));
    }

    private async Task OpenRequestedAsync()
    {
        if (!await ConfirmDiscardAsync())
            return;
        if (OpenFileRequested is not { } ask || await ask() is not { } path)
            return;
        await OpenAsync(path);
    }

    /// <summary>Opens <paramref name="path"/>, replacing the current image.</summary>
    /// <param name="keep">
    /// The pan and zoom to keep, rather than starting centered and fitted. Used after a save,
    /// so the reloaded image appears exactly where the rotated one was.
    /// </param>
    public async Task OpenAsync(string path, ViewTransform? keep = null)
    {
        var name = Path.GetFileName(path);
        _busyText = Strings.Loading(name);
        IsBusy = true;
        try
        {
            // Decoding, the premultiplied copy for display, and the histogram all read every
            // pixel. On the pool, so a 40-megapixel photograph does not freeze the window.
            var (document, display, histogram) = await Task.Run(() =>
            {
                var loaded = ImageCodec.Load(path);
                try
                {
                    return (loaded, DisplayBitmap.From(loaded.Pixels), Models.Histogram.FromBitmap(loaded.Pixels));
                }
                catch
                {
                    loaded.Dispose();
                    throw;
                }
            });

            var previousDocument = Document;
            var previousDisplay = DisplayImage;

            var start = keep ?? ViewTransform.Identity.WithZoom(
                ImageCanvas.FitZoom(new PixelSize(document.Width, document.Height), ViewportSize));
            _history.Reset(start.WithRotation(0));
            Transform = _history.Current;
            Document = document;
            DisplayImage = display;
            Histogram.Histogram = histogram;
            UpdateHistoryState();

            previousDocument?.Dispose();
            previousDisplay?.Dispose();
            _logger.ZLogInformation($"opened {path} ({document.Width}x{document.Height}, {document.Format})");
        }
        // Anything at all: a decoder meeting a malformed file can fail in ways nobody listed,
        // and this also runs outside any command, when a path arrives on the command line.
        catch (Exception error)
        {
            _logger.ZLogWarning(error, $"could not open {path}");
            ErrorRaised?.Invoke(Strings.OpenFailed(name, error.Message));
        }
        finally
        {
            IsBusy = false;
            _busyText = null;
        }
    }

    private async Task SaveRequestedAsync()
    {
        if (Document is not { } document)
            return;

        // A GIF, BMP or ICO can be viewed and turned but not written back, so Save asks where
        // to put it instead, which is what Save does in any editor for a file it cannot write.
        var format = ImageFormats.FromPath(document.Path);
        if (!ImageFormats.CanWrite(format))
        {
            await SaveAsRequestedAsync();
            return;
        }

        await SaveToAsync(document.Path, format);
    }

    private async Task SaveAsRequestedAsync()
    {
        if (Document is not { } document || SaveFileRequested is not { } ask)
            return;

        var offered = ImageFormats.CanWrite(document.Format) ? document.Format : ImageFormat.Png;
        var suggested = $"{Path.GetFileNameWithoutExtension(document.Path)}.{ImageFormats.DefaultExtension(offered)}";
        if (await ask(suggested, offered) is not { } path)
            return;

        var format = ImageFormats.FromPath(path);
        if (format == ImageFormat.Unknown)
        {
            ErrorRaised?.Invoke(Strings.UnknownType(Path.GetFileName(path)));
            return;
        }
        if (!ImageFormats.CanWrite(format))
        {
            ErrorRaised?.Invoke(Strings.CannotWrite(ImageFormats.DisplayName(format)));
            return;
        }

        await SaveToAsync(path, format);
    }

    private async Task SaveToAsync(string path, ImageFormat format)
    {
        if (Document is not { } document)
            return;

        var name = Path.GetFileName(path);
        var view = _history.Current;
        _busyText = Strings.Saving(name);
        IsBusy = true;
        try
        {
            await Task.Run(() => ImageCodec.Save(document, view.Rotation, path, format));
            _logger.ZLogInformation($"saved {path} as {format}, rotated {view.Rotation:0.###} degrees");
        }
        catch (Exception error)
        {
            _logger.ZLogWarning(error, $"could not save {path}");
            ErrorRaised?.Invoke(Strings.SaveFailed(name, error.Message));
            return;
        }
        finally
        {
            IsBusy = false;
            _busyText = null;
        }

        // The rotation is in the pixels now. What was saved is what is open from here on, so
        // the next Save goes to the same place, and the history starts again: undoing a turn
        // that is already written into the file would turn it twice.
        await OpenAsync(path, view);
    }

    private void OnCommandFailed(Exception error)
    {
        _logger.ZLogError(error, $"command failed");
        ErrorRaised?.Invoke(error.Message);
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
            subscription.Dispose();
        _subscriptions.Clear();
        Document?.Dispose();
        DisplayImage?.Dispose();
    }
}
