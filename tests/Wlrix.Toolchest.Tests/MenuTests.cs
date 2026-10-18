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
    public void TheDesktopMenuIsCustomizeDesksAudioAndClipboardThenTerminalThenLogOut() =>
        Assert.Equal(
            [Strings.Customize, Strings.ExtraDesks, Strings.ControlAudio, Strings.ClipboardHistory, Strings.OpenTerminal, Strings.LogOut],
            HeadersUnder(Strings.Desktop));

    [Fact]
    public void CustomizeHoldsTheDesktopsOwnPanels()
    {
        // IRIX's Desktop → Customize submenu, ahead of Desks as on IRIX: behavior, then looks,
        // then the mouse and keyboard, with a rule between each group.
        var customize = Assert.Single(ItemsUnder(Strings.Desktop), node => node.Header == Strings.Customize);
        Assert.Equal(
            [Strings.Windows, Strings.Sounds, null, Strings.ColorSchemes, null, Strings.Mouse, Strings.Keyboard],
            customize.Children!.Select(item => item.IsSeparator ? null : item.Header));
        Assert.All(customize.Children!.Where(item => !item.IsSeparator), item => Assert.NotNull(item.Command));
    }

    [Fact]
    public void TheDesktopMenuIsInThreeGroups()
    {
        // The desktop's own panels together, then the terminal, then Log Out, with a rule between
        // the groups and none at either end: Log Out in particular should not sit flush against
        // the item above it.
        Assert.Equal([false, false, false, false, true, false, true, false],
            ItemsUnder(Strings.Desktop).Select(item => item.IsSeparator));
    }

    [Fact]
    public void TheSystemMenuIsSoftwareThenPropertiesThenTheMachine() =>
        // Three groups: installing software, the machine's own settings, and ending everything
        // running on the box.
        Assert.Equal(
            [Strings.ManageSoftware, null, Strings.DisplayProperties, Strings.NetworkProperties, Strings.LoginProperties, null,
                Strings.RestartSystem, Strings.ShutDownSystem],
            ItemsUnder(Strings.System).Select(item => item.IsSeparator ? null : item.Header));

    [Fact]
    public void RestartAndShutDownBothOpenTheShutDownDialog()
    {
        // They were grayed out for as long as there was nowhere for them to go. There is now:
        // wlrix-shutdown, which asks before anything happens to the machine.
        foreach (var header in new[] { Strings.RestartSystem, Strings.ShutDownSystem })
        {
            var item = Assert.Single(ItemsUnder(Strings.System), node => node.Header == header);
            Assert.True(item.IsEnabled, header);
            Assert.NotNull(item.Command);
        }
    }

    [Fact]
    public void EveryEnabledItemDoesSomething()
    {
        foreach (var menu in new[] { Strings.Desktop, Strings.System })
        foreach (var item in Leaves(ItemsUnder(menu)).Where(item => !item.IsSeparator && item.IsEnabled))
            Assert.True(item.Command is not null, $"{menu} → {item.Header}");
    }

    /// <summary>The items that act: a submenu's children in place of the submenu itself.</summary>
    private static IEnumerable<MenuNode> Leaves(IEnumerable<MenuNode> items)
    {
        foreach (var item in items)
        {
            if (item.Children is { } children)
            {
                foreach (var leaf in Leaves(children))
                    yield return leaf;
            }
            else
            {
                yield return item;
            }
        }
    }
}
