using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Rendering;
using Wlrix.Desks.Models;
using Wlrix.Desks.ViewModels;

namespace Wlrix.Desks.Controls;

/// <summary>
/// Draws a desk's windows as miniature framed windows, and hit-tests them: the rectangle
/// under the pointer reports itself as hovered, and clicking one selects it. The rectangles are
/// expected to be pre-scaled (by <see cref="DeskViewModel"/>) into this control's own coordinate
/// space, so it simply fills each one — mirroring the theme's direct-render controls and
/// sidestepping the per-item container positioning an ItemsControl/Canvas would need.
///
/// Each window is drawn as IRIX's overview drew it, measured off an IRIX screen: a one-pixel
/// outline, then a frame one pixel wide down the sides and along the bottom and three pixels
/// deep across the top for the title bar, around the body. The frame is what shows the window's
/// state, with the colors an icon's body takes: gray at rest, white under the pointer, lamp
/// yellow selected. The outline and the body stay as they are.
///
/// Hover and selection are held as window *ids* and bound two-way to shared state, so a window
/// that appears on several desks (anything on the Global desk) lights up in every tile at once,
/// the way IRIX highlighted it.
///
/// A window can be dragged out of the preview. Past a few pixels of movement the preview raises
/// <see cref="WindowDragDeltaEvent"/> as the pointer moves and <see cref="WindowDragEndedEvent"/>
/// when it is let go or the drag is canceled. The pointer stays captured here for the length of
/// the drag, so the events keep coming wherever it goes; drawing the ghost and deciding which
/// desk is under it are the window's business, because a drag crosses from one tile to another.
///
/// Behind the windows is the desk's background, as the desktop would show it on each screen:
/// <see cref="Backgrounds"/>, laid out by <see cref="MainWindowViewModel"/>.
///
/// The rectangle under the pointer also names itself in a tooltip, which is what
/// <c>ov</c>'s <c>--noWindowName</c> switched between titles and application IDs. The tip is set
/// imperatively from the pointer handlers rather than declared per item, because there are no
/// per-item controls here to declare it on.
/// </summary>
public sealed class DeskPreview : Control, ICustomHitTest
{
    public static readonly StyledProperty<IReadOnlyList<PreviewWindow>?> WindowsProperty =
        AvaloniaProperty.Register<DeskPreview, IReadOnlyList<PreviewWindow>?>(nameof(Windows));

    public static readonly StyledProperty<IReadOnlyList<PreviewWallpaper>?> BackgroundsProperty =
        AvaloniaProperty.Register<DeskPreview, IReadOnlyList<PreviewWallpaper>?>(nameof(Backgrounds));

    public static readonly StyledProperty<IBrush?> WindowBrushProperty =
        AvaloniaProperty.Register<DeskPreview, IBrush?>(nameof(WindowBrush));

    public static readonly StyledProperty<IBrush?> WindowBorderBrushProperty =
        AvaloniaProperty.Register<DeskPreview, IBrush?>(nameof(WindowBorderBrush));

    public static readonly StyledProperty<IBrush?> FrameBrushProperty =
        AvaloniaProperty.Register<DeskPreview, IBrush?>(nameof(FrameBrush));

    public static readonly StyledProperty<IBrush?> HoverFrameBrushProperty =
        AvaloniaProperty.Register<DeskPreview, IBrush?>(nameof(HoverFrameBrush));

    public static readonly StyledProperty<IBrush?> SelectionFrameBrushProperty =
        AvaloniaProperty.Register<DeskPreview, IBrush?>(nameof(SelectionFrameBrush));

