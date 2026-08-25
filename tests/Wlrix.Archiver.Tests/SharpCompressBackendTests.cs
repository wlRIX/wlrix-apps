using Wlrix.Archiver.Models;
using Wlrix.Archiver.Services.Archives;
using Wlrix.Archiver.Services.Encodings;
using Xunit;

namespace Wlrix.Archiver.Tests;

public class SharpCompressBackendTests
{
    private static SharpCompressBackend Backend() =>
        new(new FilenameDecoder());

    [Fact]
    public async Task AZipWithTheUtf8FlagClearReadsAsJapaneseRatherThanMojibake()
    {
        // The regression this application exists to avoid. The fixture is a real CP932 zip with
        // general-purpose bit 11 clear; read as UTF-8 or Latin-1 the names come out as garbage,
        // and the garbage is what the extracted files would be named.
        var archive = await Backend().OpenAsync(Fixture.Path("sjis-names.zip"), ArchiveFormat.Zip);

        Assert.Contains(archive.Entries, entry => entry.Path == "文書/読みこみ.txt");
        Assert.Contains(archive.Entries, entry => entry.Path == "画像/写真.jpg");
    }

    [Fact]
    public async Task AZipThatDeclaresUtf8IsTakenAtItsWordAndNotSecondGuessed()
    {
        var archive = await Backend().OpenAsync(Fixture.Path("utf8-names.zip"), ArchiveFormat.Zip);

        Assert.Contains(archive.Entries, entry => entry.Path == "文書/読みこみ.txt");
        Assert.Contains(archive.Entries, entry => entry.Path == "naïve/café.txt");
    }

    [Fact]
    public async Task TarEntriesCarryTheirModeAndOwnershipIntoTheColumns()
    {
        var archive = await Backend().OpenAsync(Fixture.Path("modes.tar"), ArchiveFormat.Tar);

        var binary = archive.Entries.Single(entry => entry.Path == "bin/blender");
        // Grouped binary rather than octal, which C# has no literal for: rwx | r-x | r-x.
        Assert.Equal(0b111_101_101, binary.Mode);
        Assert.Equal("1000", binary.Owner);
        Assert.Equal("100", binary.Group);
        Assert.Equal("-rwxr-xr-x", binary.ModeText);
    }

    [Fact]
    public async Task ACompressedTarListsTheFilesInsideItRatherThanTheTarItself()
    {
        // `ArchiveFactory.Open` on a .tar.gz sees the gzip wrapper and stops there: it answers a
        // GZip archive holding one entry named after the tar inside. Nothing throws — the window
        // just shows a single row called `modes.tar` where the files should be. The wrapper has
        // to be peeled off before the tar can be read.
        var archive = await Backend().OpenAsync(Fixture.Path("modes.tar.gz"), ArchiveFormat.TarGz);

        Assert.Contains(archive.Entries, entry => entry.Path == "bin/blender");
        Assert.Contains(archive.Entries, entry => entry.Path == "copyright.txt");
        Assert.DoesNotContain(archive.Entries, entry => entry.Path == "modes.tar");
    }

    [Fact]
    public async Task ACompressedTarKeepsTheModesTheUncompressedOneHas()
    {
        // The same tar through the two code paths has to produce the same rows, or the scratch
        // file route has quietly lost the header fields on the way through.
        var plain = await Backend().OpenAsync(Fixture.Path("modes.tar"), ArchiveFormat.Tar);
        var compressed = await Backend()
            .OpenAsync(Fixture.Path("modes.tar.gz"), ArchiveFormat.TarGz);

        Assert.Equal(
            plain.Entries.Select(entry => (entry.Path, entry.ModeText, entry.Owner)),
            compressed.Entries.Select(entry => (entry.Path, entry.ModeText, entry.Owner)));
    }

    [Fact]
    public async Task PaxHeaderEntriesAreBookkeepingAndNeverAppearAsRows()
    {
        // PAX and GNU tar store what will not fit in the 512-byte header — long names, high
        // uids, sub-second times — as extra entries in the stream, and SharpCompress hands them
        // over alongside the real ones. Unfiltered, every PAX tarball shows a `@PaxHeader` row
        // between each pair of files. The fixtures are PAX, which is Python tarfile's default.
        var archive = await Backend().OpenAsync(Fixture.Path("modes.tar"), ArchiveFormat.Tar);

        Assert.DoesNotContain(archive.Entries,
            entry => entry.Name.StartsWith('@') || entry.Path.Contains("PaxHeader"));
        Assert.Equal(4, archive.Entries.Count);
    }

    [Theory]
    [InlineData("././@PaxHeader")]
    [InlineData("PaxHeaders.0/thing.txt")]
    [InlineData("dir/@LongLink")]
    public void TarBookkeepingEntriesAreRecognizedWhereverTheySit(string key)
    {
        Assert.True(SharpCompressBackend.IsTarMetadata(key));
    }

