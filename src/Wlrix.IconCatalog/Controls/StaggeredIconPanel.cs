// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Wlrix.IconCatalog.Models;

namespace Wlrix.IconCatalog.Controls;

/// <summary>
/// A grid of uniform cells with every second row pushed right by half a cell: the IRIX icon
/// catalog's layout.
/// </summary>
/// <remarks>
/// Not virtualizing, unlike Wlrix.Files' icon panel: a catalog page holds a few dozen icons,
/// not a directory's thousands, and realizing them all keeps this to layout and navigation.
/// The arithmetic is <see cref="StaggeredGridMetrics"/>, apart from the control so it can be
/// tested without a display.
/// </remarks>
public class StaggeredIconPanel : Panel, INavigableContainer
{
    public static readonly StyledProperty<double> CellWidthProperty =
        AvaloniaProperty.Register<StaggeredIconPanel, double>(nameof(CellWidth), 96);

    public static readonly StyledProperty<double> CellHeightProperty =
        AvaloniaProperty.Register<StaggeredIconPanel, double>(nameof(CellHeight), 96);

    public static readonly StyledProperty<double> CellGapProperty =
        AvaloniaProperty.Register<StaggeredIconPanel, double>(nameof(CellGap), 8);

    public static readonly StyledProperty<double> CellMarginProperty =
        AvaloniaProperty.Register<StaggeredIconPanel, double>(nameof(CellMargin), 12);

    private double _lastWidth;

    static StaggeredIconPanel()
    {
        AffectsMeasure<StaggeredIconPanel>(
            CellWidthProperty, CellHeightProperty, CellGapProperty, CellMarginProperty);
    }

    public double CellWidth
    {
        get => GetValue(CellWidthProperty);
        set => SetValue(CellWidthProperty, value);
    }

    public double CellHeight
    {
        get => GetValue(CellHeightProperty);
        set => SetValue(CellHeightProperty, value);
    }

    public double CellGap
    {
        get => GetValue(CellGapProperty);
        set => SetValue(CellGapProperty, value);
    }

    public double CellMargin
    {
        get => GetValue(CellMarginProperty);
        set => SetValue(CellMarginProperty, value);
    }

    public StaggeredGridMetrics Metrics => new(CellWidth, CellHeight, CellGap, CellMargin);

    /// <summary>The width the layout was last done at, which decides the column count.</summary>
    public double LayoutWidth => _lastWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        var metrics = Metrics;

        // Inside a vertical ScrollViewer the height is infinite, but the width is real and is
        // what decides the column count.
        var width = double.IsInfinity(availableSize.Width) ? _lastWidth : availableSize.Width;
        if (width <= 0)
            width = 1;
        _lastWidth = width;

        var cell = new Size(metrics.CellWidth, metrics.CellHeight);
        foreach (var child in Children)
            child.Measure(cell);

        var count = Children.Count;
        return new Size(
            Math.Max(width, metrics.ExtentWidth(count, width)),
            metrics.ExtentHeight(count, width));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var metrics = Metrics;
        for (var i = 0; i < Children.Count; i++)
        {
            var (x, y) = metrics.CellOrigin(i, _lastWidth);
            Children[i].Arrange(new Rect(x, y, metrics.CellWidth, metrics.CellHeight));
        }
        return finalSize;
    }

    /// <summary>The index of the cell under a point in this panel's coordinates, or -1.</summary>
    public int IndexAt(Point point) => Metrics.IndexAt(point.X, point.Y, Children.Count, _lastWidth);

    /// <summary>Every cell a rectangle in this panel's coordinates touches.</summary>
    public IEnumerable<int> IndicesIn(Rect rect) =>
        Metrics.IndicesIn(rect.X, rect.Y, rect.Width, rect.Height, Children.Count, _lastWidth);

    /// <summary>Keyboard navigation across the staggered rows.</summary>
    /// <remarks>
    /// Page Up and Page Down are not handled here: in the catalog they turn the notebook's
    /// pages, as the Page menu says.
    /// </remarks>
    IInputElement? INavigableContainer.GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        var count = Children.Count;
        if (count == 0)
            return null;

        var current = from is Control control ? IndexOfContainer(control) : -1;
        var metrics = Metrics;

        var target = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => count - 1,
            _ when current < 0 => 0,
            NavigationDirection.Next => current + 1,
            NavigationDirection.Previous => current - 1,
            NavigationDirection.Left => metrics.Move(current, GridDirection.Left, count, _lastWidth),
            NavigationDirection.Right => metrics.Move(current, GridDirection.Right, count, _lastWidth),
            NavigationDirection.Up => metrics.Move(current, GridDirection.Up, count, _lastWidth),
            NavigationDirection.Down => metrics.Move(current, GridDirection.Down, count, _lastWidth),
            _ => -1,
        };

        if (target < 0 || target >= count)
            return null;

        BringIntoView(target);
        return Children[target];
    }

    /// <summary>Scrolls the nearest scroll viewer the least distance that shows a cell.</summary>
    public void BringIntoView(int index)
    {
        if (index < 0 || index >= Children.Count || this.FindAncestorOfType<ScrollViewer>() is not { } scroller)
            return;

        var offset = Metrics.ScrollToItem(index, _lastWidth, scroller.Viewport.Height, scroller.Offset.Y);
        if (Math.Abs(offset - scroller.Offset.Y) > 0.5)
            scroller.Offset = scroller.Offset.WithY(offset);
    }

    // The child that is, or contains, the element focus is on.
    private int IndexOfContainer(Control control)
    {
        for (Visual? visual = control; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Control child && child.GetVisualParent() == this)
                return Children.IndexOf(child);
        }
        return -1;
    }
}
