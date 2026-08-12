using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Wlrix.SoftwareManager.Controls;

/// <summary>
/// The IRIX Software Manager disk-space pie: a filled ellipse seen in perspective and extruded
/// downwards, so it reads as a short cylinder rather than a flat circle. Slices start at twelve
/// o'clock and run clockwise — used space, then the space this transaction would add, then
/// what is left free.
///
/// <see cref="Wlrix.Avalonia.Controls.ProgressGauge"/> is the theme's other gauge and is the
/// sheared progress <em>bar</em>; there is nothing round in the theme, so this lives here. It
/// has no dependency on the app and could move into <c>wlrix-avalonia</c> once its API has
/// settled, the way <c>Wlrix.SourcePicker</c> keeps its own <c>LedButton</c> for now.
/// </summary>
public sealed class DiskSpaceGauge : Control
{
    /// <summary>Total capacity. Slices are fractions of this; zero draws an empty outline.</summary>
    public static readonly StyledProperty<double> TotalProperty =
        AvaloniaProperty.Register<DiskSpaceGauge, double>(nameof(Total));

    /// <summary>Space already in use.</summary>
    public static readonly StyledProperty<double> UsedProperty =
        AvaloniaProperty.Register<DiskSpaceGauge, double>(nameof(Used));

    /// <summary>
    /// What the pending transaction would add, drawn as a third slice between used and free so
    /// the user can see the change before starting it. Negative values (a removal) are drawn
    /// back off the used slice instead.
    /// </summary>
    public static readonly StyledProperty<double> NetChangeProperty =
        AvaloniaProperty.Register<DiskSpaceGauge, double>(nameof(NetChange));

    /// <summary>How far the ellipse is squashed: the vertical radius as a fraction of the horizontal one.</summary>
    public static readonly StyledProperty<double> PerspectiveProperty =
        AvaloniaProperty.Register<DiskSpaceGauge, double>(nameof(Perspective), 0.42);

    /// <summary>How far the disk is extruded downwards, in pixels.</summary>
    public static readonly StyledProperty<double> DepthProperty =
        AvaloniaProperty.Register<DiskSpaceGauge, double>(nameof(Depth), 18.0);

    public static readonly StyledProperty<IBrush?> UsedBrushProperty =
        AvaloniaProperty.Register<DiskSpaceGauge, IBrush?>(nameof(UsedBrush));

    public static readonly StyledProperty<IBrush?> FreeBrushProperty =
        AvaloniaProperty.Register<DiskSpaceGauge, IBrush?>(nameof(FreeBrush));

    public static readonly StyledProperty<IBrush?> NetChangeBrushProperty =
        AvaloniaProperty.Register<DiskSpaceGauge, IBrush?>(nameof(NetChangeBrush));

    public static readonly StyledProperty<IBrush?> OutlineBrushProperty =
        AvaloniaProperty.Register<DiskSpaceGauge, IBrush?>(nameof(OutlineBrush));

    static DiskSpaceGauge() =>
        AffectsRender<DiskSpaceGauge>(TotalProperty, UsedProperty, NetChangeProperty,
            PerspectiveProperty, DepthProperty, UsedBrushProperty, FreeBrushProperty,
            NetChangeBrushProperty, OutlineBrushProperty);

    public double Total
    {
        get => GetValue(TotalProperty);
        set => SetValue(TotalProperty, value);
    }

    public double Used
    {
        get => GetValue(UsedProperty);
        set => SetValue(UsedProperty, value);
    }

    public double NetChange
    {
        get => GetValue(NetChangeProperty);
        set => SetValue(NetChangeProperty, value);
    }

    public double Perspective
    {
        get => GetValue(PerspectiveProperty);
        set => SetValue(PerspectiveProperty, value);
    }

    public double Depth
    {
        get => GetValue(DepthProperty);
        set => SetValue(DepthProperty, value);
    }

    public IBrush? UsedBrush
    {
        get => GetValue(UsedBrushProperty);
        set => SetValue(UsedBrushProperty, value);
    }

    public IBrush? FreeBrush
    {
        get => GetValue(FreeBrushProperty);
        set => SetValue(FreeBrushProperty, value);
    }

    public IBrush? NetChangeBrush
    {
        get => GetValue(NetChangeBrushProperty);
        set => SetValue(NetChangeBrushProperty, value);
    }