    public static readonly StyledProperty<long?> HoveredWindowIdProperty =
        AvaloniaProperty.Register<DeskPreview, long?>(
            nameof(HoveredWindowId), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<long?> SelectedWindowIdProperty =
        AvaloniaProperty.Register<DeskPreview, long?>(
            nameof(SelectedWindowId), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<WindowLabel> LabelModeProperty =
        AvaloniaProperty.Register<DeskPreview, WindowLabel>(nameof(LabelMode));

    // The frame's depth across the top, where the title bar is, and down the other three sides.
    private const double TitleDepth = 3;
    private const double FrameWidth = 1;

    /// <summary>A drag of one of this preview's windows moved; bubbles to the window.</summary>
    public static readonly RoutedEvent<WindowDragEventArgs> WindowDragDeltaEvent =
        RoutedEvent.Register<DeskPreview, WindowDragEventArgs>(nameof(WindowDragDelta), RoutingStrategies.Bubble);

    /// <summary>A drag of one of this preview's windows ended, dropped or canceled.</summary>
    public static readonly RoutedEvent<WindowDragEventArgs> WindowDragEndedEvent =
        RoutedEvent.Register<DeskPreview, WindowDragEventArgs>(nameof(WindowDragEnded), RoutingStrategies.Bubble);

    // How far the pointer has to travel before a press on a window becomes a drag, so a click
    // that wobbles still only selects.
    private const double DragThreshold = 4;

    // The press that may become a drag, and once it has, the drag itself.
    private PreviewWindow? _pressedWindow;
    private Point _pressPoint;
    private bool _dragging;

    // Where the pointer was last seen, so a snapshot that moves the windows can re-ask what is
    // under it.
    private Point? _lastPointerPosition;

    // LabelModeProperty is deliberately absent: a label is tipped, never drawn, so changing it
    // has nothing to repaint.
    static DeskPreview() =>
        AffectsRender<DeskPreview>(
            WindowsProperty, BackgroundsProperty, WindowBrushProperty, WindowBorderBrushProperty,
            FrameBrushProperty, HoverFrameBrushProperty, SelectionFrameBrushProperty,
            HoveredWindowIdProperty, SelectedWindowIdProperty);

    public IReadOnlyList<PreviewWindow>? Windows
    {
        get => GetValue(WindowsProperty);
        set => SetValue(WindowsProperty, value);
    }

    /// <summary>The desk's background, one area per screen.</summary>
    public IReadOnlyList<PreviewWallpaper>? Backgrounds
    {
        get => GetValue(BackgroundsProperty);
        set => SetValue(BackgroundsProperty, value);
    }

    /// <summary>Fills a window's body.</summary>
    public IBrush? WindowBrush
    {
        get => GetValue(WindowBrushProperty);
        set => SetValue(WindowBrushProperty, value);
    }

    /// <summary>Outlines every window, whatever its state.</summary>
    public IBrush? WindowBorderBrush
    {
        get => GetValue(WindowBorderBrushProperty);
        set => SetValue(WindowBorderBrushProperty, value);
    }

    /// <summary>A window's frame at rest.</summary>
    public IBrush? FrameBrush
    {
        get => GetValue(FrameBrushProperty);
        set => SetValue(FrameBrushProperty, value);
    }

    /// <summary>The frame of the window under the pointer.</summary>
    public IBrush? HoverFrameBrush
    {
        get => GetValue(HoverFrameBrushProperty);
        set => SetValue(HoverFrameBrushProperty, value);
    }

    /// <summary>The frame of the selected window.</summary>
    public IBrush? SelectionFrameBrush
    {
        get => GetValue(SelectionFrameBrushProperty);
        set => SetValue(SelectionFrameBrushProperty, value);
    }

    /// <summary>The hovered window, shared across every tile (bind two-way).</summary>
    public long? HoveredWindowId
    {
        get => GetValue(HoveredWindowIdProperty);
        set => SetValue(HoveredWindowIdProperty, value);
    }

    /// <summary>The selected window, shared across every tile (bind two-way). Only one at a
    /// time — this is a single id, not a set.</summary>
    public long? SelectedWindowId
    {
        get => GetValue(SelectedWindowIdProperty);
        set => SetValue(SelectedWindowIdProperty, value);
    }

    /// <summary>Whether a hovered window names itself by title, by application ID, or not at all.</summary>
    public WindowLabel LabelMode
    {
        get => GetValue(LabelModeProperty);
        set => SetValue(LabelModeProperty, value);
    }

    public event EventHandler<WindowDragEventArgs>? WindowDragDelta
    {
        add => AddHandler(WindowDragDeltaEvent, value);
        remove => RemoveHandler(WindowDragDeltaEvent, value);
    }

    public event EventHandler<WindowDragEventArgs>? WindowDragEnded
    {
        add => AddHandler(WindowDragEndedEvent, value);
        remove => RemoveHandler(WindowDragEndedEvent, value);
    }

    // Only the window rectangles are painted, and hit testing follows what a control paints, so
    // without this the gaps between windows would not be clickable — and clicking the desk's
    // empty space has to be able to clear the selection.
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    public override void Render(DrawingContext context)
    {
        if (Backgrounds is { } backgrounds)
        {
            foreach (var background in backgrounds)
                DrawBackground(context, background);
        }

        if (Windows is not { } windows)
            return;

        var hovered = HoverFrameBrush ?? FrameBrush;
        var selected = SelectionFrameBrush ?? FrameBrush;

        foreach (var w in windows)
        {
            // Selection wins under the pointer, as it does for icons.
            var frame = w.Id == SelectedWindowId ? selected
                : w.Id == HoveredWindowId ? hovered
                : FrameBrush;

            // Whole pixels, so the one-pixel lines stay one pixel and do not blur across two.
            var x = Math.Round(w.X);
            var y = Math.Round(w.Y);
            var outer = new Rect(x, y, Math.Max(1, Math.Round(w.X + w.W) - x), Math.Max(1, Math.Round(w.Y + w.H) - y));

            // Painted from the outside in, each layer over the last, so a window too small for
            // all of them still shows as much as it has room for.
            Fill(context, WindowBorderBrush, outer);
            var inner = outer.Deflate(1);
            Fill(context, frame, inner);
            Fill(context, WindowBrush, inner.Deflate(new Thickness(FrameWidth, TitleDepth, FrameWidth, FrameWidth)));
        }
    }

    /// <summary>
    /// One screen's background, fitted as <c>wlrix-bg</c> fits it: the color first, under the
    /// letterbox bars and anything transparent, then the picture.
    /// </summary>
    private static void DrawBackground(DrawingContext context, PreviewWallpaper background)
    {
        var area = background.Area;
        using var clip = context.PushClip(area);
        context.FillRectangle(new SolidColorBrush(background.Color), area);
        if (background.Picture is not { } picture || background.Mode == WallpaperMode.Solid)
            return;

        var size = picture.Size;
        var native = background.PictureSize;
        if (size.Width <= 0 || size.Height <= 0)
            return;

        if (background.Mode == WallpaperMode.Tile)
        {
            // From the screen's top-left at the picture's own size, as wlrix-bg repeats it. At a
            // few percent a 100-pixel IRIX pattern is a few pixels across, and its dither reads as
            // the same texture it is on the desktop.
            if (native.Width <= 0 || native.Height <= 0)
                return;
            var tiles = new ImageBrush(picture)
            {
                TileMode = TileMode.Tile,
                Stretch = Stretch.Fill,
                DestinationRect = new RelativeRect(area.X, area.Y, native.Width, native.Height, RelativeUnit.Absolute),
            };
            context.FillRectangle(tiles, area);
            return;
        }

        var destination = background.Mode switch
        {
            WallpaperMode.Center => Centered(area, native),
            WallpaperMode.Fit => Centered(area, size * Math.Min(area.Width / size.Width, area.Height / size.Height)),
            WallpaperMode.Fill => Centered(area, size * Math.Max(area.Width / size.Width, area.Height / size.Height)),
            _ => area,
        };
        context.DrawImage(picture, new Rect(size), destination);
    }

    private static Rect Centered(Rect area, Size size) =>
        new(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2, size.Width, size.Height);

    private static void Fill(DrawingContext context, IBrush? brush, Rect rect)
    {
        if (brush is not null && rect.Width > 0 && rect.Height > 0)
            context.FillRectangle(brush, rect);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _lastPointerPosition = e.GetPosition(this);

        if (_pressedWindow is { } window)
        {
            var travel = _lastPointerPosition.Value - _pressPoint;
            if (!_dragging && Math.Max(Math.Abs(travel.X), Math.Abs(travel.Y)) >= DragThreshold)
            {
                _dragging = true;
                // Nothing under the pointer is being named while it carries a window.
                SetCurrentValue(HoveredWindowIdProperty, null);
                ToolTip.SetTip(this, null);
            }

            if (_dragging)
            {
                RaiseEvent(new WindowDragEventArgs(WindowDragDeltaEvent, e, window, GrabOffset(window)));
                return;
            }
        }

        UpdateHover(_lastPointerPosition.Value);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressedWindow is not { } window || e.InitialPressMouseButton != MouseButton.Left)
            return;

        var dropped = _dragging;
        EndDrag();
        e.Pointer.Capture(null);
        if (dropped)
            RaiseEvent(new WindowDragEventArgs(WindowDragEndedEvent, e, window, GrabOffset(window)));
    }

    // Escape, a grab taken by something else, or the window losing the pointer: the drag ends
    // where it started, with nothing moved.
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_pressedWindow is not { } window)
            return;

