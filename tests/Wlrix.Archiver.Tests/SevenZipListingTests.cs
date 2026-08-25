using Wlrix.Archiver.Services.Archives;
using Xunit;

namespace Wlrix.Archiver.Tests;

/// <summary>
/// The <c>7z l -slt</c> parser, against captured output.
/// </summary>
/// <remarks>
/// Captured rather than produced by running the tool: these tests have to pass on a machine
/// with no 7z installed, which is exactly the machine the fallback path exists for.
/// </remarks>
public class SevenZipListingTests
{
    /// <summary>Real output from <c>7z 26.02</c>, trimmed to the shape that matters.</summary>
    private const string Listing = """
        7-Zip 26.02 (x64) : Copyright (c) 1999-2026 Igor Pavlov : 2026-06-25

        Scanning the drive for archives:
        1 file, 190 bytes (1 KiB)

        Listing archive: t.7z

        --
        Path = t.7z
        Type = 7z
        Physical Size = 190

        ----------
        Path = src
        Size = 0
        Packed Size = 0
        Modified = 2026-08-24 20:01:31.5519502
        Attributes = D drwxr-xr-x
        CRC =
        Encrypted = -

        Path = src/a.txt
        Size = 6
        Packed Size = 10
        Modified = 2026-08-24 20:01:31.5519502
        Attributes = A -rw-r--r--
        CRC = 363A3020
        Encrypted = -

        Path = src/secret.txt
        Size = 12
        Packed Size = 32
        Modified = 2026-08-24 20:02:00.0000000
        Attributes = A -rw-------
        CRC = 00000000
        Encrypted = +

        """;

    [Fact]
    public void TheArchivesOwnHeaderIsNotMistakenForAnEntry()
    {
        // The trap: the block before the rule of dashes is a `Key = Value` block too, and it
        // carries a `Path =` naming the archive. Parse from the top and t.7z appears inside
        // itself.
        var entries = SevenZipCliBackend.ParseListing(Listing);

        Assert.DoesNotContain(entries, entry => entry.Path == "t.7z");
        Assert.Equal(3, entries.Count);
    }

    [Fact]
    public void ADirectoryIsRecognizedFromTheLeadingDOfItsAttributes()
    {
        var entries = SevenZipCliBackend.ParseListing(Listing);

        var directory = entries.Single(entry => entry.Path == "src");
        Assert.True(directory.IsDirectory);
        Assert.False(entries.Single(entry => entry.Path == "src/a.txt").IsDirectory);
    }

    [Fact]
    public void SizesAndModificationTimesAreReadIntoTheirColumns()
    {
        var entry = SevenZipCliBackend.ParseListing(Listing)
            .Single(candidate => candidate.Path == "src/a.txt");

        Assert.Equal(6, entry.Size);
        Assert.Equal(10, entry.CompressedSize);
        Assert.Equal(new DateTime(2026, 8, 24, 20, 1, 31), entry.Modified?.AddTicks(-5519502));
    }

    [Fact]
    public void TheUnixModeStringIsReadBackIntoModeBits()
    {
        var entries = SevenZipCliBackend.ParseListing(Listing);

        Assert.Equal(0b110_100_100, entries.Single(entry => entry.Path == "src/a.txt").Mode);
        Assert.Equal(0b110_000_000, entries.Single(entry => entry.Path == "src/secret.txt").Mode);
        Assert.Equal("-rw-r--r--",
            entries.Single(entry => entry.Path == "src/a.txt").ModeText);
    }

    [Fact]
    public void AnEncryptedEntryIsMarkedFromThePlusInItsEncryptedField()
    {
        var entries = SevenZipCliBackend.ParseListing(Listing);

        Assert.True(entries.Single(entry => entry.Path == "src/secret.txt").IsEncrypted);
        Assert.False(entries.Single(entry => entry.Path == "src/a.txt").IsEncrypted);
    }

    [Fact]
    public void OutputWithNoEntriesAtAllParsesToAnEmptyListRatherThanThrowing()
    {
        Assert.Empty(SevenZipCliBackend.ParseListing(
            "7-Zip 26.02\n\nListing archive: empty.7z\n\n--\nPath = empty.7z\n\n----------\n"));
    }

    [Fact]
    public void AnArchiveWrittenOnWindowsHasNoModeStringAndTheColumnStaysBlank()
    {
        const string windows = """
            ----------
            Path = readme.txt
            Size = 5
            Attributes = A
            Encrypted = -

            """;

        var entry = Assert.Single(SevenZipCliBackend.ParseListing(windows));
        Assert.Null(entry.Mode);
        Assert.Equal(string.Empty, entry.ModeText);
    }
}
