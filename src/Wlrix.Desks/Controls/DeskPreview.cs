using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Wlrix.Desks.ViewModels;

namespace Wlrix.Desks.Controls;

/// <summary>
/// Draws a desk's windows as opaque, outlined rectangles, and hit-tests them: the rectangle
/// under the pointer reports itself as hovered, and clicking one selects it. The rectangles are
/// expected to be pre-scaled (by <see cref="DeskViewModel"/>) into this control's own coordinate
/// space, so it simply fills each one — mirroring the theme's direct-render controls and
/// sidestepping the per-item container positioning an ItemsControl/Canvas would need.
///
/// Hover and selection are held as window *ids* and bound two-way to shared state, so a window
/// that appears on several desks (anything on the Global desk) lights up in every tile at once,
/// the way IRIX highlighted it.
/// </summary>
public sealed class DeskPreview : Control, ICustomHitTest
{
    public static readonly StyledProperty<IReadOnlyList<PreviewWindow>?> WindowsProperty =
        AvaloniaProperty.Register<DeskPreview, IReadOnlyList<PreviewWindow>?>(nameof(Windows));

    public static readonly StyledProperty<IBrush?> WindowBrushProperty =
        AvaloniaProperty.Register<DeskPreview, IBrush?>(nameof(WindowBrush));

    public static readonly StyledProperty<IBrush?> WindowBorderBrushProperty =
        AvaloniaProperty.Register<DeskPreview, IBrush?>(nameof(WindowBorderBrush));

    public static readonly StyledProperty<IBrush?> HoverBorderBrushProperty =
        AvaloniaProperty.Register<DeskPreview, IBrush?>(nameof(HoverBorderBrush));

    public static readonly StyledProperty<IBrush?> SelectionBorderBrushProperty =
        AvaloniaProperty.Register<DeskPreview, IBrush?>(nameof(SelectionBorderBrush));

    public static readonly StyledProperty<long?> HoveredWindowIdProperty =
        AvaloniaProperty.Register<DeskPreview, long?>(
            nameof(HoveredWindowId), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<long?> SelectedWindowIdProperty =
        AvaloniaProperty.Register<DeskPreview, long?>(
            nameof(SelectedWindowId), defaultBindingMode: BindingMode.TwoWay);

    static DeskPreview() =>
        AffectsRender<DeskPreview>(
            WindowsProperty, WindowBrushProperty, WindowBorderBrushProperty,
            HoverBorderBrushProperty, SelectionBorderBrushProperty,
            HoveredWindowIdProperty, SelectedWindowIdProperty);

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

    /// <summary>Outlines the window under the pointer.</summary>
    public IBrush? HoverBorderBrush
    {
        get => GetValue(HoverBorderBrushProperty);
        set => SetValue(HoverBorderBrushProperty, value);
    }

    /// <summary>Outlines the selected window.</summary>
    public IBrush? SelectionBorderBrush
    {
        get => GetValue(SelectionBorderBrushProperty);
        set => SetValue(SelectionBorderBrushProperty, value);
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

    // Only the window rectangles are painted, and hit testing follows what a control paints, so
    // without this the gaps between windows would not be clickable — and clicking the desk's
    // empty space has to be able to clear the selection.
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    public override void Render(DrawingContext context)
    {
        if (Windows is not { } windows)
            return;

        var normal = Pen(WindowBorderBrush);
        var hovered = Pen(HoverBorderBrush) ?? normal;
        var selected = Pen(SelectionBorderBrush) ?? normal;

        foreach (var w in windows)
        {
            var pen = w.Id == SelectedWindowId ? selected
                : w.Id == HoveredWindowId ? hovered
                : normal;
            context.DrawRectangle(WindowBrush, pen, new Rect(w.X, w.Y, w.W, w.H));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        SetCurrentValue(HoveredWindowIdProperty, WindowAt(e.GetPosition(this)));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        // Avalonia raises this on the tile being left before the move on the tile being
        // entered, so handing the hover over between tiles doesn't clear the new one.
        SetCurrentValue(HoveredWindowIdProperty, null);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        // Clicking the desk's empty space deselects, matching the desk strip's own behavior.
        SetCurrentValue(SelectedWindowIdProperty, WindowAt(e.GetPosition(this)));
    }

    // The topmost window at this point: windows are drawn in order, so the last one wins.
    private long? WindowAt(Point point)
    {
        if (Windows is not { } windows)
            return null;

        for (var i = windows.Count - 1; i >= 0; i--)
        {
            var w = windows[i];
            if (new Rect(w.X, w.Y, w.W, w.H).Contains(point))
                return w.Id;
        }

        return null;
    }

    private static Pen? Pen(IBrush? brush) => brush is null ? null : new Pen(brush);
}
