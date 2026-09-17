using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Wlrix.Files.Core;

namespace Wlrix.Files.Controls;

/// <summary>
/// A uniform grid of cells that realizes only what is on screen: the IRIX icon view.
/// </summary>
/// <remarks>
/// Avalonia has no virtualizing wrap panel. <c>WrapPanel</c> realizes every child, and the
/// only <see cref="VirtualizingPanel"/> subclasses it ships are the stack and carousel ones,
/// so an icon view over a large directory has to be written.
///
/// <para>
/// Uniform cells are what keep this to arithmetic rather than a layout pass: the index of an
/// item determines its position outright, so the visible range is a division rather than a
/// walk. That arithmetic lives in <see cref="IconGridMetrics"/> in Core, apart from this
/// class, because a control cannot be tested where there is no display and the rules that
/// are easy to get wrong can be.
/// </para>
///
/// <para>
/// The viewport comes from <see cref="Layoutable.EffectiveViewportChanged"/>, which is how
/// <c>VirtualizingStackPanel</c> does it too — it reports the visible rectangle in this
/// panel's own coordinates, so no hunting for an ancestor <c>ScrollViewer</c>.
/// </para>
/// </remarks>
public class VirtualizingIconPanel : VirtualizingPanel
{
    /// <summary>Marks a container as belonging to the shared recycle pool.</summary>
    private static readonly AttachedProperty<object?> RecycleKeyProperty =
        AvaloniaProperty.RegisterAttached<VirtualizingIconPanel, Control, object?>("RecycleKey");

    private static readonly object ItemIsItsOwnContainer = new();

    public static readonly StyledProperty<double> CellWidthProperty =
        AvaloniaProperty.Register<VirtualizingIconPanel, double>(nameof(CellWidth), 96);

    public static readonly StyledProperty<double> CellHeightProperty =
        AvaloniaProperty.Register<VirtualizingIconPanel, double>(nameof(CellHeight), 104);

    public static readonly StyledProperty<double> CellGapProperty =
        AvaloniaProperty.Register<VirtualizingIconPanel, double>(nameof(CellGap), 8);

    public static readonly StyledProperty<double> CellMarginProperty =
        AvaloniaProperty.Register<VirtualizingIconPanel, double>(nameof(CellMargin), 12);

    private readonly Dictionary<int, Control> _realized = [];
    private readonly Dictionary<object, Stack<Control>> _recyclePool = [];
    private Rect _viewport = new(0, 0, double.PositiveInfinity, double.PositiveInfinity);
    private double _lastWidth;

    static VirtualizingIconPanel()
    {
        AffectsMeasure<VirtualizingIconPanel>(
            CellWidthProperty, CellHeightProperty, CellGapProperty, CellMarginProperty);
    }

    public VirtualizingIconPanel() => EffectiveViewportChanged += OnEffectiveViewportChanged;

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

    /// <summary>The layout rules, as Core describes them.</summary>
    public IconGridMetrics Metrics => new(CellWidth, CellHeight, CellGap, CellMargin);

    protected override Size MeasureOverride(Size availableSize)
    {
        var items = Items;
        var metrics = Metrics;

        // Inside a vertical ScrollViewer the available height is infinite, but the width is
        // real and is what decides the column count.
        var width = double.IsInfinity(availableSize.Width) ? _lastWidth : availableSize.Width;
        if (width <= 0)
            width = 1;
        _lastWidth = width;

        if (items.Count == 0)
        {
            RecycleAll();
            return default;
        }

        var (first, count) = metrics.VisibleRange(
            items.Count, width,
            scrollOffset: double.IsInfinity(_viewport.Y) ? 0 : _viewport.Y,
            viewportHeight: double.IsInfinity(_viewport.Height) ? availableSize.Height : _viewport.Height);

        // Before the first effective-viewport report the height can still be infinite, which
        // would realize the whole directory. One screenful is the safe guess until the real
        // viewport arrives a beat later.
        if (count == 0 && items.Count > 0 && double.IsInfinity(_viewport.Height))
            (first, count) = (0, Math.Min(items.Count, metrics.Columns(width) * 8));

        RecycleOutside(first, count);

        var cell = new Size(metrics.CellWidth, metrics.CellHeight);
        for (var i = first; i < first + count; i++)
        {
            var container = GetOrCreate(items, i);
            container.Measure(cell);
        }

        return new Size(width, metrics.ExtentHeight(items.Count, width));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var metrics = Metrics;
        foreach (var (index, container) in _realized)
        {
            var (x, y) = metrics.CellOrigin(index, finalSize.Width);
            container.Arrange(new Rect(x, y, metrics.CellWidth, metrics.CellHeight));
        }
        return finalSize;
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        var viewport = e.EffectiveViewport;
        // Only a vertical change alters which rows are realized; ignoring the rest avoids a
        // remeasure per horizontal pixel during a window resize.
        if (Math.Abs(viewport.Y - _viewport.Y) < 0.5 && Math.Abs(viewport.Height - _viewport.Height) < 0.5)
        {
            _viewport = viewport;
            return;
        }

        _viewport = viewport;
        InvalidateMeasure();
    }

    // --- realization -----------------------------------------------------

