// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Wlrix.Settings.Audio.Audio;

namespace Wlrix.Settings.Audio.Controls;

/// <summary>
/// One channel's level meter, as on the IRIX Audio Panel: a narrow black well with a column
/// of lamps, green at the bottom, yellow near the top and red at the very top, where the clip
/// row is. The scale beside it is <see cref="SliderScale"/>'s business.
///
/// <see cref="Peak"/> is set about 25 times a second. The bar jumps up to a new peak and falls
/// back slowly, as a needle would, and the highest recent peak stays lit for a moment above
/// it. A clip keeps the top row lit for longer, so a one-frame overload is still seen.
/// </summary>
public sealed class LevelMeter : Control
{
    public static readonly StyledProperty<float> PeakProperty =
        AvaloniaProperty.Register<LevelMeter, float>(nameof(Peak));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<LevelMeter, bool>(nameof(IsActive));

    /// <summary>How far the bar falls each second, as a share of the meter's height.</summary>
    private const double FallPerSecond = 0.9;
    private static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ClipHoldTime = TimeSpan.FromSeconds(2);
    private const int Segments = 30;

    private readonly DispatcherTimer _timer;
    private double _level;
    private double _hold;
    private DateTime _holdSet;
    private DateTime _clipSet = DateTime.MinValue;
    private DateTime _lastTick = DateTime.UtcNow;

    static LevelMeter()
    {
        AffectsRender<LevelMeter>(IsActiveProperty);
        PeakProperty.Changed.AddClassHandler<LevelMeter>((meter, _) => meter.OnPeak());
        IsActiveProperty.Changed.AddClassHandler<LevelMeter>((meter, e) => meter.OnActive(e.NewValue is true));
    }

    public LevelMeter()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, OnTick);
        Width = 9;
    }

    /// <summary>The newest peak, 0 to 1; 1 and over is clipping.</summary>
    public float Peak
    {
        get => GetValue(PeakProperty);
        set => SetValue(PeakProperty, value);
    }

    /// <summary>Whether the meter is on. Off, it is a dark well and costs nothing.</summary>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (IsActive)
            _timer.Start();
    }

    private void OnActive(bool active)
    {
        _level = _hold = 0;
        _clipSet = DateTime.MinValue;
        if (active && VisualRoot is not null)
            _timer.Start();
        else
            _timer.Stop();
        InvalidateVisual();
    }

    private void OnPeak()
    {
        if (!IsActive)
            return;
        var now = DateTime.UtcNow;
        var fraction = VolumeScale.MeterFraction(Peak);
        if (fraction > _level)
            _level = fraction;
        if (fraction >= _hold)
        {
            _hold = fraction;
            _holdSet = now;
        }
        if (VolumeScale.IsClipping(Peak))
            _clipSet = now;
        InvalidateVisual();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var seconds = (now - _lastTick).TotalSeconds;
        _lastTick = now;
        var before = (_level, _hold);
        _level = Math.Max(0, _level - FallPerSecond * seconds);
        if (now - _holdSet > HoldTime)
            _hold = Math.Max(_level, _hold - FallPerSecond * seconds);
        if (before != (_level, _hold) || now - _clipSet < ClipHoldTime + TimeSpan.FromMilliseconds(50))
            InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var well = Brush("WlrixOuterLine", Brushes.Black);
        context.FillRectangle(well, bounds);

        var inner = bounds.Deflate(1);
        if (inner.Width <= 0 || inner.Height <= 0)
            return;

        var off = Brush("AudioMeterOff", Brushes.DarkSlateGray);
        var low = Brush("AudioMeterLow", Brushes.Green);
        var high = Brush("AudioMeterHigh", Brushes.Yellow);
        var clip = Brush("AudioMeterClip", Brushes.Red);
        var now = DateTime.UtcNow;
        var clipped = IsActive && now - _clipSet < ClipHoldTime;

        // The clip row is the top mark step; the bar's lamps share the rest.
        var clipHeight = inner.Height * VolumeScale.MarkStep;
        var barTop = inner.Top + clipHeight;
        var barHeight = inner.Height - clipHeight;
        context.FillRectangle(clipped ? clip : off,
            new Rect(inner.Left, inner.Top, inner.Width, Math.Max(1, clipHeight - 2)));

        var segment = barHeight / Segments;
        var top = 1 - VolumeScale.MarkStep;
        var yellow = VolumeScale.YellowFrom / top;
        var red = VolumeScale.RedFrom / top;
        var level = Math.Min(1, _level / top);
        var hold = Math.Min(1, _hold / top);
        var holdSegment = (int)Math.Ceiling(hold * Segments) - 1;

        for (var i = 0; i < Segments; i++)
        {
            var at = (i + 0.5) / Segments;
            var lit = IsActive && (at <= level || i == holdSegment && hold > 0);
            var color = at >= red ? clip : at >= yellow ? high : low;
            var y = barTop + barHeight - (i + 1) * segment;
            context.FillRectangle(lit ? color : off,
                new Rect(inner.Left, y + 1, inner.Width, Math.Max(1, segment - 1)));
        }
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;
}
