using System.Globalization;
using Wlrix.Files.Core.Mime;
using Xunit;

namespace Wlrix.Files.Core.Tests;

public class SharedMimeDatabaseTests
{
    private static SharedMimeDatabase Db() =>
        SharedMimeDatabase.Load([Path.Combine(AppContext.BaseDirectory, "Fixtures", "mime")]);

    [Theory]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("photo.png", "image/png")]
    [InlineData("photo.JPG", "image/jpeg")]
    [InlineData("page.html", "text/html")]
    public void APlainExtensionResolves(string name, string expected) =>
        Assert.Equal(expected, Db().ResolveByName(name));

    [Fact]
    public void TheLongestExtensionWinsSoATarballIsNotJustGzip()
    {
        // The case that decides whether a tarball opens in the archiver or in something
        // that only knows how to gunzip it.
        var db = Db();
        Assert.Equal("application/x-compressed-tar", db.ResolveByName("backup.tar.gz"));
        Assert.Equal("application/gzip", db.ResolveByName("notes.gz"));
        Assert.Equal("application/x-tar", db.ResolveByName("backup.tar"));
    }

    [Fact]
    public void ALiteralNameBeatsAWildcardThatAlsoMatches()
    {
        // "Makefile" is a literal; without literal-first precedence "README*" or another
        // pattern could claim it.
        Assert.Equal("text/x-makefile", Db().ResolveByName("Makefile"));
        Assert.Equal("text/x-makefile", Db().ResolveByName("makefile"));
    }

    [Fact]
    public void TheCaseSensitiveFlagIsHonored()
    {
        // "core" is a core dump; "CORE" is somebody's file. Matching the second as the
        // first would put a crash-dump icon on it and offer the wrong opener.
        var db = Db();
        Assert.Equal("application/x-core", db.ResolveByName("core"));
        Assert.Null(db.ResolveByName("CORE"));
        Assert.Equal("text/x-genie", db.ResolveByName("a.gs"));
        Assert.Null(db.ResolveByName("a.GS"));
    }

    [Fact]
    public void NonExtensionPatternsStillMatch()
    {
        var db = Db();
        Assert.Equal("text/x-scons", db.ResolveByName("sconscript.local"));
        Assert.Equal("application/x-trash", db.ResolveByName("draft.txt~"));
        Assert.Equal("text/x-readme", db.ResolveByName("README.rst"));
    }

    [Fact]
    public void AnUnknownNameResolvesToNothingRatherThanGuessing()
    {
        Assert.Null(Db().ResolveByName("mystery.qqq"));
        Assert.Null(Db().ResolveByName(""));
    }

    [Fact]
    public void TheStatKindDecidesBeforeTheNameDoes()
    {
        // A directory called notes.txt is a directory. Letting the glob win here would
        // give it a text icon and try to open it in an editor.
        var db = Db();
        var dir = new FileEntry
        {
            Location = Location.Parse("/a/notes.txt"),
            Name = "notes.txt",
            Kind = FileKind.Directory
        };
        Assert.Equal(SharedMimeDatabase.Directory, db.Resolve(dir));

        var file = new FileEntry
        {
            Location = Location.Parse("/a/notes.txt"),
            Name = "notes.txt",
            Kind = FileKind.File
        };
        Assert.Equal("text/plain", db.Resolve(file));
    }

    [Fact]
    public void AnUnrecognizedFileFallsBackToOctetStream()
    {
        var entry = new FileEntry
        {
            Location = Location.Parse("/a/mystery.qqq"),
            Name = "mystery.qqq",
            Kind = FileKind.File
        };
        Assert.Equal(SharedMimeDatabase.Default, Db().Resolve(entry));
    }

    [Fact]
    public void AliasesResolveToTheirCanonicalType()
    {
        var db = Db();
        Assert.Equal("application/gzip", db.Canonicalize("application/x-gzip"));
        Assert.Equal("image/jpeg", db.Canonicalize("image/pjpeg"));
        // An unknown type is returned unchanged rather than nulled.
        Assert.Equal("application/unknown", db.Canonicalize("application/unknown"));
    }

    [Fact]
    public void SubclassingIsTransitive()
    {
        // text/markdown -> text/plain directly; the transitive case is what an "is this
        // text?" check depends on.
        var db = Db();
        Assert.True(db.IsSubclassOf("text/markdown", "text/plain"));
        Assert.True(db.IsSubclassOf("application/x-compressed-tar", "application/gzip"));
        Assert.True(db.IsSubclassOf("text/plain", "text/plain"));
        Assert.False(db.IsSubclassOf("image/png", "text/plain"));
    }

    [Fact]
    public void SubclassingResolvesAliasesOnBothSides()
    {
        Assert.True(Db().IsSubclassOf("text/x-markdown", "text/plain"));
    }

    [Fact]
    public void IconCandidatesRunFromMostToLeastSpecific()
    {
        var db = Db();

        // An explicit icons= override comes first.
        Assert.Equal(
            ["application-x-core-custom", "application-x-core", "application-x-generic"],
            db.IconNamesFor("application/x-core"));

        // Otherwise: the type transformed, the generic-icons hint, then the media fallback.
        Assert.Equal(
            ["text-markdown", "text-x-generic", "text-x-generic"],
            db.IconNamesFor("text/markdown"));

        // With no hint at all, just the transform and the fallback.
        Assert.Equal(["image-png", "image-x-generic"], db.IconNamesFor("image/png"));
    }

    [Fact]
    public void IconCandidatesFollowAliasesToo()
    {
        Assert.Equal(["image-jpeg", "image-x-generic"], Db().IconNamesFor("image/pjpeg"));
    }

    [Fact]
    public void AMissingDatabaseDirectoryIsNotAnError()
    {
        // A data directory can exist with no compiled mime database under it, and "icons"
        // is frequently absent entirely.
        var db = SharedMimeDatabase.Load(["/nonexistent-" + Guid.NewGuid().ToString("N")]);
        Assert.Null(db.ResolveByName("a.txt"));
        Assert.Equal(SharedMimeDatabase.Default, db.Resolve(new FileEntry
        {
            Location = Location.Parse("/a.txt"),
            Name = "a.txt",
            Kind = FileKind.File
        }));
    }

    [Fact]
    public void ThisMachinesRealDatabaseLoadsAndResolvesTheObviousCases()
    {
        // A smoke test against the installed database: the fixture cannot catch a format
        // change in shared-mime-info, and this will. Deliberately only asserts types that
        // have been stable for decades.
        var db = SharedMimeDatabase.Load();
        Assert.Equal("text/plain", db.ResolveByName("notes.txt"));
        Assert.Equal("image/png", db.ResolveByName("photo.png"));
    }

    // --- sniffing, for files whose name says nothing -----------------------

    private static FileEntry Entry(string name, FileKind kind = FileKind.File) =>
        new()
        {
            Location = Location.Parse("/base").Child(name),
            Name = name,
            Kind = kind
        };

    [Fact]
    public void ContentsDecideWhenTheNameSaysNothing()
    {
        // The whole point of the tier: README, configure, run — the files with no extension —
        // otherwise come out as a generic blob that nothing offers to open.
        var db = Db();
        Assert.Equal("application/pdf", db.ResolveWithContent(Entry("paper"), "%PDF-1.7"u8));
        Assert.Equal("text/x-shellscript", db.ResolveWithContent(Entry("configure"), "#!/bin/sh\n"u8));
    }

    [Fact]
    public void TheNameStillDecidesFirst()
    {
        // A name that resolves is trusted over the bytes, which is the order gio uses and the
        // order the class documents. It is also the safer half of the trade: a wrong glob
        // names the wrong application, where second-guessing every name would do that to files
        // that were perfectly well named.
        var db = Db();
        Assert.Equal("text/plain", db.ResolveWithContent(Entry("notes.txt"), "%PDF-1.7"u8));
    }

    [Fact]
    public void AnEmptyFileIsSaidToBeEmptyRatherThanUnknown()
    {
        Assert.Equal("inode/x-empty", Db().Sniff([]));
        Assert.Equal("inode/x-empty", Db().ResolveWithContent(Entry("scratch"), []));
    }

    [Fact]
    public void TextWithNothingDistinctiveAboutItIsStillText()
    {
        // No magic rule can match plain text — there is nothing to match — so the fallback is
        // what stops an extensionless note being a blob.
        var db = Db();
        Assert.Equal("text/plain", db.Sniff("just some words\nand a second line\n"u8));
        Assert.Equal("text/plain", db.Sniff("日本語のテキスト"u8));
    }

    [Fact]
    public void BytesThatAreNotTextAndMatchNothingAreLeftUnnamed()
    {
        // Answering application/octet-stream here would be the caller's decision to make, and
        // null is the honest report that nothing was recognized.
        Assert.Null(Db().Sniff([0x00, 0x01, 0x02, 0xFF]));
        Assert.Equal(SharedMimeDatabase.Default, Db().ResolveWithContent(Entry("blob"), [0x00, 0x01]));
    }

    [Fact]
    public void ADirectoryIsNeverSniffed()
    {
        // The stat kind is not a guess and no amount of content changes it — and a directory
        // has no contents to read in this sense anyway.
        Assert.Equal(
            SharedMimeDatabase.Directory,
            Db().ResolveWithContent(Entry("stuff", FileKind.Directory), "%PDF-1.7"u8));
    }

    [Fact]
    public void TheNestedDocbookRuleSurvivesTheRealFixture()
    {
        // Pinned through the full database rather than only in the parser's own tests, because
        // this is the rule shape that a careless reader gets wrong, and getting it wrong makes
        // every XML file a DocBook one.
        var db = Db();
        Assert.Equal("application/xml", db.Sniff("<?xml version=\"1.0\"?><note/>"u8));
        Assert.Equal(
            "application/docbook+xml",
            db.Sniff("<?xml version=\"1.0\"?>\n<!DOCTYPE book PUBLIC \"-//OASIS//DTD DocBook XML V4.5//EN\">"u8));
    }

    [Fact]
    public void TheDatabaseSaysHowMuchOfAFileItWants()
    {
        var db = Db();
        Assert.True(db.CanSniff);
        Assert.True(db.SniffLength > 0);
    }

    // --- descriptions, for the properties dialog ---------------------------

    [Fact]
    public void ATypeIsDescribedInWordsRatherThanAsItsName()
    {
        // "image/png" is what the file manager matches on; "PNG image" is what a properties
        // dialog shows a person. Pinned to the invariant culture, because this machine's
        // session is Japanese and the fixture has a Japanese translation — which is the
        // next test's subject, not this one's.
        InCulture(CultureInfo.InvariantCulture, () =>
            Assert.Equal("PNG image", Db().DescriptionFor("image/png")));
    }

    [Fact]
    public void AnAliasIsDescribedAsWhateverItIsAnAliasFor()
    {
        // The description lives in the canonical type's file; asking about the alias must not
        // go looking for a file that was never written.
        var db = Db();
        Assert.Equal("image/jpeg", db.Canonicalize("image/pjpeg"));
        Assert.Equal("JPEG image", db.DescriptionFor("image/pjpeg"));
    }

    [Theory]
    [InlineData("ja", "PNG 画像")]
    [InlineData("ja-JP", "PNG 画像")]
    [InlineData("pt-BR", "imagem PNG")]
    [InlineData("pt", "PNG image")]
    [InlineData("de", "PNG image")]
    public void TheDescriptionIsTakenInTheSessionsLanguageWhenThereIsOne(string culture, string expected)
    {
        // Note pt: the fixture has pt-BR and no pt, and a regional translation must not be
        // shown to a speaker who did not ask for that region. Falling back to the untagged
        // English one is the spec's own arrangement.
        InCulture(CultureInfo.GetCultureInfo(culture), () =>
            Assert.Equal(expected, Db().DescriptionFor("image/png")));
    }

    /// <summary>Runs something as if the session were in another language.</summary>
    private static void InCulture(CultureInfo culture, Action body)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = culture;
            body();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void ATypeWithNoDescriptionAnswersNullRatherThanSomethingInvented()
    {
        // Both cases: a file that exists but carries no comment, and no file at all. The
        // caller shows the type itself, which is honest.
        var db = Db();
        Assert.Null(db.DescriptionFor("text/x-makefile"));
        Assert.Null(db.DescriptionFor("application/x-nothing-here"));
        Assert.Null(db.DescriptionFor("not-a-type"));
    }

    [Fact]
    public void AMimeTypeCannotNameAFileOutsideTheDatabase()
    {
        // The type reaching this can come from a glob table on disk, so it is not
        // necessarily well formed. It becomes a path, and a path made of untrusted text is
        // worth refusing rather than normalizing.
        var db = Db();
        Assert.Null(db.DescriptionFor("../../etc/passwd"));
        Assert.Null(db.DescriptionFor("image/../../../etc/passwd"));
    }
}
