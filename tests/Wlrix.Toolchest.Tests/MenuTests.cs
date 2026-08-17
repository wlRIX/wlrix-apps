using Wlrix.Toolchest.Localization;
using Wlrix.Toolchest.ViewModels;
using Xunit;

namespace Wlrix.Toolchest.Tests;

/// <summary>
/// The Toolchest's shape. Built through the design-time constructor, which takes no services:
/// the commands are closures that are never run here, so the tree can be inspected without a
/// launcher, a catalog or a display.
/// </summary>
public class MenuTests
{
    private static readonly MainWindowViewModel Menu = new();

    /// <summary>The items under the top-level menu headed <paramref name="header"/>.</summary>
    private static IReadOnlyList<MenuNode> ItemsUnder(string header) =>
        Assert.Single(Menu.TopLevel, top => top.Header == header).Children!;

    /// <summary>The items under <paramref name="header"/>, with the separators dropped.</summary>
    private static IEnumerable<string> HeadersUnder(string header) =>
        ItemsUnder(header).Where(item => !item.IsSeparator).Select(item => item.Header);

    [Fact]
    public void TheToolchestIsDesktopSystemApplicationsHelp()
    {
        Assert.Equal([Strings.Desktop, Strings.System, Strings.Applications, Strings.Help],
            Menu.TopLevel.Select(top => top.Header));
    }

    [Fact]
    public void TheDesktopMenuIsExtraDesksThenTerminalThenLogOut() =>
        Assert.Equal([Strings.ExtraDesks, Strings.OpenTerminal, Strings.LogOut],
            HeadersUnder(Strings.Desktop));

    [Fact]
    public void EachDesktopItemStandsInItsOwnGroup()
    {
        // A rule between every pair, and none at either end: the three do unrelated things, and
        // Log Out in particular should not sit flush against the item above it.
        Assert.Equal([false, true, false, true, false],
            ItemsUnder(Strings.Desktop).Select(item => item.IsSeparator));
    }

    [Fact]
    public void TheSystemMenuIsSoftwareManagerThenRestartThenShutDown() =>
        Assert.Equal([Strings.SoftwareManager, Strings.RestartSystem, Strings.ShutDownSystem],
            HeadersUnder(Strings.System));

    [Fact]
    public void TheSystemMenuSeparatesTheAppFromTheMachine()
    {
        // One rule, between launching an application and ending everything running on the box.
        Assert.Equal([false, true, false, false],
            ItemsUnder(Strings.System).Select(item => item.IsSeparator));
    }

    [Fact]
    public void RestartAndShutDownAreGreyedOutUntilTheyHaveADialog()
    {
        // Not merely command-less: an enabled item that does nothing when chosen reads as a bug,
        // so these have to *look* unavailable. See the note in BuildTopLevel.
        foreach (var header in new[] { Strings.RestartSystem, Strings.ShutDownSystem })
        {
            var item = Assert.Single(ItemsUnder(Strings.System), node => node.Header == header);
            Assert.False(item.IsEnabled, header);
            Assert.Null(item.Command);
        }
    }

    [Fact]
    public void EveryEnabledItemDoesSomething()
    {
        foreach (var menu in new[] { Strings.Desktop, Strings.System })
        foreach (var item in ItemsUnder(menu).Where(item => !item.IsSeparator && item.IsEnabled))
            Assert.True(item.Command is not null, $"{menu} → {item.Header}");
    }
}
