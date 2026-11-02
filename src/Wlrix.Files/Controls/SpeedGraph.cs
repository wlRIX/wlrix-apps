using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Wlrix.Files.Core;
using Wlrix.Files.Localization;
using Wlrix.Files.ViewModels;

namespace Wlrix.Files.Controls;

/// <summary>The last minute of a transfer's speed, as a filled curve over labeled grid lines.</summary>
/// <remarks>
/// After the graph in Plasma's job panel, because a curve answers the question a progress bar
/// cannot: whether the copy is moving <em>now</em>. A share that stalls shows as a cliff to the
/// floor within seconds, where the bar would only stop creeping.
///
/// <para>
/// One sample a second, drawn from the right edge leftward, so a new operation's curve grows in
/// from the right and the newest second is always at the same place. The scale is rounded up to
/// a power of two, so its quarters are round numbers in the binary units the sizes are given in
/// -- 1, 2, 3 and 4 MiB/s, not 1.3, 2.5 and 3.8.
/// </para>
/// </remarks>
public sealed class SpeedGraph : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> SamplesProperty =
        AvaloniaProperty.Register<SpeedGraph, IReadOnlyList<double>?>(nameof(Samples));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<SpeedGraph, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<SpeedGraph, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<SpeedGraph, IBrush?>(nameof(GridBrush));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<SpeedGraph, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<IBrush?> FillForegroundProperty =
        AvaloniaProperty.Register<SpeedGraph, IBrush?>(nameof(FillForeground));

    static SpeedGraph() =>
        AffectsRender<SpeedGraph>(SamplesProperty, FillProperty, StrokeProperty, GridBrushProperty, ForegroundProperty,
            FillForegroundProperty);

    public IReadOnlyList<double>? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>The color of a scale label where it lies on the curve's fill.</summary>
    public IBrush? FillForeground
    {
        get => GetValue(FillForegroundProperty);
        set => SetValue(FillForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return;

        var samples = Samples ?? [];
        var top = Scale(samples.Count == 0 ? 0 : samples.Max());

        // The grid first, so the curve is drawn over it: a line every quarter of the scale.
        var grid = new Pen(GridBrush ?? Brushes.Gray, 1);
        for (var quarter = 0; quarter <= 4; quarter++)
        {
            var y = LineY(quarter, height);
            context.DrawLine(grid, new Point(0, y), new Point(width, y));
        }

        var curve = Curve(samples, top, width, height);
        if (curve is not null)
            context.DrawGeometry(Fill, Stroke is { } stroke ? new Pen(stroke, 1) : null, curve);

        DrawLabels(context, top, height, curve, width);
    }

    /// <summary>Where the line for a quarter of the scale runs: on a pixel, the floor at the bottom.</summary>
    private static double LineY(int quarter, double height) =>
        Math.Round(height - quarter * (height - 1) / 4.0) - 0.5;

    /// <summary>The filled shape of the samples; null until there are two to join.</summary>
    private static StreamGeometry? Curve(IReadOnlyList<double> samples, double top, double width, double height)
    {
        if (samples.Count < 2)
            return null;

        var step = width / (OperationViewModel.HistoryLength - 1);
        var first = width - (samples.Count - 1) * step;
        var curve = new StreamGeometry();
        using var draw = curve.Open();
        draw.BeginFigure(new Point(first, height), isFilled: true);
        for (var i = 0; i < samples.Count; i++)
            draw.LineTo(new Point(first + i * step, height - samples[i] / top * (height - 1)));
        draw.LineTo(new Point(width, height));
        draw.EndFigure(isClosed: true);
        return curve;
    }

    /// <summary>
    /// The scale's labels, each just below the left end of its line, in the band it opens. The
    /// floor has no label: five labels do not fit four bands, and the floor being nothing goes
    /// without saying.
    /// </summary>
    /// <remarks>
    /// Over the curve rather than under it, or a fast transfer paints out the very numbers that
    /// say how fast it is. And in two colors, cut along the curve: the graph's foreground is meant
    /// for its background and <see cref="FillForeground"/> for its fill, and under Gotham white on
    /// the fill's gray is barely there. A label the curve runs through changes color where it
    /// crosses, so it is readable on both sides of the edge.
    /// </remarks>
    private void DrawLabels(DrawingContext context, double top, double height, Geometry? curve, double width)
    {
        // The theme's family, inherited like any text's. FontFamily.Default is not guaranteed
        // to resolve to an installed face, and a label that cannot be shaped throws out of the
        // render pass.
        var typeface = new Typeface(TextElement.GetFontFamily(this));
        var foreground = Foreground ?? Brushes.Black;

        var labels = new List<(FormattedText Text, Point Origin)>(4);
        for (var quarter = 1; quarter <= 4; quarter++)
        {
            var text = new FormattedText(
                Strings.ProgressRate(FileSizes.Binary((long)(top * quarter / 4))),
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, foreground);
            labels.Add((text, new Point(3, Math.Max(0, LineY(quarter, height) + 1))));
        }

        if (curve is null)
        {
            foreach (var (text, origin) in labels)
                context.DrawText(text, origin);
            return;
        }

        // Clipped on both sides rather than the fill's color drawn over the plain one: the
        // edges of one letter under the same letter in another color would leave a fringe.
        var background = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, width, height)), curve);
        using (context.PushGeometryClip(background))
        {
            foreach (var (text, origin) in labels)
                context.DrawText(text, origin);
        }

        using (context.PushGeometryClip(curve))
        {
            foreach (var (text, origin) in labels)
            {
                text.SetForegroundBrush(FillForeground ?? foreground);
                context.DrawText(text, origin);
            }
        }
    }

    /// <summary>The top of the scale: the fastest sample with a little headroom, up to a power of two.</summary>
    /// <remarks>
    /// Never below 1 KiB/s, so an idle or stalled transfer draws a flat line along the floor
    /// rather than dividing by nothing.
    /// </remarks>
    internal static double Scale(double fastest)
    {
        var target = Math.Max(fastest * 1.1, 1024);
        var top = 1024.0;
        while (top < target)
            top *= 2;
        return top;
    }
}
