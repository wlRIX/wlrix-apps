using Wlrix.Common.Desktop;
using Wlrix.Files.Core.Platform;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// Which applications an Open With menu offers, and in what order.
/// </summary>
/// <remarks>
/// The order is the whole value: the configured default first, then anything the user added by
/// hand, then whatever registered itself. Get it wrong and the menu is a pile of applications
/// with the one you want somewhere in it.
/// </remarks>
public class ApplicationHandlerTests
{
    private static DesktopEntry App(
        string id, string name, string[] mimeTypes,
        bool hidden = false, bool noDisplay = false, string? exec = "given") => new()
    {
        Id = id,
        Name = new Dictionary<string, string> { [string.Empty] = name },
        Exec = exec is null ? null : $"{name.ToLowerInvariant()} %f",
        MimeType = mimeTypes,
        Hidden = hidden,
        NoDisplay = noDisplay
    };

    private static string Write(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), "wlrix-handlers", Guid.NewGuid().ToString("N"), "mimeapps.list");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    /// <summary>A made-up inheritance graph, so this does not depend on shared-mime-info.</summary>
    private sealed class Inheritance : SharedMime
    {
        public override IReadOnlyList<string> Ancestry(string mimeType) => mimeType switch
        {
            "text/x-csrc" => ["text/x-csrc", "text/plain"],
            _ => [mimeType]
        };
    }

    [Fact]
    public void AnApplicationThatClaimsTheTypeIsOffered()
    {
        var handlers = new ApplicationHandlers([App("micro.desktop", "Micro", ["text/plain"])], Write(""));

        var found = Assert.Single(handlers.For("text/plain"));
        Assert.Equal("micro.desktop", found.Id);
    }

    [Fact]
    public void TheConfiguredDefaultComesFirst()
    {
        var entries = new[]
        {
            App("aaa.desktop", "Aaa", ["text/plain"]),
            App("zzz.desktop", "Zzz", ["text/plain"])
        };
        var path = Write("""
            [Default Applications]
            text/plain=zzz.desktop;
            """);

        var handlers = new ApplicationHandlers(entries, path);
        Assert.Equal(["zzz.desktop", "aaa.desktop"], handlers.For("text/plain").Select(e => e.Id));
        Assert.Equal("zzz.desktop", handlers.DefaultFor("text/plain")!.Id);
    }

    [Fact]
    public void AnApplicationIsNotListedTwiceForBeingBothDefaultAndRegistered()
    {
        var path = Write("""
            [Default Applications]
            text/plain=micro.desktop;
            """);
        var handlers = new ApplicationHandlers([App("micro.desktop", "Micro", ["text/plain"])], path);

        Assert.Single(handlers.For("text/plain"));
    }

    [Fact]
    public void AnAddedAssociationOffersSomethingThatNeverClaimedTheType()
    {
        // How a user says "yes, I do want to open this in that", about an application whose
        // own MimeType= belongs to its package and cannot be edited.
        var path = Write("""
            [Added Associations]
            text/plain=gimp.desktop;
            """);
        var handlers = new ApplicationHandlers(
            [App("micro.desktop", "Micro", ["text/plain"]), App("gimp.desktop", "Gimp", ["image/png"])], path);

        Assert.Equal(["gimp.desktop", "micro.desktop"], handlers.For("text/plain").Select(e => e.Id));
    }

    [Fact]
    public void ARemovedAssociationTakesOneAway()
    {
        // The other half of the same story: an application claimed a type it is bad at, and
        // the user does not want it offered.
        var path = Write("""
            [Removed Associations]
            text/plain=gimp.desktop;
            """);
        var handlers = new ApplicationHandlers(
            [App("micro.desktop", "Micro", ["text/plain"]), App("gimp.desktop", "Gimp", ["text/plain"])], path);

        Assert.Equal(["micro.desktop"], handlers.For("text/plain").Select(e => e.Id));
    }

    [Fact]
    public void ASubtypeAlsoOffersWhatOpensTheTypeItInheritsFrom()
    {
        // A C source file is a text file, so the text editors belong on its menu -- under
        // anything that claims C specifically.
        var handlers = new ApplicationHandlers(
            [App("micro.desktop", "Micro", ["text/plain"]), App("ide.desktop", "Ide", ["text/x-csrc"])],
            Write(""), new Inheritance());

        Assert.Equal(["ide.desktop", "micro.desktop"], handlers.For("text/x-csrc").Select(e => e.Id));
    }

    [Fact]
    public void NothingHiddenOrUnrunnableIsOffered()
    {
        // Each of these would put a line in the menu that does nothing when clicked.
        var entries = new[]
        {
            App("good.desktop", "Good", ["text/plain"]),
            App("hidden.desktop", "Hidden", ["text/plain"], hidden: true),
            App("nodisplay.desktop", "NoDisplay", ["text/plain"], noDisplay: true),
            App("noexec.desktop", "NoExec", ["text/plain"], exec: null)
        };

        Assert.Equal(["good.desktop"],
            new ApplicationHandlers(entries, Write("")).For("text/plain").Select(e => e.Id));
    }

    [Fact]
    public void ATypeNothingClaimsOffersNothingRatherThanEverything()
    {
        var handlers = new ApplicationHandlers([App("micro.desktop", "Micro", ["text/plain"])], Write(""));

        Assert.Empty(handlers.For("application/x-nothing-opens-this"));
        Assert.Null(handlers.DefaultFor("application/x-nothing-opens-this"));
    }

    [Fact]
    public void SettingADefaultIsVisibleImmediatelyAndSurvivesAReread()
    {
        // Read from disk each time, because the user can change it from another application
        // or by editing the file, and a stale menu is worse than a few milliseconds.
        var entries = new[] { App("aaa.desktop", "Aaa", ["text/plain"]), App("zzz.desktop", "Zzz", ["text/plain"]) };
        var path = Write("");
        var handlers = new ApplicationHandlers(entries, path);

        Assert.Equal("aaa.desktop", handlers.DefaultFor("text/plain")!.Id);
        handlers.SetDefault("text/plain", entries[1]);
        Assert.Equal("zzz.desktop", handlers.DefaultFor("text/plain")!.Id);

        Assert.Equal("zzz.desktop", new ApplicationHandlers(entries, path).DefaultFor("text/plain")!.Id);
    }

    [Fact]
    public void TheFirstEntryOfADuplicatedIdWins()
    {
        // Scanning walks XDG_DATA_DIRS in order, so a user's own copy in ~/.local/share
        // shadows the packaged one of the same name rather than the other way round.
        var entries = new[]
        {
            App("micro.desktop", "Mine", ["text/plain"]),
            App("micro.desktop", "Packaged", ["text/plain"])
        };
        var handlers = new ApplicationHandlers(entries, Write(""));

        var found = Assert.Single(handlers.For("text/plain"));
        Assert.Equal("Mine", found.Name[string.Empty]);
    }
}