    public IBrush? OutlineBrush
    {
        get => GetValue(OutlineBrushProperty);
        set => SetValue(OutlineBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        var depth = Math.Max(0, Depth);
        if (bounds.Width <= 2 || bounds.Height - depth <= 2)
            return;

        var radiusX = bounds.Width / 2 - 1;
        var radiusY = Math.Min(radiusX * Perspective, (bounds.Height - depth) / 2 - 1);
        if (radiusX <= 0 || radiusY <= 0)
            return;

        var center = new Point(bounds.Width / 2, (bounds.Height - depth) / 2);
        var pen = OutlineBrush is { } outline ? new Pen(outline) : null;

        // The used slice grows clockwise from twelve o'clock, the change follows it, and free
        // takes the rest. A removal shrinks used and draws the change over the gap it leaves,
        // which is why the change slice is placed by its own start rather than after used.
        var total = Total;
        var used = total > 0 ? Math.Clamp(Used / total, 0, 1) : 0;
        var change = total > 0 ? Math.Clamp(NetChange / total, -1, 1) : 0;
        var changeStart = change >= 0 ? used : Math.Max(0, used + change);
        var changeEnd = Math.Clamp(change >= 0 ? used + change : used, 0, 1);
        var filled = Math.Max(used, changeEnd);

        // Walls first: they sit below the top face and share its boundary arc, so painting the
        // face afterwards keeps that edge crisp instead of letting the wall's own fill bleed
        // over it.
        DrawWall(context, center, radiusX, radiusY, depth, 0, filled, UsedBrush, pen);
        DrawWall(context, center, radiusX, radiusY, depth, filled, 1, FreeBrush, pen);

        DrawSlice(context, center, radiusX, radiusY, 0, used, UsedBrush, pen);
        DrawSlice(context, center, radiusX, radiusY, used, 1, FreeBrush, pen);
        if (changeEnd > changeStart)
            DrawSlice(context, center, radiusX, radiusY, changeStart, changeEnd, NetChangeBrush, pen);
    }

    /// <summary>A wedge of the top face, from <paramref name="from"/> to <paramref name="to"/> turns.</summary>
    private static void DrawSlice(DrawingContext context, Point center, double radiusX,
        double radiusY, double from, double to, IBrush? brush, Pen? pen)
    {
        if (brush is null || to <= from)
            return;

        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            // A full turn is a plain ellipse: starting and ending at twelve o'clock would
            // otherwise leave a hairline seam up the top of the disk.
            var whole = to - from >= 1;
            sink.BeginFigure(whole ? OnEllipse(center, radiusX, radiusY, 0) : center, true);
            foreach (var point in Arc(center, radiusX, radiusY, from, to))
                sink.LineTo(point);
            sink.EndFigure(true);
        }

        context.DrawGeometry(brush, pen, geometry);
    }

    /// <summary>
    /// The extruded side under a wedge. Only the front of the disk has a visible wall — the
    /// back of it is hidden behind the top face — so the wedge is first clipped to the front
    /// half, which runs from three o'clock round to nine.
    /// </summary>
    private static void DrawWall(DrawingContext context, Point center, double radiusX,
        double radiusY, double depth, double from, double to, IBrush? brush, Pen? pen)
    {
        if (brush is null || depth <= 0)
            return;

        var start = Math.Max(from, 0.25);
        var end = Math.Min(to, 0.75);
        if (end <= start)
            return;

        var top = Arc(center, radiusX, radiusY, start, end).ToList();
        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(top[0], true);
            foreach (var point in top.Skip(1))
                sink.LineTo(point);
            for (var i = top.Count - 1; i >= 0; i--)
                sink.LineTo(top[i] + new Vector(0, depth));
            sink.EndFigure(true);
        }

        // The wall is the same color as its face, darkened, so the two read as one solid.
        context.DrawGeometry(Shade(brush), pen, geometry);
    }

    /// <summary>
    /// The ellipse points from <paramref name="from"/> to <paramref name="to"/>, measured in
    /// turns clockwise from twelve o'clock. Flattened to a polyline rather than emitted as an
    /// arc segment: at these radii the difference is invisible, and it sidesteps the
    /// large-arc/sweep flags, which are easy to get subtly wrong and hard to see wrong.
    /// </summary>
    private static IEnumerable<Point> Arc(Point center, double radiusX, double radiusY,
        double from, double to)
    {
        var steps = Math.Max(2, (int)Math.Ceiling((to - from) * 180));
        for (var i = 0; i <= steps; i++)
            yield return OnEllipse(center, radiusX, radiusY, from + (to - from) * i / steps);
    }

    private static Point OnEllipse(Point center, double radiusX, double radiusY, double turns)
    {
        var angle = turns * 2 * Math.PI;
        return new Point(center.X + radiusX * Math.Sin(angle), center.Y - radiusY * Math.Cos(angle));
    }

    /// <summary>A darker version of <paramref name="brush"/>, for the extruded side.</summary>
    private static IBrush Shade(IBrush brush)
    {
        if (brush is not ISolidColorBrush solid)
            return brush;

        var color = solid.Color;
        return new SolidColorBrush(Color.FromArgb(color.A, Scale(color.R), Scale(color.G), Scale(color.B)));

        static byte Scale(byte channel) => (byte)(channel * 55 / 100);
    }
}
