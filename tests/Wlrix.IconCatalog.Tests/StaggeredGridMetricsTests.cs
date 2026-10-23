// SPDX-License-Identifier: GPL-3.0-or-later

using Wlrix.IconCatalog.Models;
using Xunit;

namespace Wlrix.IconCatalog.Tests;

public class StaggeredGridMetricsTests
{
    // 100-wide cells, 10 gap, 10 margin: stride 110, offset 55.
    private static readonly StaggeredGridMetrics Grid = new(100, 80, gap: 10, margin: 10);

    // Room for exactly four columns plus the offset: 2*10 + 4*100 + 3*10 + 55.
    private const double Width = 505;

    [Fact]
    public void TheOffsetIsSetAsideBeforeCountingColumns()
    {
        Assert.Equal(4, Grid.Columns(Width));
        Assert.Equal(3, Grid.Columns(Width - 1));
        Assert.Equal(1, Grid.Columns(10));
    }

    [Fact]
    public void EverySecondRowIsPushedRightByHalfAStride()
    {
        Assert.Equal((10, 10), Grid.CellOrigin(0, Width));
        Assert.Equal((340, 10), Grid.CellOrigin(3, Width));
        Assert.Equal((65, 100), Grid.CellOrigin(4, Width));
        Assert.Equal((395, 100), Grid.CellOrigin(7, Width));
        Assert.Equal((10, 190), Grid.CellOrigin(8, Width));
    }

    [Fact]
    public void TheGridIsAsTallAsItsRows()
    {
        Assert.Equal(0, Grid.ExtentHeight(0, Width));
        Assert.Equal(100, Grid.ExtentHeight(4, Width));
        Assert.Equal(190, Grid.ExtentHeight(5, Width));
    }

    [Theory]
    [InlineData(15, 15, 0)]
    [InlineData(115, 15, -1)] // the gap after the first cell
    [InlineData(125, 15, 1)]
    [InlineData(15, 105, -1)] // under the offset: nothing there
    [InlineData(70, 105, 4)]
    [InlineData(70, 95, -1)] // the gap between rows
    [InlineData(460, 105, 7)]
    [InlineData(15, 195, 8)]
    [InlineData(125, 195, -1)] // past the last item
    public void HitTestingFollowsTheOffset(double x, double y, int expected)
    {
        Assert.Equal(expected, Grid.IndexAt(x, y, itemCount: 9, Width));
    }

    [Fact]
    public void ABandSelectsWhatItTouches()
    {
        // From the middle of cell 0 down into row two, as far right as cell 4's left half.
        var hit = Grid.IndicesIn(50, 50, 30, 80, itemCount: 9, Width).ToList();
        Assert.Equal([0, 4], hit);

        // Only in the margin under the offset: nothing.
        Assert.Empty(Grid.IndicesIn(10, 100, 40, 50, itemCount: 9, Width));
    }

    [Fact]
    public void LeftAndRightMoveByOneAndStopAtTheEnds()
    {
        Assert.Equal(0, Grid.Move(0, GridDirection.Left, 9, Width));
        Assert.Equal(4, Grid.Move(3, GridDirection.Right, 9, Width));
        Assert.Equal(8, Grid.Move(8, GridDirection.Right, 9, Width));
    }

    [Fact]
    public void DownThenUpComesBackToWhereItStarted()
    {
        for (var start = 0; start < 4; start++)
        {
            var down = Grid.Move(start, GridDirection.Down, 12, Width);
            Assert.Equal(start + 4, down);
            Assert.Equal(start, Grid.Move(down, GridDirection.Up, 12, Width));
        }
    }

    [Fact]
    public void UpAndDownStopAtTheEdgesAndLandOnAShortLastRow()
    {
        Assert.Equal(2, Grid.Move(2, GridDirection.Up, 9, Width));
        Assert.Equal(8, Grid.Move(7, GridDirection.Down, 9, Width));
        Assert.Equal(8, Grid.Move(8, GridDirection.Down, 9, Width));
    }

    [Fact]
    public void ScrollingBringsARowIntoViewByTheShortestWay()
    {
        Assert.Equal(0, Grid.ScrollToItem(0, Width, 150, 0));
        // Row three's cell bottom is 270, plus the margin.
        Assert.Equal(130, Grid.ScrollToItem(8, Width, 150, 0));
        Assert.Equal(90, Grid.ScrollToItem(4, Width, 150, 200));
    }
}
