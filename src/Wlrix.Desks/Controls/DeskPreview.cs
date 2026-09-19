using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Wlrix.Desks.Models;
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
///
/// The rectangle under the pointer also names itself in a tooltip, which is what
/// <c>ov</c>'s <c>--noWindowName</c> switched between titles and application IDs. The tip is set
/// imperatively from the pointer handlers rather than declared per item, because there are no
/// per-item controls here to declare it on.
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

    public static readonly StyledProperty<WindowLabel> LabelModeProperty =
        AvaloniaProperty.Register<DeskPreview, WindowLabel>(nameof(LabelMode));

    // Where the pointer was last seen, so a snapshot that moves the windows can re-ask what is
    // under it.
    private Point? _lastPointerPosition;

    // LabelModeProperty is deliberately absent: a label is tipped, never drawn, so changing it
    // has nothing to repaint.
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

    /// <summary>Whether a hovered window names itself by title, by application ID, or not at all.</summary>
    public WindowLabel LabelMode
    {
        get => GetValue(LabelModeProperty);
        set => SetValue(LabelModeProperty, value);
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
        _lastPointerPosition = e.GetPosition(this);
        UpdateHover(_lastPointerPosition.Value);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _lastPointerPosition = null;
        // Avalonia raises this on the tile being left before the move on the tile being
        // entered, so handing the hover over between tiles doesn't clear the new one.
        SetCurrentValue(HoveredWindowIdProperty, null);
        ToolTip.SetTip(this, null);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // The previews are rebuilt on every snapshot, so a window can move out from under a
        // pointer that never moved. Without this the tooltip would go on naming it until the
        // pointer twitched.
        if ((change.Property == WindowsProperty || change.Property == LabelModeProperty)
            && IsPointerOver
            && _lastPointerPosition is { } position)
        {
            UpdateHover(position);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        // Clicking the desk's empty space deselects, matching the desk strip's own behavior.
        SetCurrentValue(SelectedWindowIdProperty, WindowAt(e.GetPosition(this))?.Id);
    }

    private void UpdateHover(Point point)
    {
        var hovered = WindowAt(point);
        SetCurrentValue(HoveredWindowIdProperty, hovered?.Id);

        // Setting the tip here reaches the tooltip service on this same pointer event: the
        // input manager routes a raw event to the controls before it hands it to the service.
        // Setting it to null over empty space is what closes an open tooltip, and setting it to
        // a different string while one is open replaces the text in place.
        //
        // What that in-place replacement does not do is move the bubble, which is placed where
        // the pointer was when it opened. Crossing straight from one rectangle to another that
        // touches it therefore leaves the tip where it was. Fighting it is worse than living
        // with it -- closing the tooltip by hand leaves the service still considering this
        // control its host, so it never reopens -- and rectangles in a 320x100 preview almost
        // always have a gap between them to pass over.
        ToolTip.SetTip(this, LabelFor(hovered));
    }

    /// <summary>
    /// What a window calls itself, in the mode asked for, falling back to the other name.
    /// </summary>
    /// <remarks>
    /// The fallback is why no localized string is needed here: a window with no title names its
    /// application rather than showing an empty bubble, and one that offers neither gets no
    /// tooltip at all. Titles and application IDs are the application's own text in any case,
    /// never this program's.
    /// </remarks>
    private string? LabelFor(PreviewWindow? window) => window is null ? null : LabelMode switch
    {
        WindowLabel.Title => Preferring(window.Title, window.AppId),
        WindowLabel.AppId => Preferring(window.AppId, window.Title),
        _ => null,
    };

    private static string? Preferring(string first, string second) =>
        first.Length > 0 ? first : second.Length > 0 ? second : null;

    // The topmost window at this point: windows are drawn in order, so the last one wins.
    private PreviewWindow? WindowAt(Point point)
    {
        if (Windows is not { } windows)
            return null;

        for (var i = windows.Count - 1; i >= 0; i--)
        {
            var w = windows[i];
            if (new Rect(w.X, w.Y, w.W, w.H).Contains(point))
                return w;
        }

        return null;
    }

    private static Pen? Pen(IBrush? brush) => brush is null ? null : new Pen(brush);
}
