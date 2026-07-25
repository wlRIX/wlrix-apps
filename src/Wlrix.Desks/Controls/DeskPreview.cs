using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Wlrix.Desks.ViewModels;

namespace Wlrix.Desks.Controls;

/// <summary>
/// Draws a desk's windows as opaque, outlined rectangles. The rectangles are expected to be
/// pre-scaled (by <see cref="DeskViewModel"/>) into this control's own coordinate space, so it
/// simply fills each one — mirroring the theme's direct-render controls and sidestepping the
/// per-item container positioning an ItemsControl/Canvas would need.
/// </summary>
public sealed class DeskPreview : Control
{
    public static readonly StyledProperty<IReadOnlyList<PreviewWindow>?> WindowsProperty =
        AvaloniaProperty.Register<DeskPreview, IReadOnlyList<PreviewWindow>?>(nameof(Windows));

    public static readonly StyledProperty<IBrush?> WindowBrushProperty =
        AvaloniaProperty.Register<DeskPreview, IBrush?>(nameof(WindowBrush));

    public static readonly StyledProperty<IBrush?> WindowBorderBrushProperty =
        AvaloniaProperty.Register<DeskPreview, IBrush?>(nameof(WindowBorderBrush));

    static DeskPreview() =>
        AffectsRender<DeskPreview>(WindowsProperty, WindowBrushProperty, WindowBorderBrushProperty);

    public IReadOnlyList<PreviewWindow>? Windows
    {
        get => GetValue(WindowsProperty);
        set => SetValue(WindowsProperty, value);
    }

    public IBrush? WindowBrush
    {
        get => GetValue(WindowBrushProperty);
        set => SetValue(WindowBrushProperty, value);
    }

    public IBrush? WindowBorderBrush
    {
        get => GetValue(WindowBorderBrushProperty);
        set => SetValue(WindowBorderBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Windows is not { } windows)
            return;

        var pen = WindowBorderBrush is { } border ? new Pen(border) : null;
        foreach (var w in windows)
            context.DrawRectangle(WindowBrush, pen, new Rect(w.X, w.Y, w.W, w.H));
    }
}
