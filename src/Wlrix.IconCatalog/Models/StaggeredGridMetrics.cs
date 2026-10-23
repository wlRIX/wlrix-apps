// SPDX-License-Identifier: GPL-3.0-or-later

namespace Wlrix.IconCatalog.Models;

/// <summary>Which way an arrow key moves through the grid.</summary>
public enum GridDirection
{
    Left,
    Right,
    Up,
    Down,
}

/// <summary>
/// The arithmetic behind the icon catalog's grid: IRIX's, with every second row pushed right by
/// half a cell.
/// </summary>
/// <remarks>
/// The IRIX icon catalog staggered its rows, so each icon sat between the two above it rather
/// than under one, and a long label had the gap of the row above to spill into. Wlrix.Files'
/// <c>IconGridMetrics</c> with that one change; kept as a pure struct for the same reason, so the
/// part that is easy to get wrong can be tested with no display.
///
/// <para>
/// Every row holds the same number of cells, offset rows included, so the column count is
/// what fits once half a stride is set aside for the offset.
/// </para>
/// </remarks>
public readonly record struct StaggeredGridMetrics
{
    public StaggeredGridMetrics(double cellWidth, double cellHeight, double gap = 8, double margin = 12)
    {
        // A non-positive cell would make the column count infinite.
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

    /// <summary>How far an odd row is pushed right.</summary>
    public double Offset => StrideX / 2;

    /// <summary>How many columns fit in a viewport of this width. Never fewer than one.</summary>
    /// <remarks>
    /// n columns need n cells, n−1 gaps, both margins and the offset row's half stride.
    /// </remarks>
    public int Columns(double viewportWidth)
    {
        var usable = viewportWidth - 2 * Margin + Gap - Offset;
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
        return rows == 0 ? 0 : rows * CellHeight + (rows - 1) * Gap + 2 * Margin;
    }

    /// <summary>The width the grid needs, which is more than the viewport only when one column does not fit.</summary>
    public double ExtentWidth(int itemCount, double viewportWidth)
    {
        if (itemCount <= 0)
            return 0;
        var columns = Math.Min(Columns(viewportWidth), itemCount);
        var offset = itemCount > columns ? Offset : 0;
        return columns * CellWidth + (columns - 1) * Gap + offset + 2 * Margin;
    }

    /// <summary>The row and column an item sits in.</summary>
    public (int Row, int Column) Cell(int index, double viewportWidth)
    {
        var columns = Columns(viewportWidth);
        return (index / columns, index % columns);
    }

    /// <summary>Where an item's cell sits, in grid coordinates.</summary>
    public (double X, double Y) CellOrigin(int index, double viewportWidth)
    {
        var (row, column) = Cell(index, viewportWidth);
        return (Margin + RowOffset(row) + column * StrideX, Margin + row * StrideY);
    }

    /// <summary>The item at a point, or -1 for the gaps and margins between cells.</summary>
    /// <remarks>
    /// -1 rather than the nearest item, because clicking the space between icons is how a
    /// rubber-band selection starts.
    /// </remarks>
    public int IndexAt(double x, double y, int itemCount, double viewportWidth)
    {
        if (itemCount <= 0)
            return -1;

        var columns = Columns(viewportWidth);
        var row = (int)Math.Floor((y - Margin) / StrideY);
        if (row < 0 || y - Margin - row * StrideY > CellHeight)
            return -1;

        var left = x - Margin - RowOffset(row);
        var column = (int)Math.Floor(left / StrideX);
        if (column < 0 || column >= columns || left - column * StrideX > CellWidth)
            return -1;

        var index = row * columns + column;
        return index < itemCount ? index : -1;
    }

    /// <summary>Every item whose cell intersects a rectangle, for rubber-band selection.</summary>
    /// <remarks>
    /// A plain walk over every cell: a catalog page holds dozens of icons, not the tens of
    /// thousands a directory can, so there is nothing to win by narrowing the search first.
    /// </remarks>
    public IEnumerable<int> IndicesIn(double x, double y, double width, double height, int itemCount, double viewportWidth)
    {
        if (width <= 0 || height <= 0)
            yield break;

        for (var index = 0; index < itemCount; index++)
        {
            var (cellX, cellY) = CellOrigin(index, viewportWidth);
            if (cellX < x + width && cellX + CellWidth > x && cellY < y + height && cellY + CellHeight > y)
                yield return index;
        }
    }

    /// <summary>The item an arrow key moves to from <paramref name="index"/>; the same index at an edge.</summary>
    /// <remarks>
    /// Up and Down go to whichever cell of the next row is nearest across, since with the rows
    /// staggered a plain step of one row's worth of items would drift sideways a half cell each
    /// time. Two cells are always equally near; the one with the same column number wins, which
    /// is what makes Down then Up come back to where it started.
    /// </remarks>
    public int Move(int index, GridDirection direction, int itemCount, double viewportWidth)
    {
        if (itemCount <= 0)
            return -1;
        index = Math.Clamp(index, 0, itemCount - 1);

        switch (direction)
        {
            case GridDirection.Left:
                return Math.Max(0, index - 1);
            case GridDirection.Right:
                return Math.Min(itemCount - 1, index + 1);
        }

        var columns = Columns(viewportWidth);
        var (row, column) = Cell(index, viewportWidth);
        var target = direction == GridDirection.Up ? row - 1 : row + 1;
        if (target < 0 || target * columns >= itemCount)
            return index;

        var center = RowOffset(row) + column * StrideX;
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var candidate = 0; candidate < columns; candidate++)
        {
            var distance = Math.Abs(RowOffset(target) + candidate * StrideX - center);
            if (distance < bestDistance - 0.001 || (Math.Abs(distance - bestDistance) <= 0.001 && candidate == column))
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        // The last row may be short; the nearest cell that exists is the next best thing.
        return Math.Min(itemCount - 1, target * columns + best);
    }

    /// <summary>
    /// The scroll offset that brings an item fully into view, or the current offset if it
    /// already is. Scrolls the minimum distance.
    /// </summary>
    public double ScrollToItem(int index, double viewportWidth, double viewportHeight, double currentOffset)
    {
        var (_, y) = CellOrigin(index, viewportWidth);
        var top = y - Margin;
        var bottom = y + CellHeight + Margin;

        if (top < currentOffset)
            return Math.Max(0, top);
        if (bottom > currentOffset + viewportHeight)
            return Math.Max(0, bottom - viewportHeight);
        return currentOffset;
    }

    private double RowOffset(int row) => (row & 1) == 1 ? Offset : 0;
}
