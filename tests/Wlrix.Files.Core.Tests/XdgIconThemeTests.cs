using Wlrix.Files.Core.Icons;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// Resolution against a synthetic theme tree, so the size-distance and inheritance rules are
/// pinned without depending on Adwaita being installed or on its layout staying put.
/// </summary>
public class XdgIconThemeTests
{
    private static string Root => Path.Combine(AppContext.BaseDirectory, "Fixtures", "icons");

    private static XdgIconTheme Theme(string name = "Child") =>
        new([Root], [Path.Combine(Root, "pixmaps")]) { Theme = name };

    private static string Relative(string? path) =>
        path is null ? "(none)" : Path.GetRelativePath(Root, path);

    [Fact]
    public void AnExactSizeMatchIsPreferred()
    {
        Assert.Equal("Child/16x16/mimetypes/text-plain.png", Relative(Theme().Lookup("text-plain", 16)));
        Assert.Equal("Child/48x48/mimetypes/text-plain.png", Relative(Theme().Lookup("text-plain", 48)));
    }

    [Fact]
    public void AThresholdDirectoryMatchesWithinItsThreshold()
    {
        // The 48px directory declares Threshold=4, so 45 and 52 are still exact matches and
        // must not fall through to the scalable one.
        Assert.Equal("Child/48x48/mimetypes/text-plain.png", Relative(Theme().Lookup("text-plain", 45)));
        Assert.Equal("Child/48x48/mimetypes/text-plain.png", Relative(Theme().Lookup("text-plain", 52)));
    }

    [Fact]
    public void AScalableDirectoryCoversItsWholeRange()
    {
        Assert.Equal("Child/scalable/mimetypes/text-plain.svg", Relative(Theme().Lookup("text-plain", 256)));
    }

    [Fact]
    public void WithNoExactMatchTheClosestSizeIsUsed()
    {
        // 24px matches no directory: 16 is Fixed, 48 is out of threshold, and scalable
        // starts at 64. The nearest is 16, eight away, against 48's twenty.
        Assert.Equal("Child/16x16/mimetypes/text-plain.png", Relative(Theme().Lookup("text-plain", 24)));
    }

    [Fact]
    public void TheScaleIsPartOfTheMatchNotJustTheSize()
    {
        // A 64@2x directory serves a 64px request at scale 2 and must not answer scale 1,
        // or a HiDPI icon would be drawn at half size on an ordinary display.
        Assert.Equal("Child/64x64@2/mimetypes/text-plain.png", Relative(Theme().Lookup("text-plain", 64, scale: 2)));
        Assert.NotEqual("Child/64x64@2/mimetypes/text-plain.png", Relative(Theme().Lookup("text-plain", 64)));
    }

    [Fact]
    public void RasterBeatsScalableAtTheSameSize()
    {
        // A theme author who shipped a hand-tuned 16px png meant it to be used.
        Assert.Equal("Child/16x16/mimetypes/both-formats.png", Relative(Theme().Lookup("both-formats", 16)));
    }

    [Fact]
    public void AMissingNameFallsThroughInheritsThenHicolorThenPixmaps()
    {
        var theme = Theme();
        Assert.Equal("Child/48x48/mimetypes/only-in-child.png", Relative(theme.Lookup("only-in-child", 48)));
        Assert.Equal("Parent/32x32/mimetypes/only-in-parent.png", Relative(theme.Lookup("only-in-parent", 32)));
        Assert.Equal("hicolor/48x48/apps/only-in-hicolor.png", Relative(theme.Lookup("only-in-hicolor", 48)));
        Assert.Equal("pixmaps/only-in-pixmaps.png", Relative(theme.Lookup("only-in-pixmaps", 48)));
    }

    [Fact]
    public void HicolorIsSearchedEvenWhenTheConfiguredThemeDoesNotInheritIt()
    {
        // Parent's chain does name hicolor, but a theme that forgot to would still have to
        // find an application's own installed icon.
        Assert.Equal("hicolor/48x48/apps/only-in-hicolor.png", Relative(Theme("Parent").Lookup("only-in-hicolor", 48)));
    }

    [Fact]
    public void AnUnknownNameResolvesToNothing()
    {
        Assert.Null(Theme().Lookup("wlrix-no-such-icon-anywhere", 48));
        Assert.Null(Theme().Lookup("", 48));
    }

