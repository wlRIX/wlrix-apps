namespace Wlrix.Files.Core;

/// <summary>
/// The arithmetic behind an IRIX-style icon grid: how many columns fit, where a cell lands,
/// and which items a viewport can see.
/// </summary>
/// <remarks>
/// This is a pure struct in Core, apart from the panel that uses it, and deliberately.
/// Avalonia has no virtualizing wrap panel — <c>WrapPanel</c> does not virtualize and the only
/// <c>VirtualizingPanel</c> subclasses are the stack and carousel ones — so the file manager
/// has to write its own. A custom panel cannot be tested in CI, which has no display; the
/// arithmetic that is actually easy to get wrong can be, if it lives here.
///
/// <para>
/// Cells are uniform, which is what keeps this to index arithmetic rather than a layout pass.
/// IRIX's grid is uniform, and <c>wlrix-desktop</c> already proves the model with its
/// <c>cell_width</c>, <c>cell_height</c>, <c>gap</c> and <c>margin</c> settings.
/// </para>
/// </remarks>
public readonly record struct IconGridMetrics
{
    public IconGridMetrics(double cellWidth, double cellHeight, double gap = 8, double margin = 12)
    {
        // A non-positive cell would make the column count infinite, so the floor is enforced
        // here rather than trusted from a config file.
        CellWidth = Math.Max(1, cellWidth);
        CellHeight = Math.Max(1, cellHeight);
        Gap = Math.Max(0, gap);
        Margin = Math.Max(0, margin);
    }

    public double CellWidth { get; }
    public double CellHeight { get; }

    /// <summary>Space between cells, both ways.</summary>
    public double Gap { get; }

    /// <summary>Space between the outermost cells and the panel edge.</summary>
    public double Margin { get; }

    /// <summary>The pitch from one cell's left edge to the next.</summary>
    public double StrideX => CellWidth + Gap;

    /// <summary>The pitch from one row's top edge to the next.</summary>
    public double StrideY => CellHeight + Gap;

    /// <summary>How many columns fit in a viewport of this width. Never fewer than one.</summary>
    /// <remarks>
    /// n columns need n cells plus n−1 gaps plus both margins, so the usable width has one
    /// gap added back before dividing. Returning zero for a narrow window would divide by
    /// zero everywhere downstream; a single column that overflows is the better failure.
    /// </remarks>
    public int Columns(double viewportWidth)
    {
        var usable = viewportWidth - 2 * Margin + Gap;
        if (usable <= 0)
            return 1;
        return Math.Max(1, (int)(usable / StrideX));
    }

    /// <summary>How many rows an item count occupies at this width.</summary>
    public int Rows(int itemCount, double viewportWidth)
    {
        if (itemCount <= 0)
            return 0;
        var columns = Columns(viewportWidth);
        return (itemCount + columns - 1) / columns;
    }

    /// <summary>The full height the grid needs, for the scrollbar's extent.</summary>
    public double ExtentHeight(int itemCount, double viewportWidth)
    {
        var rows = Rows(itemCount, viewportWidth);
        if (rows == 0)
            return 0;
        // rows cells plus rows-1 gaps plus both margins.
        return rows * CellHeight + (rows - 1) * Gap + 2 * Margin;
    }

    /// <summary>Where an item's cell sits, in grid coordinates.</summary>
    public (double X, double Y) CellOrigin(int index, double viewportWidth)
    {
        var columns = Columns(viewportWidth);
        var row = index / columns;
        var column = index % columns;
        return (Margin + column * StrideX, Margin + row * StrideY);
    }

    /// <summary>
    /// The half-open range of item indices a viewport can show, padded by
    /// <paramref name="overscanRows"/> rows either side.
    /// </summary>
    /// <remarks>
    /// The overscan is what stops a fast scroll showing empty cells: containers for the row
    /// about to arrive are realized before it does. Returns an empty range rather than a
    /// negative one when there is nothing to show, so callers need no special case.
    /// </remarks>
    public (int First, int Count) VisibleRange(int itemCount, double viewportWidth, double scrollOffset, double viewportHeight, int overscanRows = 1)
    {
        if (itemCount <= 0 || viewportHeight <= 0)
            return (0, 0);

        var columns = Columns(viewportWidth);
        var totalRows = Rows(itemCount, viewportWidth);

        // The margin is above the first row, so an offset inside it is still row zero.
        var firstRow = (int)Math.Floor((scrollOffset - Margin) / StrideY);
        var lastRow = (int)Math.Floor((scrollOffset + viewportHeight - Margin) / StrideY);

        firstRow = Math.Max(0, firstRow - overscanRows);
        lastRow = Math.Min(totalRows - 1, lastRow + overscanRows);
        if (lastRow < firstRow)
            return (0, 0);

        var first = firstRow * columns;
        var last = Math.Min(itemCount - 1, (lastRow + 1) * columns - 1);
        return (first, last - first + 1);
    }

    /// <summary>
    /// The scroll offset that brings an item fully into view, or the current offset if it
    /// already is.
    /// </summary>
    /// <remarks>
    /// Scrolls the minimum distance: an item above the viewport comes to the top edge, one
    /// below comes to the bottom. Jumping it to the center would be disorienting when
    /// arrow-keying through a listing.
    /// </remarks>
    public double ScrollToItem(int index, double viewportWidth, double viewportHeight, double currentOffset)
    {
        var (_, y) = CellOrigin(index, viewportWidth);

        // Include the margin in what has to be visible, so the first row is not flush
        // against the top edge after scrolling to it.
        var top = y - Margin;
        var bottom = y + CellHeight + Margin;

        if (top < currentOffset)
            return Math.Max(0, top);
        if (bottom > currentOffset + viewportHeight)
            return Math.Max(0, bottom - viewportHeight);
        return currentOffset;
    }

    /// <summary>
    /// The item at a point, or -1 for the gaps and margins between cells.
    /// </summary>
    /// <remarks>
    /// Returns -1 rather than the nearest item on purpose: clicking the space between two
    /// icons is how a rubber-band selection starts, and snapping it to whichever icon was
    /// closest would make an empty-space drag impossible.
    /// </remarks>
    public int IndexAt(double x, double y, int itemCount, double viewportWidth)
    {
        if (itemCount <= 0)
            return -1;

        var columns = Columns(viewportWidth);
        var column = (int)Math.Floor((x - Margin) / StrideX);
        var row = (int)Math.Floor((y - Margin) / StrideY);
        if (column < 0 || column >= columns || row < 0)
            return -1;

        // Inside the cell rather than the gap that follows it.
        if (x - Margin - column * StrideX > CellWidth)
            return -1;
        if (y - Margin - row * StrideY > CellHeight)
            return -1;

        var index = row * columns + column;
        return index < itemCount ? index : -1;
    }

    /// <summary>
    /// Every item whose cell intersects a rectangle, for rubber-band selection.
    /// </summary>
    /// <remarks>
    /// Intersection rather than containment, which is what a user expects from a band drawn
    /// across a grid: brushing an icon selects it, and requiring the band to swallow a cell
    /// whole would make selecting a row of them awkward.
    /// </remarks>
    public IEnumerable<int> IndicesIn(double x, double y, double width, double height, int itemCount, double viewportWidth)
    {
        if (itemCount <= 0 || width <= 0 || height <= 0)
            yield break;

        var columns = Columns(viewportWidth);
        var right = x + width;
        var bottom = y + height;

        var firstRow = Math.Max(0, (int)Math.Floor((y - Margin) / StrideY));
        var lastRow = (int)Math.Floor((bottom - Margin) / StrideY);
        var firstColumn = Math.Max(0, (int)Math.Floor((x - Margin) / StrideX));
        var lastColumn = Math.Min(columns - 1, (int)Math.Floor((right - Margin) / StrideX));

        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                var index = row * columns + column;
                if (index >= itemCount)
                    yield break;

                // The row/column window above includes cells the band only reaches the gap
                // beside, so each candidate is still checked against the band proper.
                var cellX = Margin + column * StrideX;
                var cellY = Margin + row * StrideY;
                if (cellX < right && cellX + CellWidth > x && cellY < bottom && cellY + CellHeight > y)
                    yield return index;
            }
        }
    }
}
