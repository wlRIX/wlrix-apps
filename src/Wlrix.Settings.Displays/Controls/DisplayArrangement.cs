using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Wlrix.Settings.Displays.Layout;
using Wlrix.Settings.Displays.ViewModels;

namespace Wlrix.Settings.Displays.Controls;

/// <summary>
/// The displays drawn to scale as raised tiles, where they sit in the layout, for picking one
/// and dragging it somewhere else. A bar along one edge of each tile marks where the bottom of
/// that screen is, so a turned display shows which way it was turned.
///
/// The tiles are drawn rather than built from controls, as Wlrix.Desks' DeskPreview is: a
/// tile's position is a scaled copy of the layout, and a drag moves a ghost of it, neither of
/// which a panel of per-item containers would make simpler. Where a dropped tile lands is the
/// view model's business (<see cref="MainWindowViewModel.MoveDisplay"/>); this control only
/// says where it was let go.
/// </summary>
public sealed class DisplayArrangement : Control, ICustomHitTest
{
    public static readonly StyledProperty<IReadOnlyList<DisplayViewModel>?> DisplaysProperty =
        AvaloniaProperty.Register<DisplayArrangement, IReadOnlyList<DisplayViewModel>?>(nameof(Displays));

    public static readonly StyledProperty<DisplayViewModel?> SelectedDisplayProperty =
        AvaloniaProperty.Register<DisplayArrangement, DisplayViewModel?>(
            nameof(SelectedDisplay), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Bound to <see cref="MainWindowViewModel.LayoutVersion"/>; any change redraws.</summary>
    public static readonly StyledProperty<int> LayoutVersionProperty =
        AvaloniaProperty.Register<DisplayArrangement, int>(nameof(LayoutVersion));

    public static readonly StyledProperty<IBrush?> TileBrushProperty =
        AvaloniaProperty.Register<DisplayArrangement, IBrush?>(nameof(TileBrush));

    public static readonly StyledProperty<IBrush?> LightBrushProperty =
        AvaloniaProperty.Register<DisplayArrangement, IBrush?>(nameof(LightBrush));

    public static readonly StyledProperty<IBrush?> DarkBrushProperty =
        AvaloniaProperty.Register<DisplayArrangement, IBrush?>(nameof(DarkBrush));

    public static readonly StyledProperty<IBrush?> OuterLineBrushProperty =
        AvaloniaProperty.Register<DisplayArrangement, IBrush?>(nameof(OuterLineBrush));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<DisplayArrangement, IBrush?>(nameof(Foreground));

    /// <summary>The bottom-edge bar of an unselected tile.</summary>
    public static readonly StyledProperty<IBrush?> BarBrushProperty =
        AvaloniaProperty.Register<DisplayArrangement, IBrush?>(nameof(BarBrush));

    /// <summary>The bottom-edge bar and outline of the selected tile.</summary>
    public static readonly StyledProperty<IBrush?> SelectionBrushProperty =
        AvaloniaProperty.Register<DisplayArrangement, IBrush?>(nameof(SelectionBrush));

    // Room around the layout, so the outermost tiles' bevels are not cut off.
    private const double Inset = 12;
    private const double Bevel = 2;
    private const double BarThickness = 6;
    // How far the pointer has to travel before a press becomes a drag, so a click to select
    // does not nudge the display a pixel.
    private const double DragThreshold = 4;

    private Drag? _drag;

    static DisplayArrangement() =>
        AffectsRender<DisplayArrangement>(
            DisplaysProperty, SelectedDisplayProperty, LayoutVersionProperty, TileBrushProperty,
            LightBrushProperty, DarkBrushProperty, OuterLineBrushProperty, ForegroundProperty,
            BarBrushProperty, SelectionBrushProperty);

    public IReadOnlyList<DisplayViewModel>? Displays
    {
        get => GetValue(DisplaysProperty);
        set => SetValue(DisplaysProperty, value);
    }

    public DisplayViewModel? SelectedDisplay
    {
        get => GetValue(SelectedDisplayProperty);
        set => SetValue(SelectedDisplayProperty, value);
    }

    public int LayoutVersion
    {
        get => GetValue(LayoutVersionProperty);
        set => SetValue(LayoutVersionProperty, value);
    }

    public IBrush? TileBrush
    {
        get => GetValue(TileBrushProperty);
        set => SetValue(TileBrushProperty, value);
    }

    public IBrush? LightBrush
    {
        get => GetValue(LightBrushProperty);
        set => SetValue(LightBrushProperty, value);
    }

    public IBrush? DarkBrush
    {
        get => GetValue(DarkBrushProperty);
        set => SetValue(DarkBrushProperty, value);
    }

    public IBrush? OuterLineBrush
    {
        get => GetValue(OuterLineBrushProperty);
        set => SetValue(OuterLineBrushProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? BarBrush
    {
        get => GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public IBrush? SelectionBrush
    {
        get => GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    /// <summary>A tile was dragged and let go; the position is in layout coordinates.</summary>
    public event Action<DisplayViewModel, int, int>? DisplayDropped;

    // The whole area takes the pointer, so a press on the gap between tiles is not lost to
    // whatever is behind.
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    // ── Geometry ────────────────────────────────────────────────────────────────────────

    /// <summary>How the layout maps onto this control: a uniform scale and an offset.</summary>
    private readonly record struct Fit(double Scale, double OffsetX, double OffsetY)
    {
        public Rect ToView(Box box) =>
            new(OffsetX + box.X * Scale, OffsetY + box.Y * Scale, box.Width * Scale, box.Height * Scale);
    }

    private IEnumerable<DisplayViewModel> Tiles =>
        Displays?.Where(d => d.Enabled && d.Box.Width > 0) ?? [];

    private Fit ComputeFit()
    {
        var boxes = Tiles.Select(d => d.Box).ToList();
        if (boxes.Count == 0)
            return new Fit(1, 0, 0);

        var left = boxes.Min(b => b.X);
        var top = boxes.Min(b => b.Y);
        var width = Math.Max(1, boxes.Max(b => b.Right) - left);
        var height = Math.Max(1, boxes.Max(b => b.Bottom) - top);

        var scale = Math.Min(
            Math.Max(1, Bounds.Width - 2 * Inset) / width,
            Math.Max(1, Bounds.Height - 2 * Inset) / height);
        var offsetX = (Bounds.Width - width * scale) / 2 - left * scale;
        var offsetY = (Bounds.Height - height * scale) / 2 - top * scale;
        return new Fit(scale, offsetX, offsetY);
    }

    private DisplayViewModel? TileAt(Point point, Fit fit) =>
        Tiles.LastOrDefault(d => fit.ToView(d.Box).Contains(point));

    // ── Drawing ─────────────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        // The fit is frozen for the length of a drag, so the other tiles stay still under the
        // one being moved rather than rescaling around it.
        var fit = _drag?.Fit ?? ComputeFit();

        // The selected tile last, so its outline is never painted over by a neighbor's bevel;
        // the dragged one after that, as a ghost above everything.
        foreach (var display in Tiles.OrderBy(d => d == SelectedDisplay))
        {
            if (_drag is { Moved: true } drag && drag.Display == display)
                continue;
            DrawTile(context, display, fit.ToView(display.Box), fit.Scale, 1.0);
        }

        if (_drag is { Moved: true } ghost)
        {
            var rect = fit.ToView(ghost.Display.Box).Translate(ghost.Offset);
            DrawTile(context, ghost.Display, rect, fit.Scale, 0.75);
        }
    }

    private void DrawTile(DrawingContext context, DisplayViewModel display, Rect rect, double scale, double opacity)
    {
        // A pixel of gap between neighbors, so two tiles read as two screens.
        rect = rect.Deflate(1);
        if (rect.Width < 4 || rect.Height < 4)
            return;

        using var _ = context.PushOpacity(opacity);

        var selected = display == SelectedDisplay;
        if (TileBrush is { } face)
            context.FillRectangle(face, rect);
        DrawBevel(context, rect);

        if (Bar(rect, display) is { } bar && (selected ? SelectionBrush : BarBrush) is { } barBrush)
            context.FillRectangle(barBrush, bar);

        if (selected && SelectionBrush is { } selection)
            context.DrawRectangle(new Pen(selection, 2), rect.Deflate(1));
        else if (OuterLineBrush is { } outline)
            context.DrawRectangle(new Pen(outline, 1), rect.Deflate(0.5));

        DrawLabel(context, display, rect.Deflate(Bevel + BarThickness + 2));
    }

    private void DrawBevel(DrawingContext context, Rect rect)
    {
        if (LightBrush is { } light)
        {
            context.FillRectangle(light, new Rect(rect.X, rect.Y, rect.Width, Bevel));
            context.FillRectangle(light, new Rect(rect.X, rect.Y, Bevel, rect.Height));
        }
        if (DarkBrush is { } dark)
        {
            context.FillRectangle(dark, new Rect(rect.X, rect.Bottom - Bevel, rect.Width, Bevel));
            context.FillRectangle(dark, new Rect(rect.Right - Bevel, rect.Y, Bevel, rect.Height));
        }
    }

    /// <summary>
    /// The bar along the screen's bottom edge. A quarter turn one way puts the bottom on the
    /// left, the other on the right; upside down puts it at the top.
    /// </summary>
    private static Rect? Bar(Rect rect, DisplayViewModel display)
    {
        var inner = rect.Deflate(Bevel);
        return ((int)display.Transform & 3) switch
        {
            0 => new Rect(inner.X, inner.Bottom - BarThickness, inner.Width, BarThickness),
            1 => new Rect(inner.X, inner.Y, BarThickness, inner.Height),
            2 => new Rect(inner.X, inner.Y, inner.Width, BarThickness),
            _ => new Rect(inner.Right - BarThickness, inner.Y, BarThickness, inner.Height),
        };
    }

    private void DrawLabel(DrawingContext context, DisplayViewModel display, Rect area)
    {
        if (Foreground is not { } foreground || area.Width <= 0 || area.Height <= 0)
            return;

        var typeface = new Typeface(GetValue(TextElement.FontFamilyProperty));
        var size = GetValue(TextElement.FontSizeProperty);
        var text = string.Join('\n', display.TileLines);

        // Shrink the text to fit a small tile rather than spill out of it, down to a size that
        // is still text; below that, the tile is left blank.
        for (; size >= 7; size -= 1)
        {
            var formatted = new FormattedText(
                text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, foreground)
            {
                TextAlignment = TextAlignment.Center,
                MaxTextWidth = area.Width,
            };
            if (formatted.Width <= area.Width && formatted.Height <= area.Height)
            {
                var origin = new Point(area.X, area.Y + (area.Height - formatted.Height) / 2);
                context.DrawText(formatted, origin);
                return;
            }
        }
    }

    // ── Pointer ─────────────────────────────────────────────────────────────────────────

    private sealed class Drag(DisplayViewModel display, Point start, Fit fit)
    {
        public DisplayViewModel Display { get; } = display;
        public Point Start { get; } = start;
        public Fit Fit { get; } = fit;
        public Vector Offset { get; set; }
        public bool Moved { get; set; }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var fit = ComputeFit();
        var point = e.GetPosition(this);
        if (TileAt(point, fit) is not { } display)
            return;

        SelectedDisplay = display;
        _drag = new Drag(display, point, fit);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is not { } drag)
            return;

        var offset = e.GetPosition(this) - drag.Start;
        if (!drag.Moved && Math.Abs(offset.X) < DragThreshold && Math.Abs(offset.Y) < DragThreshold)
            return;
        drag.Moved = true;
        drag.Offset = offset;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is not { } drag)
            return;

        _drag = null;
        e.Pointer.Capture(null);
        if (drag.Moved)
        {
            var box = drag.Display.Box;
            var x = box.X + (int)Math.Round(drag.Offset.X / drag.Fit.Scale);
            var y = box.Y + (int)Math.Round(drag.Offset.Y / drag.Fit.Scale);
            DisplayDropped?.Invoke(drag.Display, x, y);
        }
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // Lost mid-drag (the window lost focus, say): the tile goes back where it was.
        if (_drag is null)
            return;
        _drag = null;
        InvalidateVisual();
    }
}