    [Theory]
    [InlineData("bin/blender")]
    [InlineData("notes@home.txt")]
    public void OrdinaryEntriesAreNotMistakenForBookkeeping(string key)
    {
        Assert.False(SharpCompressBackend.IsTarMetadata(key));
    }

    [Fact]
    public async Task AnOrdinaryDirectoryIsNotReportedAsStickyByTheWholeArchive()
    {
        // SharpCompress ORs 0o1000 into every directory's mode, so before this was masked off
        // every directory of every tarball rendered as `drwxr-xr-t`. `tar tvf` on the same file
        // says `drwxr-xr-x`, and it is right.
        var archive = await Backend()
            .OpenAsync(Fixture.Path("special-bits.tar"), ArchiveFormat.Tar);

        Assert.Equal("drwxr-xr-x",
            archive.Entries.Single(entry => entry.Path == "plain").ModeText);
    }

    [Fact]
    public async Task SetgidOnADirectorySurvivesTheStickyBitBeingMaskedOff()
    {
        var archive = await Backend()
            .OpenAsync(Fixture.Path("special-bits.tar"), ArchiveFormat.Tar);

        // 0o3755 stays distinguishable from 0o1755, so this one is real information and is kept.
        Assert.Equal("drwxr-sr-x",
            archive.Entries.Single(entry => entry.Path == "setgid").ModeText);
    }

    [Fact]
    public async Task TheSpecialBitsOfAFileAreUntouchedByTheDirectoryWorkaround()
    {
        var archive = await Backend()
            .OpenAsync(Fixture.Path("special-bits.tar"), ArchiveFormat.Tar);

        // Files do not get the spurious bit, so setuid on one is reported as it stands.
        Assert.Equal("-rwsr-xr-x",
            archive.Entries.Single(entry => entry.Path == "setuid-file").ModeText);
    }

