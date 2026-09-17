using Wlrix.Files.Core.Dnd;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// Hover-to-open. Driven by an explicit clock precisely so these run instantly rather than
/// spending most of a second each waiting for a real timer.
/// </summary>
public class SpringLoadTests
{
    private static readonly Location Downloads = Location.Parse("/home/vic/Downloads");
    private static readonly Location Documents = Location.Parse("/home/vic/Documents");
    private static readonly DateTimeOffset Start = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RestingOnAFolderForTheDelayOpensIt()
    {
        var spring = new SpringLoad(TimeSpan.FromMilliseconds(800));

        Assert.Null(spring.Update(Downloads, Start));
        Assert.Null(spring.Update(Downloads, Start.AddMilliseconds(799)));
        Assert.Equal(Downloads, spring.Update(Downloads, Start.AddMilliseconds(800)));
    }

    [Fact]
    public void CrossingAFolderOnTheWaySomewhereElseDoesNotOpenIt()
    {
        var spring = new SpringLoad();

        spring.Update(Downloads, Start);
        spring.Update(Downloads, Start.AddMilliseconds(200));
        Assert.Null(spring.Update(Documents, Start.AddMilliseconds(300)));
        // The clock restarted on the new folder, so the time already spent on the old one
        // does not count towards this one.
        Assert.Null(spring.Update(Documents, Start.AddMilliseconds(900)));
        Assert.Equal(Documents, spring.Update(Documents, Start.AddMilliseconds(1100)));
    }

    [Fact]
    public void AFolderOpensOnceRatherThanOnEveryEventAfterTheDelay()
    {
        // Drag-over fires continuously while the pointer is held still. Without the latch,
        // every one of them after 800 ms would navigate again.
        var spring = new SpringLoad();

        spring.Update(Downloads, Start);
        Assert.Equal(Downloads, spring.Update(Downloads, Start.AddSeconds(1)));
        Assert.Null(spring.Update(Downloads, Start.AddSeconds(2)));
        Assert.Null(spring.Update(Downloads, Start.AddSeconds(5)));
    }

    [Fact]
    public void MovingAwayAndBackStartsAFreshRest()
    {
        var spring = new SpringLoad();

        spring.Update(Downloads, Start);
        Assert.Equal(Downloads, spring.Update(Downloads, Start.AddSeconds(1)));

        spring.Update(null, Start.AddSeconds(2));
        spring.Update(Downloads, Start.AddSeconds(3));
        Assert.Equal(Downloads, spring.Update(Downloads, Start.AddSeconds(4)));
    }

    [Fact]
    public void HoveringOverNothingNeverOpensAnything()
    {
        var spring = new SpringLoad();

        Assert.Null(spring.Update(null, Start));
        Assert.Null(spring.Update(null, Start.AddSeconds(10)));
        Assert.Null(spring.Hovering);
    }

    [Fact]
    public void ResettingForgetsTheRestSoTheNextDragStartsClean()
    {
        var spring = new SpringLoad();

        spring.Update(Downloads, Start);
        spring.Reset();
        Assert.Null(spring.Hovering);
        // The clock is only restarted by the next Update, so a stale timestamp from the
        // previous drag must not make the first event of the next one fire immediately.
        Assert.Null(spring.Update(Downloads, Start.AddSeconds(30)));
        Assert.Equal(Downloads, spring.Update(Downloads, Start.AddSeconds(31)));
    }

    [Fact]
    public void TheHoveredFolderIsVisibleSoTheViewCanHighlightIt()
    {
        var spring = new SpringLoad();

        spring.Update(Downloads, Start);
        Assert.Equal(Downloads, spring.Hovering);
        spring.Update(Documents, Start.AddMilliseconds(100));
        Assert.Equal(Documents, spring.Hovering);
    }
}
