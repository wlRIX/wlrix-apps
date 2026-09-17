using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// The grid arithmetic. This lives in Core precisely so it can be tested: the panel that uses
/// it is an Avalonia control and CI has no display, but every rule that is easy to get wrong
/// is in here.
/// </summary>
public class IconGridMetricsTests
{
    // 96x104 cells with an 8px gap and a 12px margin: wlrix-desktop's defaults, so the file
    // manager's grid and the desktop's agree.
    private static IconGridMetrics Grid() => new(96, 104, gap: 8, margin: 12);

    [Fact]
    public void ColumnCountAccountsForGapsAndMargins()
    {
        var grid = Grid();
        // 400 wide: 400 - 24 margins + 8 (n gaps is one fewer than n cells) = 384, / 104 = 3.
        Assert.Equal(3, grid.Columns(400));
        // 504: 488 / 104 = 4. Five would need 5*96 + 4*8 + 24 = 536.
        Assert.Equal(4, grid.Columns(504));
        Assert.Equal(5, grid.Columns(536));
        // 600 still holds five; six would need 640.
        Assert.Equal(5, grid.Columns(600));
    }

    [Fact]
    public void ANarrowViewportStillGivesOneColumnRatherThanZero()
    {
        // Zero would divide by zero in every caller. A single overflowing column is the
        // better failure, and it is what a window dragged very narrow should do.
        var grid = Grid();
        Assert.Equal(1, grid.Columns(10));
        Assert.Equal(1, grid.Columns(0));
        Assert.Equal(1, grid.Columns(-50));
    }

    [Fact]
    public void RowsRoundUpForAPartialLastRow()
    {
        var grid = Grid();
        Assert.Equal(0, grid.Rows(0, 400));
        Assert.Equal(1, grid.Rows(1, 400));
        Assert.Equal(1, grid.Rows(3, 400));
        Assert.Equal(2, grid.Rows(4, 400));
        Assert.Equal(4, grid.Rows(10, 400));
    }

    [Fact]
    public void ExtentIsRowsPlusInteriorGapsPlusBothMargins()
    {
        var grid = Grid();
        Assert.Equal(0, grid.ExtentHeight(0, 400));
        // One row: 104 + 24 margins.
        Assert.Equal(128, grid.ExtentHeight(1, 400));
        // Two rows: 208 + one 8px gap + 24.
        Assert.Equal(240, grid.ExtentHeight(4, 400));
    }

    [Fact]
    public void CellsAreLaidOutLeftToRightThenDown()
    {
        var grid = Grid();
        Assert.Equal((12d, 12d), grid.CellOrigin(0, 400));
        Assert.Equal((116d, 12d), grid.CellOrigin(1, 400));
        Assert.Equal((220d, 12d), grid.CellOrigin(2, 400));
        // Wraps to the second row.
        Assert.Equal((12d, 124d), grid.CellOrigin(3, 400));
    }

    [Fact]
    public void OnlyTheVisibleRowsAreRealizedForALargeGrid()
    {
        // The entire point of the panel: a hundred thousand items must not mean a hundred
        // thousand containers.
        var grid = Grid();
        var (first, count) = grid.VisibleRange(100_000, 400, scrollOffset: 0, viewportHeight: 600, overscanRows: 0);
        Assert.Equal(0, first);
        // 600px of viewport holds six 112px rows fully and part of a seventh, times 3 columns.
        Assert.InRange(count, 18, 24);
    }

    [Fact]
    public void ScrollingMovesTheVisibleWindowByWholeRows()
    {
        var grid = Grid();
        var (first, _) = grid.VisibleRange(100_000, 400, scrollOffset: 1120, viewportHeight: 600, overscanRows: 0);
        // The margin sits above the first row, so it comes off the offset before dividing:
        // (1120 - 12) / 112 = row 9, and three columns makes that item 27.
        Assert.Equal(27, first);
    }

    [Fact]
    public void OverscanRealizesARowEitherSideSoFastScrollingShowsNoGaps()
    {
        var grid = Grid();
        var (withOut, countWithout) = grid.VisibleRange(100_000, 400, 1120, 600, overscanRows: 0);
        var (with, countWith) = grid.VisibleRange(100_000, 400, 1120, 600, overscanRows: 1);
        Assert.Equal(withOut - 3, with);
        Assert.Equal(countWithout + 6, countWith);
    }

