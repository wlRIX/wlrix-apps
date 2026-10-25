using Wlrix.Common.Desktop;
using Wlrix.Settings.Utilities.Services;
using Xunit;

namespace Wlrix.Settings.Utilities.Tests;

/// <summary>Which types each menu's choice sets.</summary>
public class RoleTests
{
    private static DesktopEntry App(string id, string[] mimeTypes, string[]? categories = null) => new()
    {
        Id = id,
        Name = new Dictionary<string, string> { [string.Empty] = id },
        Exec = "run %f",
        MimeType = mimeTypes,
        Categories = categories ?? [],
    };

    private static UtilityRole Role(string key) => UtilityRoles.All.Single(role => role.Key == key);

    [Fact]
    public void AnImageViewerTakesEveryImageTypeItOpensAndNothingElse()
    {
        var viewer = App("imv.desktop", ["image/png", "image/jpeg", "image/webp", "application/pdf"]);

        Assert.Equal(["image/png", "image/jpeg", "image/webp"], Role("RoleImageViewer").TypesFor(viewer));
    }

    [Fact]
    public void ABrowserPickedWithOtherStillReceivesTheLinks()
    {
        // It claims nothing, but choosing it as the browser has to mean something.
        var browser = App("odd.desktop", []);

        Assert.Equal(["x-scheme-handler/http", "x-scheme-handler/https"], Role("RoleBrowser").TypesFor(browser));
    }

    [Fact]
    public void AnArchiverTakesArchivesButNotEveryApplicationType()
    {
        var ark = App("ark.desktop", ["application/zip", "application/x-7z-compressed", "application/x-executable"]);

        Assert.Equal(["application/zip", "application/x-7z-compressed"], Role("RoleArchiver").TypesFor(ark));
    }

    [Fact]
    public void TheTerminalMenuOffersTerminalEmulators()
    {
        var terminal = Role("RoleTerminal");

        Assert.True(terminal.Offers(App("foot.desktop", [], ["System", "TerminalEmulator"])));
        Assert.False(terminal.Offers(App("micro.desktop", ["text/plain"], ["Utility", "TextEditor"])));
        Assert.Empty(terminal.TypesFor(App("foot.desktop", [])));
    }

    [Fact]
    public void EveryRoleHasAGroupAndTheMimeRolesAType() =>
        Assert.All(UtilityRoles.All, role =>
        {
            Assert.False(string.IsNullOrEmpty(role.Group));
            if (role.Kind == RoleKind.Mime)
                Assert.Contains(role.PrimaryType, role.CoreTypes);
        });
}
