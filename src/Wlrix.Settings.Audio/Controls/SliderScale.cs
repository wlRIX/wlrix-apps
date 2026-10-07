// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Wlrix.Settings.Audio.Audio;
using Wlrix.Settings.Audio.Localization;

namespace Wlrix.Settings.Audio.Controls;

/// <summary>Which scale a <see cref="SliderScale"/> draws.</summary>
public enum ScaleKind
{
    /// <summary>A gain slider's: long ticks at 0 and 10 with their numbers, short ones between.</summary>
    Gain,

    /// <summary>The meters' decibel marks, C then 0 down to −60, with a tick each side.</summary>
    Meter,
}

/// <summary>
/// The scale printed beside a slider or between two meters, since the theme's slider has no
/// tick bar. A gain scale lines its ticks up with the thumb's center, which travels half a
/// thumb short of each end of the trough; <see cref="Inset"/> is that half thumb.
/// </summary>
public sealed class SliderScale : Control
{
    public static readonly StyledProperty<ScaleKind> KindProperty =
        AvaloniaProperty.Register<SliderScale, ScaleKind>(nameof(Kind));

    public static readonly StyledProperty<double> InsetProperty =
        AvaloniaProperty.Register<SliderScale, double>(nameof(Inset), 15);

    public static readonly StyledProperty<bool> TicksRightProperty =
        AvaloniaProperty.Register<SliderScale, bool>(nameof(TicksRight));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<SliderScale>();

    static SliderScale()
    {
        AffectsRender<SliderScale>(KindProperty, InsetProperty, TicksRightProperty, ForegroundProperty);
        AffectsMeasure<SliderScale>(KindProperty);
    }

    public ScaleKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>The distance from each end to where the scale's ends are drawn.</summary>
    public double Inset
    {
        get => GetValue(InsetProperty);
        set => SetValue(InsetProperty, value);
    }

    /// <summary>
    /// Put a gain scale's ticks on its right edge, for a scale to the left of its slider.
    /// </summary>
    public bool TicksRight
    {
        get => GetValue(TicksRightProperty);
        set => SetValue(TicksRightProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Kind == ScaleKind.Meter ? 26 : 24, 0);

    public override void Render(DrawingContext context)
    {
        var brush = Foreground ?? Brushes.Black;
        var pen = new Pen(brush, 1);
        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyle.Normal, FontWeight.Normal);
        var size = Math.Max(8, TextElement.GetFontSize(this) - 2);

        if (Kind == ScaleKind.Gain)
            RenderGain(context, pen, brush, typeface, size);
        else
            RenderMeter(context, pen, brush, typeface, size);
    }

    private void RenderGain(DrawingContext context, Pen pen, IBrush brush, Typeface typeface, double size)
    {
        var top = Inset;
        var bottom = Bounds.Height - Inset;
        if (bottom <= top)
            return;

        // Ticks against the slider; the 10 and the 0 beside theirs, away from it.
        var width = Bounds.Width;
        for (var step = 0; step <= (int)VolumeScale.SliderMax; step++)
        {
            var y = Math.Round(bottom - (bottom - top) * step / VolumeScale.SliderMax) + 0.5;
            var major = step is 0 or (int)VolumeScale.SliderMax;
            var length = major ? 7 : step == 5 ? 5 : 3;
            if (TicksRight)
                context.DrawLine(pen, new Point(width - length, y), new Point(width, y));
            else
                context.DrawLine(pen, new Point(0, y), new Point(length, y));
            if (major)
            {
                var text = Text(step.ToString(CultureInfo.CurrentCulture), typeface, size, brush);
                var x = TicksRight ? width - 10 - text.Width : 10;
                context.DrawText(text, new Point(x, y - text.Height / 2));
            }
        }
    }

    private void RenderMeter(DrawingContext context, Pen pen, IBrush brush, Typeface typeface, double size)
    {
        // The meters' wells are as tall as this control, so the marks are spread over the whole
        // height: the clip row at the top, then each decibel mark at the top of its step.
        var height = Bounds.Height - 2;
        var width = Bounds.Width;
        void Mark(string label, double fraction)
        {
            var y = Math.Round(1 + height * (1 - fraction)) + 0.5;
            context.DrawLine(pen, new Point(0, y), new Point(3, y));
            context.DrawLine(pen, new Point(width - 3, y), new Point(width, y));
            var text = Text(label, typeface, size, brush);
            var labelY = Math.Clamp(y - text.Height / 2, 0, Math.Max(0, Bounds.Height - text.Height));
            context.DrawText(text, new Point((width - text.Width) / 2, labelY));
        }

        Mark(Strings.Catalog.Get("Clip"), 1 - VolumeScale.MarkStep / 2);
        for (var i = 0; i < VolumeScale.MeterMarks.Count; i++)
        {
            var db = VolumeScale.MeterMarks[i];
            Mark(db.ToString(CultureInfo.CurrentCulture), VolumeScale.MarkFraction(i));
        }
    }

    private FormattedText Text(string text, Typeface typeface, double size, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);
}