    [Fact]
    public void TheVisibleRangeIsClampedToTheItemsThatExist()
    {
        var grid = Grid();
        var (first, count) = grid.VisibleRange(5, 400, scrollOffset: 0, viewportHeight: 600);
        Assert.Equal(0, first);
        Assert.Equal(5, count);
    }

    [Fact]
    public void ScrollingPastTheEndYieldsNothingRatherThanANegativeRange()
    {
        var grid = Grid();
        var (_, count) = grid.VisibleRange(5, 400, scrollOffset: 10_000, viewportHeight: 600);
        Assert.Equal(0, count);
        Assert.Equal((0, 0), grid.VisibleRange(0, 400, 0, 600));
    }

    [Fact]
    public void ScrollToItemMovesTheMinimumDistanceInEitherDirection()
    {
        var grid = Grid();

        // Already visible: do not move at all.
        Assert.Equal(0, grid.ScrollToItem(0, 400, 600, currentOffset: 0));

        // Below the viewport: comes to the bottom edge, not the middle.
        var down = grid.ScrollToItem(30, 400, 600, currentOffset: 0);
        Assert.True(down > 0);
        Assert.Equal(grid.CellOrigin(30, 400).Y + grid.CellHeight + grid.Margin - 600, down);

        // Above it: comes to the top edge.
        var up = grid.ScrollToItem(0, 400, 600, currentOffset: 2000);
        Assert.Equal(0, up);
    }

    [Fact]
    public void HitTestingFindsTheItemUnderAPoint()
    {
        var grid = Grid();
        Assert.Equal(0, grid.IndexAt(20, 20, 10, 400));
        Assert.Equal(1, grid.IndexAt(120, 20, 10, 400));
        Assert.Equal(3, grid.IndexAt(20, 130, 10, 400));
    }

    [Fact]
    public void TheGapBetweenIconsIsNotAnItem()
    {
        // Returning the nearest item here would make it impossible to start a rubber-band
        // drag in the space between two icons, which is the usual way to start one.
        var grid = Grid();
        Assert.Equal(-1, grid.IndexAt(110, 20, 10, 400));   // in the 8px gap between columns
        Assert.Equal(-1, grid.IndexAt(20, 119, 10, 400));   // in the gap between rows
        Assert.Equal(-1, grid.IndexAt(4, 4, 10, 400));      // in the margin
    }

    [Fact]
    public void HitTestingPastTheLastItemFindsNothing()
    {
        var grid = Grid();
        Assert.Equal(-1, grid.IndexAt(220, 20, 2, 400));
        Assert.Equal(-1, grid.IndexAt(20, 20, 0, 400));
    }

    [Fact]
    public void ABandSelectsEveryIconItTouchesNotOnlyThoseItSwallows()
    {
        // Brushing an icon selects it. Requiring the band to contain a cell whole would make
        // selecting a row of them needlessly fussy.
        var grid = Grid();
        var touched = grid.IndicesIn(90, 10, 40, 40, itemCount: 10, viewportWidth: 400).ToList();
        Assert.Equal([0, 1], touched);
    }

    [Fact]
    public void ABandInEmptySpaceSelectsNothing()
    {
        var grid = Grid();
        // Entirely inside the gap between the first two columns.
        Assert.Empty(grid.IndicesIn(109, 20, 6, 20, 10, 400));
        Assert.Empty(grid.IndicesIn(0, 0, 0, 0, 10, 400));
    }

    [Fact]
    public void ABandAcrossTheWholeGridSelectsEverything()
    {
        var grid = Grid();
        Assert.Equal(Enumerable.Range(0, 7), grid.IndicesIn(0, 0, 5000, 5000, 7, 400));
    }

    [Fact]
    public void ABandSpanningTwoRowsPicksUpBoth()
    {
        var grid = Grid();
        var touched = grid.IndicesIn(10, 100, 110, 40, itemCount: 10, viewportWidth: 400).ToList();
        // Bottom of row 0 (items 0,1) and top of row 1 (items 3,4).
        Assert.Equal([0, 1, 3, 4], touched);
    }

    [Fact]
    public void ADegenerateCellSizeIsClampedRatherThanDividingByZero()
    {
        var grid = new IconGridMetrics(0, -5);
        Assert.Equal(1, grid.CellWidth);
        Assert.Equal(1, grid.CellHeight);
        Assert.True(grid.Columns(100) > 0);
    }
}