    [Fact]
    public void AnAbsolutePathIsTakenAsIs()
    {
        // Which is what a .desktop file's Icon= is allowed to hold.
        var real = Path.Combine(Root, "pixmaps", "only-in-pixmaps.png");
        Assert.Equal(real, Theme().Lookup(real, 48));
        Assert.Null(Theme().Lookup("/nonexistent/icon.png", 48));
    }

    [Fact]
    public void ChangingTheThemeForgetsWhatTheLastOneCouldNotFind()
    {
        // The negative entries are exactly the names a new theme would resolve, so keeping
        // them across a change would make the setting appear to do nothing. This is the
        // lesson wlrix-desktop's icon_theme.rs records, ported along with the code.
        var theme = Theme("Parent");
        Assert.Null(theme.Lookup("only-in-child", 48));

        theme.Theme = "Child";
        Assert.Equal("Child/48x48/mimetypes/only-in-child.png", Relative(theme.Lookup("only-in-child", 48)));
    }

    [Fact]
    public void SettingTheSameThemeAgainKeepsTheCache()
    {
        // A SIGHUP for an unrelated key must not throw away every resolution in flight.
        var theme = Theme();
        Assert.NotNull(theme.Lookup("text-plain", 48));
        theme.Theme = "Child";
        Assert.NotNull(theme.Lookup("text-plain", 48));
    }

    [Fact]
    public void AnEmptyThemeNameMeansHicolorAndPixmapsOnly()
    {
        // icon_theme = "" has to mean the pre-setting behavior exactly, or turning the
        // setting off would be a third thing rather than the way back.
        var theme = new XdgIconTheme([Root], [Path.Combine(Root, "pixmaps")]) { Theme = string.Empty };
        Assert.Null(theme.Lookup("only-in-child", 48));
        Assert.Equal("hicolor/48x48/apps/only-in-hicolor.png", Relative(theme.Lookup("only-in-hicolor", 48)));
    }

    [Fact]
    public void LookupAnyTakesTheFirstNameThatResolves()
    {
        // How the MIME database's most-specific-first candidate list is consumed.
        var theme = Theme();
        Assert.Equal("Child/48x48/mimetypes/text-plain.png",
            Relative(theme.LookupAny(["text-markdown", "text-plain", "text-x-generic"], 48)));
        Assert.Null(theme.LookupAny(["nope-one", "nope-two"], 48));
    }

    [Fact]
    public void ACycleInInheritsTerminates()
    {
        // Themes in the wild do occasionally inherit each other. Without the visited set
        // this recurses until the stack runs out.
        using var temp = new TemporaryDirectory();
        foreach (var (name, parent) in new[] { ("A", "B"), ("B", "A") })
        {
            var dir = Path.Combine(temp.Path, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "index.theme"),
                $"[Icon Theme]\nName={name}\nDirectories=16\nInherits={parent}\n\n[16]\nSize=16\nType=Fixed\n");
        }

        var theme = new XdgIconTheme([temp.Path], []) { Theme = "A" };
        Assert.Null(theme.Lookup("anything", 16));
    }

    [Fact]
    public void TheIndexParserAppliesTheSpecsDefaults()
    {
        // Type defaults to Threshold with a threshold of 2, Scale to 1, and both size bounds
        // to Size -- so a Scalable directory that omits them serves one size, not everything.
        var index = IconThemeIndex.Parse("T", ["[Icon Theme]", "Directories=d", "", "[d]", "Size=24"]);
        var directory = Assert.Single(index.Directories);
        Assert.Equal(IconSizeType.Threshold, directory.Type);
        Assert.Equal(2, directory.Threshold);
        Assert.Equal(1, directory.Scale);
        Assert.Equal(24, directory.MinSize);
        Assert.Equal(24, directory.MaxSize);
        Assert.True(directory.Matches(26, 1));
        Assert.False(directory.Matches(27, 1));
    }

    [Fact]
    public void ADirectoryNamedWithNoGroupOfItsOwnIsSkipped()
    {
        // Without a Size there is nothing sensible to assume, and inventing one would put
        // wrongly-sized artwork in front of the user.
        var index = IconThemeIndex.Parse("T", ["[Icon Theme]", "Directories=present,absent", "", "[present]", "Size=16"]);
        Assert.Single(index.Directories);
        Assert.Equal("present", index.Directories[0].Path);
    }

    [Fact]
    public void AdwaitaOnThisMachineResolvesAFolderIcon()
    {
        // A smoke test against the installed theme: the fixture cannot notice Adwaita
        // changing its layout, and this will.
        var theme = new XdgIconTheme();
        Assert.NotNull(theme.Lookup("folder", 48));
    }
}
