// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Wlrix.ImageView.Controls;

/// <summary>
/// The Geom tab's rotation dial, after IRIX imgview's: red ticks every 45° and a needle that
/// points the way the image's top now faces.
/// </summary>
/// <remarks>
/// The needle follows the pointer rather than moving by how far it is dragged: on a dial, the
/// direction you point is the answer. Holding Shift snaps to quarter turns, as the Rotate tool
/// does. Straight up is 0 and angles run clockwise, the same as <see cref="Models.ViewTransform.Rotation"/>.
/// </remarks>
public sealed class RotationDial : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<RotationDial, double>(nameof(Value),
            defaultBindingMode: global::Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<IBrush?> TickBrushProperty =
        AvaloniaProperty.Register<RotationDial, IBrush?>(nameof(TickBrush), Brushes.Red);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextBlock.ForegroundProperty.AddOwner<RotationDial>();

    private bool _dragging;

    static RotationDial()
    {
        AffectsRender<RotationDial>(ValueProperty, TickBrushProperty, ForegroundProperty);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public IBrush? TickBrush
    {
        get => GetValue(TickBrushProperty);
        set => SetValue(TickBrushProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>The unit vector for a clockwise angle from straight up.</summary>
    private static Vector Direction(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Vector(Math.Sin(radians), -Math.Cos(radians));
    }

    public override void Render(DrawingContext context)
    {
        var center = new Rect(Bounds.Size).Center;
        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2 - 2;
        if (radius <= 4)
            return;

        if (TickBrush is { } ticks)
        {
            var pen = new Pen(ticks, 2, lineCap: PenLineCap.Flat);
            for (var angle = 0; angle < 360; angle += 45)
            {
                var direction = Direction(angle);
                context.DrawLine(pen, center + direction * (radius * 0.72), center + direction * radius);
            }
        }

        // A slim wedge from a short tail through the center to a point.
        var foreground = Foreground ?? Brushes.Black;
        var tip = Direction(Value);
        var side = new Vector(-tip.Y, tip.X);
        var needle = new StreamGeometry();
        using (var path = needle.Open())
        {
            path.BeginFigure(center + tip * (radius * 0.62), isFilled: true);
            path.LineTo(center + side * 2.2);
            path.LineTo(center - tip * (radius * 0.12));
            path.LineTo(center - side * 2.2);
            path.EndFigure(isClosed: true);
        }
        context.DrawGeometry(foreground, null, needle);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _dragging = true;
        e.Pointer.Capture(this);
        PointAt(e.GetPosition(this), e.KeyModifiers);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging)
            return;
        PointAt(e.GetPosition(this), e.KeyModifiers);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging)
            return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    private void PointAt(Point point, KeyModifiers modifiers)
    {
        var center = new Rect(Bounds.Size).Center;
        double dx = point.X - center.X, dy = point.Y - center.Y;
        // Right at the center the angle is meaningless.
        if (dx * dx + dy * dy < 9)
            return;

        var degrees = Math.Atan2(dx, -dy) * 180 / Math.PI;
        if (modifiers.HasFlag(KeyModifiers.Shift))
            degrees = Math.Round(degrees / 90) * 90;
        SetCurrentValue(ValueProperty, Models.ViewTransform.NormalizeDegrees(degrees));
    }
}