    private Control GetOrCreate(IReadOnlyList<object?> items, int index)
    {
        if (_realized.TryGetValue(index, out var existing))
            return existing;

        var item = items[index];
        var generator = ItemContainerGenerator!;

        Control container;
        if (generator.NeedsContainer(item, index, out var recycleKey))
        {
            container = Reuse(item, index, recycleKey) ?? Create(item, index, recycleKey);
        }
        else
        {
            // The item is already a control, so it is its own container and must never be
            // pooled -- recycling it would hand one item's control to another.
            container = (Control)item!;
            if (!container.IsSet(RecycleKeyProperty))
            {
                generator.PrepareItemContainer(container, container, index);
                AddInternalChild(container);
                container.SetValue(RecycleKeyProperty, ItemIsItsOwnContainer);
                generator.ItemContainerPrepared(container, item, index);
            }
            container.SetCurrentValue(IsVisibleProperty, true);
        }

        _realized[index] = container;
        return container;
    }

    private Control? Reuse(object? item, int index, object? recycleKey)
    {
        if (recycleKey is null || !_recyclePool.TryGetValue(recycleKey, out var pool) || pool.Count == 0)
            return null;

        var generator = ItemContainerGenerator!;
        var recycled = pool.Pop();
        recycled.SetCurrentValue(IsVisibleProperty, true);
        generator.PrepareItemContainer(recycled, item, index);
        AddInternalChild(recycled);
        generator.ItemContainerPrepared(recycled, item, index);
        return recycled;
    }

    private Control Create(object? item, int index, object? recycleKey)
    {
        var generator = ItemContainerGenerator!;
        var container = generator.CreateContainer(item, index, recycleKey);
        container.SetValue(RecycleKeyProperty, recycleKey);
        generator.PrepareItemContainer(container, item, index);
        AddInternalChild(container);
        generator.ItemContainerPrepared(container, item, index);
        return container;
    }

    private void RecycleOutside(int first, int count)
    {
        var last = first + count - 1;
        List<int>? drop = null;
        foreach (var index in _realized.Keys)
        {
            if (index < first || index > last)
                (drop ??= []).Add(index);
        }

        if (drop is null)
            return;
        foreach (var index in drop)
        {
            Recycle(_realized[index]);
            _realized.Remove(index);
        }
    }

    private void RecycleAll()
    {
        foreach (var container in _realized.Values)
            Recycle(container);
        _realized.Clear();
    }

    private void Recycle(Control container)
    {
        var recycleKey = container.GetValue(RecycleKeyProperty);

        if (recycleKey is null)
        {
            ItemContainerGenerator!.ClearItemContainer(container);
            RemoveInternalChild(container);
            return;
        }

        if (ReferenceEquals(recycleKey, ItemIsItsOwnContainer))
        {
            container.SetCurrentValue(IsVisibleProperty, false);
            return;
        }

        ItemContainerGenerator!.ClearItemContainer(container);
        RemoveInternalChild(container);
        if (!_recyclePool.TryGetValue(recycleKey, out var pool))
            _recyclePool[recycleKey] = pool = new Stack<Control>();
        pool.Push(container);
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Indices shift on any structural change, and a realized container keyed by a stale
        // index would show the wrong item. Starting over is correct and, with only a
        // screenful realized, cheap.
        RecycleAll();
        InvalidateMeasure();
    }

    // --- VirtualizingPanel -----------------------------------------------

    protected override Control? ContainerFromIndex(int index) =>
        _realized.GetValueOrDefault(index);

    protected override int IndexFromContainer(Control container)
    {
        foreach (var (index, realized) in _realized)
        {
            if (ReferenceEquals(realized, container))
                return index;
        }
        return -1;
    }

    protected override IEnumerable<Control>? GetRealizedContainers() => _realized.Values;

    protected override Control? ScrollIntoView(int index)
    {
        if (index < 0 || index >= Items.Count)
            return null;

        if (this.FindAncestorOfType<ScrollViewer>() is { } scroller)
        {
            var offset = Metrics.ScrollToItem(index, Bounds.Width, scroller.Viewport.Height, scroller.Offset.Y);
            if (Math.Abs(offset - scroller.Offset.Y) > 0.5)
            {
                scroller.Offset = scroller.Offset.WithY(offset);
                // The new rows are not realized until the layout pass that follows the
                // scroll, so the container asked for may not exist yet.
                UpdateLayout();
            }
        }

        return ContainerFromIndex(index);
    }

    /// <summary>Keyboard navigation across the grid.</summary>
    /// <remarks>
    /// Up and down move by a whole row, which is the only part of this a stack panel could
    /// not provide: in a grid they are a column count apart, not one.
    /// </remarks>
    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        var items = Items;
        if (items.Count == 0)
            return null;

        var columns = Metrics.Columns(Bounds.Width);
        var current = from is Control control ? IndexFromContainer(control) : -1;

        var target = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => items.Count - 1,
            NavigationDirection.Next => current + 1,
            NavigationDirection.Previous => current - 1,
            NavigationDirection.Left => current - 1,
            NavigationDirection.Right => current + 1,
            NavigationDirection.Up => current - columns,
            NavigationDirection.Down => current + columns,
            NavigationDirection.PageUp => current - columns * VisibleRows(),
            NavigationDirection.PageDown => current + columns * VisibleRows(),
            _ => -1
        };

        if (target < 0 || target >= items.Count)
            return null;
        return ScrollIntoView(target);
    }

    private int VisibleRows() =>
        Math.Max(1, (int)(_viewport.Height / Math.Max(1, Metrics.StrideY)));
}
