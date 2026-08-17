using Wlrix.Toolchest.Services;
using Xunit;

namespace Wlrix.Toolchest.Tests;

/// <summary>
/// The pidfile side of Log Out. Nothing here signals anything: the point is which pids the
/// service is willing to reach for in the first place.
/// </summary>
public class SessionServiceTests
{
    [Theory]
    [InlineData("1234")]
    // The compositor writes a trailing newline.
    [InlineData("1234\n")]
    [InlineData("  1234  \n")]
    public void APidfileIsABareNumber(string text) => Assert.Equal(1234, SessionService.ParsePid(text));

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("not a pid")]
    [InlineData("12x")]
    [InlineData("-1")]
    [InlineData("3.5")]
    public void NonsenseInThePidfileIsNotAPid(string text) => Assert.Null(SessionService.ParsePid(text));

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    public void PidZeroAndOneAreRefused(string text)
    {
        // 0 signals the whole process group and 1 is init; a pidfile naming either is corrupt,
        // and acting on it would be far worse than doing nothing.
        Assert.Null(SessionService.ParsePid(text));
    }

    [Fact]
    public void ThePidfileSitsBesideTheCompositorsLog()
    {
        // Kept in step with wlrix-compositor/src/pidfile.rs by hand, so a change there that is
        // not mirrored here would make Log Out silently stop working.
        Assert.EndsWith("wlrix-compositor.pid", SessionService.PidFile(), StringComparison.Ordinal);
    }
}
