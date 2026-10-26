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

    static SpeedGraph() =>
        AffectsRender<SpeedGraph>(SamplesProperty, FillProperty, StrokeProperty, GridBrushProperty, ForegroundProperty);

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

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return;

        var samples = Samples ?? [];
        var top = Scale(samples.Count == 0 ? 0 : samples.Max());

        // The grid first, so the curve is drawn over it: a line every quarter of the scale,
        // each labeled just below its left end, in the band it opens. The floor has no label:
        // five labels do not fit four bands, and the floor being nothing goes without saying.
        var grid = new Pen(GridBrush ?? Brushes.Gray, 1);
        // The theme's family, inherited like any text's. FontFamily.Default is not guaranteed
        // to resolve to an installed face, and a label that cannot be shaped throws out of the
        // render pass.
        var typeface = new Typeface(TextElement.GetFontFamily(this));
        for (var quarter = 0; quarter <= 4; quarter++)
        {
            var y = Math.Round(height - quarter * (height - 1) / 4.0) - 0.5;
            context.DrawLine(grid, new Point(0, y), new Point(width, y));
            if (quarter == 0)
                continue;

            var label = new FormattedText(
                Strings.ProgressRate(FileSizes.Binary((long)(top * quarter / 4))),
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, Foreground ?? Brushes.Black);
            context.DrawText(label, new Point(3, Math.Max(0, y + 1)));
        }

        if (samples.Count < 2)
            return;

        var step = width / (OperationViewModel.HistoryLength - 1);
        var first = width - (samples.Count - 1) * step;
        var curve = new StreamGeometry();
        using (var draw = curve.Open())
        {
            draw.BeginFigure(new Point(first, height), isFilled: true);
            for (var i = 0; i < samples.Count; i++)
                draw.LineTo(new Point(first + i * step, height - samples[i] / top * (height - 1)));
            draw.LineTo(new Point(width, height));
            draw.EndFigure(isClosed: true);
        }
        context.DrawGeometry(Fill, Stroke is { } stroke ? new Pen(stroke, 1) : null, curve);
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
