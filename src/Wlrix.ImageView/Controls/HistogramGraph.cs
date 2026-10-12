// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Wlrix.ImageView.Models;

namespace Wlrix.ImageView.Controls;

/// <summary>Draws a <see cref="Models.Histogram"/>: one column per value, darkest on the left.</summary>
/// <remarks>
/// A single channel is drawn as filled columns in that channel's color. RGB is drawn as three
/// outlines over each other, because filling them would hide whichever was drawn first. Each
/// is scaled to its own tallest column, so a channel with one dominant value still shows its
/// shape everywhere else.
/// </remarks>
public sealed class HistogramGraph : Control
{
    public static readonly StyledProperty<Histogram?> HistogramProperty =
        AvaloniaProperty.Register<HistogramGraph, Histogram?>(nameof(Histogram));

    public static readonly StyledProperty<HistogramChannel> ChannelProperty =
        AvaloniaProperty.Register<HistogramGraph, HistogramChannel>(nameof(Channel));

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<HistogramGraph>();

    /// <summary>The luminance columns. Red, green and blue use their own colors.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextBlock.ForegroundProperty.AddOwner<HistogramGraph>();

    private static readonly IBrush RedBrush = new SolidColorBrush(Color.FromRgb(0xff, 0x40, 0x40));
    private static readonly IBrush GreenBrush = new SolidColorBrush(Color.FromRgb(0x40, 0xe0, 0x40));
    private static readonly IBrush BlueBrush = new SolidColorBrush(Color.FromRgb(0x50, 0x70, 0xff));

    static HistogramGraph()
    {
        AffectsRender<HistogramGraph>(HistogramProperty, ChannelProperty, BackgroundProperty,
            ForegroundProperty);
        ClipToBoundsProperty.OverrideDefaultValue<HistogramGraph>(true);
    }

    public Histogram? Histogram
    {
        get => GetValue(HistogramProperty);
        set => SetValue(HistogramProperty, value);
    }

    public HistogramChannel Channel
    {
        get => GetValue(ChannelProperty);
        set => SetValue(ChannelProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (Background is { } background)
            context.FillRectangle(background, bounds);

        if (Histogram is not { PixelCount: > 0 } histogram)
            return;

        if (Channel == HistogramChannel.Rgb)
        {
            DrawOutline(context, histogram.Red, RedBrush, bounds);
            DrawOutline(context, histogram.Green, GreenBrush, bounds);
            DrawOutline(context, histogram.Blue, BlueBrush, bounds);
            return;
        }

        var brush = Channel switch
        {
            HistogramChannel.Red => RedBrush,
            HistogramChannel.Green => GreenBrush,
            HistogramChannel.Blue => BlueBrush,
            _ => Foreground ?? Brushes.White,
        };
        DrawColumns(context, histogram.Counts(Channel), brush, bounds);
    }

    private static void DrawColumns(DrawingContext context, int[] counts, IBrush brush, Rect bounds)
    {
        var tallest = counts.Max();
        if (tallest == 0)
            return;

        var width = bounds.Width / counts.Length;
        for (var value = 0; value < counts.Length; value++)
        {
            var height = bounds.Height * counts[value] / tallest;
            // At least a hairline wide, so a narrow window does not lose columns to rounding.
            context.FillRectangle(brush, new Rect(value * width, bounds.Height - height,
                Math.Max(width, 1), height));
        }
    }

    private static void DrawOutline(DrawingContext context, int[] counts, IBrush brush, Rect bounds)
    {
        var tallest = counts.Max();
        if (tallest == 0)
            return;

        var width = bounds.Width / counts.Length;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            for (var value = 0; value < counts.Length; value++)
            {
                var point = new Point((value + 0.5) * width,
                    bounds.Height - bounds.Height * counts[value] / tallest);
                if (value == 0)
                    path.BeginFigure(point, isFilled: false);
                else
                    path.LineTo(point);
            }
            path.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, new Pen(brush, 1.5), geometry);
    }
}