    [Fact]
    public async Task ASymlinkEntryKeepsItsTargetAndRendersAsALinkInTheModeColumn()
    {
        var archive = await Backend().OpenAsync(Fixture.Path("modes.tar"), ArchiveFormat.Tar);

        var link = archive.Entries.Single(entry => entry.Path == "bin/blender-link");
        Assert.Equal("blender", link.LinkTarget);
        Assert.StartsWith("l", link.ModeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AZipWrittenOnUnixCarriesItsModeInTheTopHalfOfItsExternalAttributes()
    {
        var archive = await Backend()
            .OpenAsync(Fixture.Path("no-dir-entries.zip"), ArchiveFormat.Zip);

        // Zip has no mode field. A Unix host puts one in the high 16 bits of the external
        // attributes, which is where this comes from.
        Assert.All(archive.Entries, entry => Assert.Equal("-rw-------", entry.ModeText));
    }

    [Fact]
    public async Task AZipWrittenOnDosHasNoModeSoTheColumnStaysBlankRatherThanShowingNonsense()
    {
        // The same field holds FAT attributes on a DOS-made zip, and reading 0x20 as a mode
        // would print `------w-` for an ordinary file. The high half being zero is the
        // available tell, and it is the one Info-ZIP itself uses.
        var archive = await Backend().OpenAsync(Fixture.Path("dos-attrs.zip"), ArchiveFormat.Zip);

        Assert.NotEmpty(archive.Entries);
        Assert.All(archive.Entries, entry => Assert.Equal(string.Empty, entry.ModeText));
    }

    [Fact]
    public async Task ExtractingWithNoSelectionWritesTheWholeArchive()
    {
        using var destination = new TemporaryDirectory();

        await Backend().ExtractAsync(Fixture.Path("no-dir-entries.zip"), ArchiveFormat.Zip,
            [], destination.Path);

        Assert.True(File.Exists(Path.Combine(destination.Path, "a", "b", "c.txt")));
        Assert.True(File.Exists(Path.Combine(destination.Path, "a", "e.txt")));
    }

    [Fact]
    public async Task ExtractingADirectoryBringsEverythingUnderItAndNothingElse()
    {
        using var destination = new TemporaryDirectory();

        await Backend().ExtractAsync(Fixture.Path("no-dir-entries.zip"), ArchiveFormat.Zip,
            ["a/b"], destination.Path);

        Assert.True(File.Exists(Path.Combine(destination.Path, "a", "b", "c.txt")));
        Assert.True(File.Exists(Path.Combine(destination.Path, "a", "b", "d.txt")));
        Assert.False(File.Exists(Path.Combine(destination.Path, "a", "e.txt")));
    }

    [Fact]
    public async Task FlatteningWritesEveryFileStraightIntoTheDestination()
    {
        using var destination = new TemporaryDirectory();

        await Backend().ExtractAsync(Fixture.Path("no-dir-entries.zip"), ArchiveFormat.Zip,
            [], destination.Path, flatten: true);

        Assert.True(File.Exists(Path.Combine(destination.Path, "c.txt")));
        Assert.False(Directory.Exists(Path.Combine(destination.Path, "a")));
    }

    [Fact]
    public async Task ShiftJisNamesSurviveBeingWrittenOutToDisk()
    {
        // Reading the name correctly is only half of it; the point is the file on disk.
        using var destination = new TemporaryDirectory();

        await Backend().ExtractAsync(Fixture.Path("sjis-names.zip"), ArchiveFormat.Zip,
            [], destination.Path);

        Assert.True(File.Exists(Path.Combine(destination.Path, "文書", "読みこみ.txt")));
    }

    [Fact]
    public async Task RemovingAnEntryLeavesTheRestOfTheArchiveIntact()
    {
        using var work = new TemporaryDirectory();
        var archivePath = work.Copy(Fixture.Path("no-dir-entries.zip"));
        var backend = Backend();

        await backend.RemoveAsync(archivePath, ArchiveFormat.Zip, ["a/e.txt"]);

        var archive = await backend.OpenAsync(archivePath, ArchiveFormat.Zip);
        Assert.DoesNotContain(archive.Entries, entry => entry.Path == "a/e.txt");
        Assert.Contains(archive.Entries, entry => entry.Path == "a/b/c.txt");
    }

    [Fact]
    public async Task RemovingADirectoryTakesEverythingUnderItWithIt()
    {
        using var work = new TemporaryDirectory();
        var archivePath = work.Copy(Fixture.Path("no-dir-entries.zip"));
        var backend = Backend();

        await backend.RemoveAsync(archivePath, ArchiveFormat.Zip, ["a/b"]);

        var archive = await backend.OpenAsync(archivePath, ArchiveFormat.Zip);
        Assert.Equal(["a/e.txt"], archive.Entries.Select(entry => entry.Path));
    }

    [Fact]
    public async Task AddingAFilePutsItInTheArchiveAndKeepsWhatWasAlreadyThere()
    {
        using var work = new TemporaryDirectory();
        var archivePath = work.Copy(Fixture.Path("no-dir-entries.zip"));
        var added = Path.Combine(work.Path, "added.txt");
        await File.WriteAllTextAsync(added, "new\n");
        var backend = Backend();

        await backend.AddAsync(archivePath, ArchiveFormat.Zip, [added]);

        var archive = await backend.OpenAsync(archivePath, ArchiveFormat.Zip);
        Assert.Contains(archive.Entries, entry => entry.Path == "added.txt");
        Assert.Contains(archive.Entries, entry => entry.Path == "a/b/c.txt");
    }

    [Fact]
    public async Task ACompressedTarStaysCompressedAfterBeingRewritten()
    {
        // The silent failure to guard against: saving a .tar.gz with no compression leaves an
        // uncompressed tar under a name that says otherwise, and every other tool then fails to
        // open it.
        using var work = new TemporaryDirectory();
        var archivePath = work.Copy(Fixture.Path("modes.tar.gz"));
        var backend = Backend();

        await backend.RemoveAsync(archivePath, ArchiveFormat.TarGz, ["copyright.txt"]);

        var head = new byte[2];
        await using (var stream = File.OpenRead(archivePath))
        {
            _ = await stream.ReadAsync(head);
        }

        Assert.Equal([0x1f, 0x8b], head);
        var archive = await backend.OpenAsync(archivePath, ArchiveFormat.TarGz);
        Assert.DoesNotContain(archive.Entries, entry => entry.Path == "copyright.txt");
    }

    [Fact]
    public async Task AFailedRewriteLeavesNoTemporaryFileBehind()
    {
        using var work = new TemporaryDirectory();
        var archivePath = work.Copy(Fixture.Path("modes.tar.gz"));

        // Nothing matches, so nothing is removed — but the archive is still rewritten, which is
        // the path where the temporary file exists.
        await Backend().RemoveAsync(archivePath, ArchiveFormat.TarGz, ["nothing/here.txt"]);

        Assert.False(File.Exists(archivePath + ".wlrix-new"));
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip, ArchiveCapabilities.All)]
    [InlineData(ArchiveFormat.Tar, ArchiveCapabilities.All)]
    // No encoder for either, so the menu items that would change them stay disabled.
    [InlineData(ArchiveFormat.Rar, ArchiveCapabilities.ReadOnly)]
    [InlineData(ArchiveFormat.SevenZip, ArchiveCapabilities.ReadOnly)]
    // One member and no directory: there is nothing to add to or remove from.
    [InlineData(ArchiveFormat.GZip, ArchiveCapabilities.ReadOnly)]
    [InlineData(ArchiveFormat.Unknown, ArchiveCapabilities.None)]
    public void CapabilitiesAreReportedPerFormatSoTheMenuCanEnableItselfFromThem(
        ArchiveFormat format, ArchiveCapabilities expected)
    {
        Assert.Equal(expected, Backend().Supports(format));
    }
}

/// <summary>A scratch directory that deletes itself.</summary>
internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "wlrix-archiver-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Copies a fixture in, so a test that modifies an archive does not modify the fixture.</summary>
    public string Copy(string source)
    {
        var destination = System.IO.Path.Combine(Path, System.IO.Path.GetFileName(source));
        File.Copy(source, destination);
        return destination;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover scratch directory is untidy, not a test failure.
        }
    }
}