        var dragged = _dragging;
        EndDrag();
        if (dragged)
            RaiseEvent(new WindowDragEventArgs(WindowDragEndedEvent, null, window, GrabOffset(window)));
    }

    private void EndDrag()
    {
        _pressedWindow = null;
        _dragging = false;
    }

    // Where in the window it was picked up, which the ghost keeps under the pointer.
    private Vector GrabOffset(PreviewWindow window) => _pressPoint - new Point(window.X, window.Y);

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _lastPointerPosition = null;
        // Avalonia raises this on the tile being left before the move on the tile being
        // entered, so handing the hover over between tiles doesn't clear the new one.
        SetCurrentValue(HoveredWindowIdProperty, null);
        ToolTip.SetTip(this, null);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // The previews are rebuilt on every snapshot, so a window can move out from under a
        // pointer that never moved. Without this the tooltip would go on naming it until the
        // pointer twitched.
        if ((change.Property == WindowsProperty || change.Property == LabelModeProperty)
            && IsPointerOver
            && _lastPointerPosition is { } position)
        {
            UpdateHover(position);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        // Clicking the desk's empty space deselects, matching the desk strip's own behavior.
        var point = e.GetPosition(this);
        var window = WindowAt(point);
        SetCurrentValue(SelectedWindowIdProperty, window?.Id);

        // A minimized window is its icon in the grid, which has no position on the desk to drop
        // it at, so only a window on screen can be picked up.
        if (window is { Minimized: false })
        {
            _pressedWindow = window;
            _pressPoint = point;
            e.Pointer.Capture(this);
        }
    }

    private void UpdateHover(Point point)
    {
        var hovered = WindowAt(point);
        SetCurrentValue(HoveredWindowIdProperty, hovered?.Id);

        // Setting the tip here reaches the tooltip service on this same pointer event: the
        // input manager routes a raw event to the controls before it hands it to the service.
        // Setting it to null over empty space is what closes an open tooltip, and setting it to
        // a different string while one is open replaces the text in place.
        //
        // What that in-place replacement does not do is move the bubble, which is placed where
        // the pointer was when it opened. Crossing straight from one rectangle to another that
        // touches it therefore leaves the tip where it was. Fighting it is worse than living
        // with it -- closing the tooltip by hand leaves the service still considering this
        // control its host, so it never reopens -- and rectangles in a 320x100 preview almost
        // always have a gap between them to pass over.
        ToolTip.SetTip(this, LabelFor(hovered));
    }

    /// <summary>
    /// What a window calls itself, in the mode asked for, falling back to the other name.
    /// </summary>
    /// <remarks>
    /// The fallback is why no localized string is needed here: a window with no title names its
    /// application rather than showing an empty bubble, and one that offers neither gets no
    /// tooltip at all. Titles and application IDs are the application's own text in any case,
    /// never this program's.
    /// </remarks>
    private string? LabelFor(PreviewWindow? window) => window is null ? null : LabelMode switch
    {
        WindowLabel.Title => Preferring(window.Title, window.AppId),
        WindowLabel.AppId => Preferring(window.AppId, window.Title),
        _ => null,
    };

    private static string? Preferring(string first, string second) =>
        first.Length > 0 ? first : second.Length > 0 ? second : null;

    // The topmost window at this point: windows are drawn in order, so the last one wins.
    private PreviewWindow? WindowAt(Point point)
    {
        if (Windows is not { } windows)
            return null;

        for (var i = windows.Count - 1; i >= 0; i--)
        {
            var w = windows[i];
            if (new Rect(w.X, w.Y, w.W, w.H).Contains(point))
                return w;
        }

        return null;
    }
}
