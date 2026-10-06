using Wlrix.Settings.Displays.Layout;
using Wlrix.Settings.Displays.Models;
using Xunit;

namespace Wlrix.Settings.Displays.Tests;

public class ArrangementMathTests
{
    private static readonly Box Wide = new(0, 0, 2560, 1440);

    [Theory]
    [InlineData(OutputTransform.Normal, 1.0, 2560, 1440)]
    [InlineData(OutputTransform.Rotate90, 1.0, 1440, 2560)]
    [InlineData(OutputTransform.Rotate180, 2.0, 1280, 720)]
    [InlineData(OutputTransform.Flipped270, 1.0, 1440, 2560)]
    [InlineData(OutputTransform.Normal, 1.5, 1707, 960)]
    public void LogicalSizeTurnsAndScales(OutputTransform transform, double scale, int width, int height) =>
        Assert.Equal((width, height), ArrangementMath.LogicalSize(2560, 1440, transform, scale));

    [Fact]
    public void ADisplayDroppedToTheRightLandsAgainstTheEdge()
    {
        var snapped = ArrangementMath.Snap(new Box(2700, 600, 1920, 1080), [Wide], align: 64);
        Assert.Equal(new Box(2560, 600, 1920, 1080), snapped);
    }

    [Fact]
    public void ADisplayDroppedOverlappingIsPushedOut()
    {
        // Over the left edge: the nearest free place is beside it, not above or below.
        var snapped = ArrangementMath.Snap(new Box(-1200, 300, 1920, 1080), [Wide], align: 64);
        Assert.Equal(-1920, snapped.X);
        Assert.False(snapped.Overlaps(Wide));
        Assert.True(snapped.Touches(Wide));
    }

    [Fact]
    public void ADisplayDroppedBelowLandsBelow()
    {
        var snapped = ArrangementMath.Snap(new Box(900, 1600, 1920, 1080), [Wide], align: 64);
        Assert.Equal(new Box(900, 1440, 1920, 1080), snapped);
    }

    [Fact]
    public void ADisplayDroppedAboveLandsAbove()
    {
        var snapped = ArrangementMath.Snap(new Box(100, -1200, 1920, 1080), [Wide], align: 64);
        Assert.Equal(new Box(100, -1080, 1920, 1080), snapped);
    }

    [Fact]
    public void NearlyAlignedEdgesArePulledIntoLine()
    {
        // Within the threshold of the bottom edges lining up.
        var snapped = ArrangementMath.Snap(new Box(2600, 330, 1920, 1080), [Wide], align: 64);
        Assert.Equal(1440 - 1080, snapped.Y);
    }

    [Fact]
    public void ADisplayDroppedFarAwayStillMeetsTheOthers()
    {
        var snapped = ArrangementMath.Snap(new Box(9000, 9000, 1920, 1080), [Wide], align: 64);
        Assert.True(snapped.Touches(Wide));
        Assert.False(snapped.Overlaps(Wide));
    }

    [Fact]
    public void GrowingADisplayPushesItsNeighborOut()
    {
        // The left display grows from 1920 to 2560 wide, into its neighbor.
        Box[] boxes = [new(0, 0, 2560, 1440), new(1920, 0, 1920, 1080)];
        var settled = ArrangementMath.Settle(boxes, fixedIndex: 0, align: 64);
        Assert.False(settled[0].Overlaps(settled[1]));
        Assert.True(settled[0].Touches(settled[1]));
        Assert.Equal(2560, settled[1].X);
    }

    [Fact]
    public void ShrinkingADisplayClosesTheGap()
    {
        Box[] boxes = [new(0, 0, 1280, 720), new(2560, 0, 1920, 1080)];
        var settled = ArrangementMath.Settle(boxes, fixedIndex: 0, align: 64);
        Assert.True(settled[0].Touches(settled[1]));
    }

    [Fact]
    public void MovingTheMiddleDisplayKeepsTheOthersConnected()
    {
        // A B C in a row, and B is moved under A: C has nothing to meet any more.
        Box[] boxes = [new(0, 0, 1000, 500), new(0, 500, 1000, 500), new(2000, 0, 1000, 500)];
        var settled = ArrangementMath.Settle(boxes, fixedIndex: 1, align: 64);
        for (var i = 0; i < settled.Length; i++)
        {
            Assert.Contains(settled.Where((_, j) => j != i), other => other.Touches(settled[i]));
            Assert.DoesNotContain(settled.Where((_, j) => j != i), other => other.Overlaps(settled[i]));
        }
    }

    [Fact]
    public void NormalizeStartsTheLayoutAtTheOrigin()
    {
        var normalized = ArrangementMath.Normalize([new Box(-1920, 100, 1920, 1080), new Box(0, 0, 2560, 1440)]);
        Assert.Equal(new Box(0, 100, 1920, 1080), normalized[0]);
        Assert.Equal(new Box(1920, 0, 2560, 1440), normalized[1]);
    }

    [Fact]
    public void CornersDoNotCountAsTouching()
    {
        Assert.False(new Box(0, 0, 10, 10).Touches(new Box(10, 10, 10, 10)));
        Assert.True(new Box(0, 0, 10, 10).Touches(new Box(10, 9, 10, 10)));
    }
}
