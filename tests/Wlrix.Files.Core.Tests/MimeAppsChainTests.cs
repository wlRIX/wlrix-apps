using Wlrix.Common.Desktop;
using Wlrix.Files.Core.Platform;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// How the user's, the administrator's and the packages' <c>mimeapps.list</c> files combine.
/// </summary>
/// <remarks>
/// Each group combines its own way, and getting one wrong shows up as Files and
/// <c>xdg-open</c> disagreeing about what a double-click opens.
/// </remarks>
public class MimeAppsChainTests
{
    private static DesktopEntry App(string id, params string[] mimeTypes) => new()
    {
        Id = id,
        Name = new Dictionary<string, string> { [string.Empty] = id },
        Exec = "run %f",
        MimeType = mimeTypes
    };

    [Fact]
    public void TheDesktopsOwnFileComesBeforeThePlainOneInEachDirectory()
    {
        var paths = MimeAppsChain.PathsFor("/home/u/.config", ["/etc/xdg"], "/home/u/.local/share", ["/usr/share"], ["wlRIX"]);

        Assert.Equal(
            [
                "/home/u/.config/wlrix-mimeapps.list",
                "/home/u/.config/mimeapps.list",
                "/etc/xdg/wlrix-mimeapps.list",
                "/etc/xdg/mimeapps.list",
                "/home/u/.local/share/applications/wlrix-mimeapps.list",
                "/home/u/.local/share/applications/mimeapps.list",
                "/usr/share/applications/wlrix-mimeapps.list",
                "/usr/share/applications/mimeapps.list"
            ],
            paths);
    }

    [Fact]
    public void ASystemDefaultIsHonoredWhenTheUserHasNone()
    {
        using var dir = new TemporaryDirectory();
        var user = dir.File("user/mimeapps.list", "");
        var system = dir.File("etc/mimeapps.list", """
            [Default Applications]
            text/plain=writer.desktop;
            """);

        var handlers = new ApplicationHandlers(
            [App("aaa.desktop", "text/plain"), App("writer.desktop", "text/plain")], user, chainPaths: [user, system]);

        Assert.Equal("writer.desktop", handlers.DefaultFor("text/plain")!.Id);
    }

    [Fact]
    public void TheUsersDefaultBeatsTheSystems()
    {
        using var dir = new TemporaryDirectory();
        var user = dir.File("user/mimeapps.list", """
            [Default Applications]
            text/plain=aaa.desktop;
            """);
        var system = dir.File("etc/mimeapps.list", """
            [Default Applications]
            text/plain=writer.desktop;
            """);

        var chain = MimeAppsChain.Read([user, system]);
        Assert.Equal(["aaa.desktop", "writer.desktop"], chain.Defaults("text/plain"));
    }

    [Fact]
    public void ARemovalHidesWhatIsBelowItButNotWhatIsAbove()
    {
        using var dir = new TemporaryDirectory();
        var user = dir.File("user/mimeapps.list", """
            [Added Associations]
            text/plain=gimp.desktop;
            """);
        var middle = dir.File("etc/mimeapps.list", """
            [Removed Associations]
            text/plain=gimp.desktop;emacs.desktop;
            """);
        var bottom = dir.File("usr/mimeapps.list", """
            [Added Associations]
            text/plain=emacs.desktop;
            """);

        var chain = MimeAppsChain.Read([user, middle, bottom]);

        Assert.Equal(["gimp.desktop"], chain.Added("text/plain"));
        Assert.Contains("emacs.desktop", chain.Removed("text/plain"));
    }

    [Fact]
    public void TheBaselineLeavesTheUsersFileOut()
    {
        // What an editor of the preference order writes against: the user's own removal is
        // the thing being edited, so it must not already be applied.
        using var dir = new TemporaryDirectory();
        var user = dir.File("user/mimeapps.list", """
            [Removed Associations]
            text/plain=micro.desktop;
            """);
        var handlers = new ApplicationHandlers([App("micro.desktop", "text/plain")], user, chainPaths: [user]);

        Assert.Equal(["micro.desktop"], handlers.Associated("text/plain"));
        Assert.Empty(handlers.Associated("text/plain", includeUserFile: true));
    }
}
