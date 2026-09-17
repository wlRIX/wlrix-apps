using Wlrix.Files.Core.Dnd;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>The auto-scroll ramp. Arithmetic, so it can be pinned exactly.</summary>
public class EdgeScrollTests
{
    private const double Extent = 600;

    [Fact]
    public void TheMiddleOfTheViewportDoesNotScroll()
    {
        Assert.Equal(0, EdgeScroll.Rate(300, Extent));
        Assert.Equal(0, EdgeScroll.Rate(EdgeScroll.DefaultMargin + 1, Extent));
        Assert.Equal(0, EdgeScroll.Rate(Extent - EdgeScroll.DefaultMargin - 1, Extent));
    }

    [Fact]
    public void TheTopEdgeScrollsBackAndTheBottomEdgeScrollsForward()
    {
        Assert.True(EdgeScroll.Rate(0, Extent) < 0);
        Assert.True(EdgeScroll.Rate(Extent, Extent) > 0);
    }

    [Fact]
    public void TheRateRampsWithDepthIntoTheBand()
    {
        var margin = 40.0;
        var shallow = Math.Abs(EdgeScroll.Rate(margin - 4, Extent, margin));
        var deep = Math.Abs(EdgeScroll.Rate(4, Extent, margin));

        Assert.True(shallow > 0);
        Assert.True(deep > shallow * 4, $"deep {deep} should far exceed shallow {shallow}");
        Assert.Equal(EdgeScroll.DefaultMaxRate, Math.Abs(EdgeScroll.Rate(0, Extent, margin)), 3);
    }

    [Fact]
    public void APointerDraggedRightOutOfTheViewportKeepsScrollingAtFullSpeed()
    {
        // Not a curiosity: it is what happens the instant the user overshoots, and stopping
        // dead there is what makes auto-scroll feel broken.
        Assert.Equal(-EdgeScroll.DefaultMaxRate, EdgeScroll.Rate(-500, Extent), 3);
        Assert.Equal(EdgeScroll.DefaultMaxRate, EdgeScroll.Rate(Extent + 500, Extent), 3);
    }

    [Fact]
    public void AViewportShorterThanTwoMarginsStillScrollsBothWays()
    {
        // With the bands left at full depth they would overlap, and a pointer in the middle
        // would be in both at once — where the two answers cancel and nothing moves.
        var tiny = 30.0;
        Assert.True(EdgeScroll.Rate(1, tiny, margin: 100) < 0);
        Assert.True(EdgeScroll.Rate(29, tiny, margin: 100) > 0);
    }

    [Fact]
    public void ADegenerateViewportScrollsNothingRatherThanDividingByZero()
    {
        Assert.Equal(0, EdgeScroll.Rate(10, 0));
        Assert.Equal(0, EdgeScroll.Rate(10, Extent, margin: 0));
        Assert.Equal(0, EdgeScroll.Rate(10, Extent, maxRate: 0));
    }

    [Fact]
    public void TheDeltaIsTheRateOverTheElapsedTime()
    {
        Assert.Equal(90, EdgeScroll.Delta(900, TimeSpan.FromMilliseconds(100)), 6);
        // A tick that somehow arrives before the last one must not scroll backwards.
        Assert.Equal(0, EdgeScroll.Delta(900, TimeSpan.FromMilliseconds(-50)));
    }
}
