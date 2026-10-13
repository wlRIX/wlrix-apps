// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reactive;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReactiveUI;
using SkiaSharp;
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
    private readonly EditHistory<ImageState> _history = new(ImageState.Identity);
    private readonly List<IDisposable> _subscriptions = [];

    private ImageDocument? _document;
    private Bitmap? _displayImage;
    private ViewTransform _transform = ViewTransform.Identity;
    private Enhancement _enhancement = Enhancement.Identity;
    private ViewTool _tool = ViewTool.Hand;
    private bool _isBusy;
    private string? _busyText;
    private bool _canUndo;
    private bool _canRedo;
    private bool _isModified;
    private Size _viewportSize;

    /// <summary>Where the pointer last was over the well, in view coordinates.</summary>
    /// <remarks>
    /// Kept so the close-up can follow a change of view with the pointer standing still: a
    /// zoom drag moves the image under it without moving it.
    /// </remarks>
    private Point? _pointer;

    /// <summary>Whether a press in the Properties dialog is still down; see <see cref="BeginGesture"/>.</summary>
    private bool _gesture;

    // The preview. The document's own pixels are drawn while the enhancement is neutral, so
    // their display copy and histogram are kept from loading rather than recomputed each time
    // a setting is brought back to its default.
    private Bitmap? _originalDisplay;
    private Histogram? _originalHistogram;

    /// <summary>The enhanced pixels, or <c>null</c> while the enhancement is neutral.</summary>
    private SKBitmap? _adjusted;

    private bool _previewRunning;
    private bool _previewPending;

    /// <summary>
    /// A document replaced while a preview was still reading its pixels. It is disposed once
    /// that preview finishes rather than out from under it.
    /// </summary>
    private ImageDocument? _retired;

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
        Center = ReactiveCommand.Create(() => Apply(State with { View = Transform.WithPan(default) }), hasImage);
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
        ResetEnhancements = ReactiveCommand.Create(
            () => Apply(State with { Enhance = Enhancement.Identity }), hasImage);
        ResetGeometry = ReactiveCommand.Create(
            () => Apply(State with { View = Transform.WithoutOrientation.WithZoom(1) }), hasImage);

        Properties = new PropertiesViewModel(this);

        // A command that throws has nowhere to put the exception and rethrows it on the
        // scheduler, taking the process with it. Everything reports here instead.
        foreach (var command in new IHandleObservableErrors[]
                 {
                     Open, Save, SaveAs, Quit, Center, Undo, Redo, ShowProperties, ShowDetail,
                     ShowHistogram, ShowInfo, ShowAbout, ResetEnhancements, ResetGeometry,
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

    /// <summary>Puts every Enhance setting back to neutral, as one undoable step.</summary>
    public ReactiveCommand<Unit, Unit> ResetEnhancements { get; }

    /// <summary>Takes out the rotation and reflections and zooms to 100%, as one undoable step.</summary>
    public ReactiveCommand<Unit, Unit> ResetGeometry { get; }

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

    /// <summary>The Detail dialog's state, kept up to date whether or not the dialog is open.</summary>
    public DetailViewModel Detail { get; } = new();

    /// <summary>The Properties dialog's state: a view onto <see cref="Transform"/> and <see cref="Enhancement"/>.</summary>
    public PropertiesViewModel Properties { get; }

    public ImageDocument? Document
    {
        get => _document;
        private set
        {
            this.RaiseAndSetIfChanged(ref _document, value);
            this.RaisePropertyChanged(nameof(HasImage));
            this.RaisePropertyChanged(nameof(Title));
            RaiseInfo();
            UpdateDetail();
        }
    }

    public bool HasImage => Document is not null;

    /// <summary>What the canvas draws: the enhanced pixels, premultiplied.</summary>
    public Bitmap? DisplayImage
    {
        get => _displayImage;
        private set => this.RaiseAndSetIfChanged(ref _displayImage, value);
    }

    /// <summary>
    /// How the image is shown right now. The canvas writes this on every pointer move of a
    /// drag. Only <see cref="Commit"/> records a step.
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
            UpdateDetail();
        }
    }

    /// <summary>
    /// What the Enhance tab is set to right now. Like <see cref="Transform"/>, it changes live
    /// during a thumbwheel drag and only <see cref="Commit"/> records a step.
    /// </summary>
    public Enhancement Enhancement
    {
        get => _enhancement;
        set
        {
            if (value == _enhancement)
                return;
            this.RaiseAndSetIfChanged(ref _enhancement, value);
            SchedulePreview();
        }
    }

    /// <summary>The live state, as a step would record it.</summary>
    public ImageState State => new(Transform, Enhancement);

    /// <summary>The pixels as they are shown: enhanced, but neither turned nor mirrored.</summary>
    public SKBitmap? AdjustedPixels => _adjusted ?? Document?.Pixels;

    /// <summary>The size of the image well, which the view keeps up to date.</summary>
    /// <remarks>Used to pick the zoom a newly opened image starts at.</remarks>
    public Size ViewportSize
    {
        get => _viewportSize;
        set
        {
            _viewportSize = value;
            UpdateDetail();
        }
    }

    /// <summary>The pointer moved over the well, to <paramref name="point"/> in view coordinates.</summary>
    public void PointerMoved(Point point)
    {
        _pointer = point;
        UpdateDetail();
    }

    /// <summary>Recomputes what Detail shows from the pointer, the view and the image.</summary>
    private void UpdateDetail()
    {
        if (Document is not { } document)
        {
            Detail.Clear();
            return;
        }

        var image = new Size(document.Width, document.Height);
        var transform = Transform;

        // The view's top-left corner in image coordinates, and how much of the image the view
        // spans. Both in image pixels, so they read against Position in Image.
        Detail.SetView(ImageCanvas.ViewToImage(default, image, ViewportSize, transform),
            new Size(ViewportSize.Width / transform.ZoomX, ViewportSize.Height / transform.ZoomY));

        if (_pointer is not { } pointer)
            return;

        var at = ImageCanvas.ViewToImage(pointer, image, ViewportSize, transform);
        int x = (int)Math.Floor(at.X), y = (int)Math.Floor(at.Y);
        string? pixel = null;
        if (x >= 0 && y >= 0 && x < document.Width && y < document.Height && AdjustedPixels is { } pixels)
        {
            // The enhanced pixel, unpremultiplied: the value Save would write. Alpha only when
            // the image has any, where it is part of the answer.
            var color = pixels.GetPixel(x, y);
            pixel = document.HasAlpha
                ? $"{color.Red}, {color.Green}, {color.Blue}, {color.Alpha}"
                : $"{color.Red}, {color.Green}, {color.Blue}";
        }
        Detail.SetPointer(at, pixel);
    }

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
    /// Whether the image carries a rotation, a reflection or an enhancement that has not been
    /// saved. Pan and zoom never count: Save does not write them, so they are nothing to lose.
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

    public string ZoomText => Transform.IsUniformZoom
        ? $"{Transform.ZoomX * 100:0.#}%"
        : $"{Transform.ZoomX * 100:0.#}% × {Transform.ZoomY * 100:0.#}%";
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

    /// <summary>Records the live state as one undoable step, if it changed anything.</summary>
    /// <remarks>Called when a gesture ends: a drag in the view, or a press in the Properties dialog.</remarks>
    public void Commit()
    {
        if (_history.Commit(State))
            UpdateHistoryState();
    }

    /// <summary>A press began in the Properties dialog.</summary>
    /// <remarks>
    /// Until it is released, changes from the dialog are live but not committed, so a whole
    /// thumbwheel drag, or a spin button held down, is one step. The dialog marks this for the
    /// whole of its content, so every control in it follows the same rule.
    /// </remarks>
    public void BeginGesture() => _gesture = true;

    /// <summary>The press in the Properties dialog was released: whatever it changed is one step.</summary>
    public void EndGesture()
    {
        _gesture = false;
        Commit();
    }

    /// <summary>
    /// Commits a change from the Properties dialog, unless a press is still down there, in
    /// which case its release commits it. A value typed into a field takes this path.
    /// </summary>
    internal void CommitUnlessGesture()
    {
        if (!_gesture)
            Commit();
    }

    /// <summary>Commits <paramref name="state"/> as a step of its own, as Center does.</summary>
    private void Apply(ImageState state)
    {
        Show(state);
        Commit();
    }

    private void Show(ImageState state)
    {
        Transform = state.View;
        Enhancement = state.Enhance;
    }

    private void UndoStep()
    {
        if (_history.Undo())
            Show(_history.Current);
        UpdateHistoryState();
    }

    private void RedoStep()
    {
        if (_history.Redo())
            Show(_history.Current);
        UpdateHistoryState();
    }

    private void UpdateHistoryState()
    {
        CanUndo = _history.CanUndo;
        CanRedo = _history.CanRedo;
        IsModified = Document is not null && _history.Current.IsModified;
    }

    /// <summary>Brings the preview up to date with <see cref="Enhancement"/>.</summary>
    /// <remarks>
    /// One preview runs at a time, on the pool. A change that arrives while one is running
    /// marks another as wanted, and that one renders whatever the setting is by then. A
    /// thumbwheel drag therefore never queues more than one render behind the current one,
    /// and the last value it reaches is always the one shown.
    /// </remarks>
    private void SchedulePreview()
    {
        if (_previewRunning)
        {
            _previewPending = true;
            return;
        }
        _ = RunPreviewAsync();
    }

    private async Task RunPreviewAsync()
    {
        _previewRunning = true;
        try
        {
            do
            {
                _previewPending = false;
                if (Document is not { } document)
                    break;

                var enhancement = Enhancement;
                if (enhancement.IsIdentity)
                {
                    ShowPreview(null, _originalDisplay, _originalHistogram);
                    continue;
                }

                var (pixels, display, histogram) = await Task.Run(() =>
                {
                    var enhanced = ImageProcessor.Enhance(document.Pixels, enhancement);
                    return (enhanced, DisplayBitmap.From(enhanced), Models.Histogram.FromBitmap(enhanced));
                });

                // Opened over while it ran: these pixels belong to an image that is gone.
                if (!ReferenceEquals(document, Document))
                {
                    pixels.Dispose();
                    display.Dispose();
                    _previewPending = true;
                    continue;
                }

                ShowPreview(pixels, display, histogram);
            } while (_previewPending);
        }
        catch (Exception error)
        {
            _logger.ZLogWarning(error, $"could not render the preview");
        }
        finally
        {
            _previewRunning = false;
            _retired?.Dispose();
            _retired = null;
        }
    }

    /// <summary>Swaps in a rendered preview, disposing what it replaces.</summary>
    private void ShowPreview(SKBitmap? pixels, Bitmap? display, Histogram? histogram)
    {
        var previousPixels = _adjusted;
        var previousDisplay = DisplayImage;

        _adjusted = pixels;
        DisplayImage = display;
        Histogram.Histogram = histogram;
        UpdateDetail();

        if (!ReferenceEquals(previousPixels, pixels))
            previousPixels?.Dispose();
        if (!ReferenceEquals(previousDisplay, display) && !ReferenceEquals(previousDisplay, _originalDisplay))
            previousDisplay?.Dispose();
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
    /// so the reloaded image appears exactly where the edited one was.
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
            var previousOriginal = _originalDisplay;
            var previousAdjusted = _adjusted;

            var start = keep ?? ViewTransform.Identity.WithZoom(
                ImageCanvas.FitZoom(new PixelSize(document.Width, document.Height), ViewportSize));
            _history.Reset(new ImageState(start.WithoutOrientation, Enhancement.Identity));
            _adjusted = null;
            _originalDisplay = display;
            _originalHistogram = histogram;
            Document = document;
            DisplayImage = display;
            Histogram.Histogram = histogram;
            Show(_history.Current);
            UpdateHistoryState();

            previousAdjusted?.Dispose();
            if (!ReferenceEquals(previousDisplay, previousOriginal))
                previousDisplay?.Dispose();
            previousOriginal?.Dispose();
            // A preview may still be reading the old pixels. It disposes them when it is done.
            if (_previewRunning)
                _retired = previousDocument;
            else
                previousDocument?.Dispose();
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
        var state = _history.Current;
        _busyText = Strings.Saving(name);
        IsBusy = true;
        try
        {
            await Task.Run(() => ImageCodec.Save(document, state, path, format));
            _logger.ZLogInformation($"saved {path} as {format} with {state}");
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

        // The edits are in the pixels now. What was saved is what is open from here on, so the
        // next Save goes to the same place, and the history starts again: undoing a turn that
        // is already written into the file would turn it twice.
        await OpenAsync(path, state.View);
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
        _adjusted?.Dispose();
        if (!ReferenceEquals(DisplayImage, _originalDisplay))
            DisplayImage?.Dispose();
        _originalDisplay?.Dispose();
    }
}
