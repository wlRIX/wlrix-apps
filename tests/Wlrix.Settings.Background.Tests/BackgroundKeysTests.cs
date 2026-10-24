using Wlrix.Settings.Background.Services;
using Xunit;

namespace Wlrix.Settings.Background.Tests;

public class BackgroundKeysTests
{
    [Fact]
    public void TheLockKeysAreTheLockScreensBackgroundSection() =>
        Assert.Equal(
            ["lock.background.image", "lock.background.mode", "lock.background.color"],
            BackgroundKeys.Lock);

    private static Dictionary<string, string> Values(
        string image, string mode, string color, string lockImage, string lockMode, string lockColor) =>
        new()
        {
            ["background.image"] = image,
            ["background.mode"] = mode,
            ["background.color"] = color,
            ["lock.background.image"] = lockImage,
            ["lock.background.mode"] = lockMode,
            ["lock.background.color"] = lockColor,
        };

    [Fact]
    public void TheLockFollowsWhenAllThreeMatch()
    {
        Assert.True(BackgroundKeys.LockFollows(Values("/a.png", "tile", "#555555", "/a.png", "tile", "#555555")));
        // A color is the same color however it is cased.
        Assert.True(BackgroundKeys.LockFollows(Values("", "fill", "#AABBCC", "", "fill", "#aabbcc")));
    }

    [Fact]
    public void AnyDifferenceMeansTheLockHasItsOwn()
    {
        Assert.False(BackgroundKeys.LockFollows(Values("/a.png", "tile", "#555555", "/b.png", "tile", "#555555")));
        Assert.False(BackgroundKeys.LockFollows(Values("/a.png", "tile", "#555555", "/a.png", "fill", "#555555")));
        Assert.False(BackgroundKeys.LockFollows(Values("/a.png", "tile", "#555555", "/a.png", "tile", "#000000")));
    }

    [Fact]
    public void RevertWritesBackWhatWasSetAndRemovesWhatWasNot()
    {
        var open = new Dictionary<string, OpenValue>
        {
            // From /etc/wlrix: written back, because the user's file now shadows the system one.
            ["background.image"] = new("/usr/share/wlrix/wallpapers/scatter.png", IsDefault: false),
            ["background.mode"] = new("tile", IsDefault: false),
            // Set nowhere: removed again rather than pinned to today's default.
            ["background.color"] = new("#555555", IsDefault: true),
            // Absent, which is not the same as "": an absent lock image must stay absent.
            ["lock.background.image"] = new("", IsDefault: true),
        };

        var (sets, resets) = BackgroundKeys.RevertPlan(open,
            ["background.image", "background.color", "lock.background.image", "background.color"]);

        Assert.Equal(new Dictionary<string, object> { ["background.image"] = "/usr/share/wlrix/wallpapers/scatter.png" }, sets);
        Assert.Equal(["background.color", "lock.background.image"], resets);
    }

    [Fact]
    public void NothingTouchedIsNothingToRevert()
    {
        var open = new Dictionary<string, OpenValue> { ["background.mode"] = new("tile", IsDefault: false) };
        var (sets, resets) = BackgroundKeys.RevertPlan(open, []);
        Assert.Empty(sets);
        Assert.Empty(resets);
    }
}
