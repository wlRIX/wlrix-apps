using System.Text;
using Wlrix.Files.Core.Operations;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// The trash, which has to agree byte for byte with <c>wlrix-desktop/src/trash.rs</c>: the
/// desktop's Remove and the file manager's Move to Trash put things in the same place, and
/// either must read back what the other wrote.
/// </summary>
public class TrashTests
{
    [Fact]
    public void TrashingMovesTheFileAndWritesItsRecord()
    {
        using var temp = new TemporaryDirectory();
        var file = temp.File("notes.txt", "content");
        var trash = Path.Combine(temp.Path, "Trash");

        Trash.Send(Location.FromLocalPath(file), trash);

        Assert.False(File.Exists(file));
        Assert.Equal("content", File.ReadAllText(Path.Combine(trash, "files", "notes.txt")));

        var record = File.ReadAllText(Path.Combine(trash, "info", "notes.txt.trashinfo"));
        Assert.StartsWith("[Trash Info]\n", record, StringComparison.Ordinal);
        Assert.Contains($"Path={file}", record, StringComparison.Ordinal);
        Assert.Contains("DeletionDate=", record, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecondFileOfTheSameNameIsNumberedKeepingItsExtension()
    {
        // The spec's own suggestion for collisions. Keeping the extension where it belongs
        // is what lets the file still be opened from the trash.
        using var temp = new TemporaryDirectory();
        var trash = Path.Combine(temp.Path, "Trash");

        Trash.Send(Location.FromLocalPath(temp.File("a/notes.txt", "first")), trash);
        Trash.Send(Location.FromLocalPath(temp.File("b/notes.txt", "second")), trash);

        Assert.Equal("first", File.ReadAllText(Path.Combine(trash, "files", "notes.txt")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(trash, "files", "notes.2.txt")));
        Assert.True(File.Exists(Path.Combine(trash, "info", "notes.2.txt.trashinfo")));
    }

    [Fact]
    public void TheNameAndTheRecordAreClaimedTogether()
    {
        // Claiming one and finding the other taken would strand a file in the trash with no
        // record of where it came from, which is the outcome the spec's naming rule exists
        // to prevent.
        using var temp = new TemporaryDirectory();
        var trash = Path.Combine(temp.Path, "Trash");
        Directory.CreateDirectory(Path.Combine(trash, "files"));
        Directory.CreateDirectory(Path.Combine(trash, "info"));
        // A stale record with no file beside it still makes the name unusable.
        File.WriteAllText(Path.Combine(trash, "info", "notes.txt.trashinfo"), "[Trash Info]\n");

        Trash.Send(Location.FromLocalPath(temp.File("notes.txt", "content")), trash);

        Assert.False(File.Exists(Path.Combine(trash, "files", "notes.txt")));
        Assert.Equal("content", File.ReadAllText(Path.Combine(trash, "files", "notes.2.txt")));
    }

    [Fact]
    public void ADirectoryIsTrashedWhole()
    {
        using var temp = new TemporaryDirectory();
        temp.File("doomed/inner.txt", "x");
        var trash = Path.Combine(temp.Path, "Trash");

        Trash.Send(Location.FromLocalPath(Path.Combine(temp.Path, "doomed")), trash);

        Assert.False(Directory.Exists(Path.Combine(temp.Path, "doomed")));
        Assert.Equal("x", File.ReadAllText(Path.Combine(trash, "files", "doomed", "inner.txt")));
    }

    [Fact]
    public void ARemoteLocationCannotBeTrashed()
    {
        var ex = Assert.Throws<FileOperationException>(
            () => Trash.Send(Location.Parse("smb://host/share/a.txt"), "/tmp/whatever"));
        Assert.Equal(FileErrorKind.Unsupported, ex.Kind);
    }

    [Theory]
    // The unreserved set plus '/', matching the Rust side exactly. Encoding the separators
    // would be valid per the URI grammar but would disagree with what the desktop writes.
    [InlineData("/home/vic/notes.txt", "/home/vic/notes.txt")]
    [InlineData("/home/vic/my file.txt", "/home/vic/my%20file.txt")]
    [InlineData("/home/vic/a+b&c.txt", "/home/vic/a%2Bb%26c.txt")]
    [InlineData("/home/vic/~keep-me_1.0.txt", "/home/vic/~keep-me_1.0.txt")]
    public void ThePathFieldIsPercentEncodedTheSameWayTheDesktopEncodesIt(string path, string expected) =>
        Assert.Equal(expected, Trash.PercentEncode(path));

    [Fact]
    public void NonAsciiPathsAreEncodedAsUtf8Bytes()
    {
        // Byte-wise, not character-wise: a reader decodes the bytes back to UTF-8, and
        // encoding the code point would produce something it cannot.
        var encoded = Trash.PercentEncode("/home/vic/日本語.txt");
        Assert.Equal("/home/vic/%E6%97%A5%E6%9C%AC%E8%AA%9E.txt", encoded);
        Assert.Equal("/home/vic/日本語.txt", Uri.UnescapeDataString(encoded));
    }

    [Fact]
    public void TheRecordUsesTheSpecsDateFormat()
    {
        var record = Trash.TrashInfo("/a/b.txt", new DateTimeOffset(2026, 9, 12, 14, 5, 3, TimeSpan.Zero));
        Assert.Equal("[Trash Info]\nPath=/a/b.txt\nDeletionDate=2026-09-12T14:05:03\n", record);
    }

    [Fact]
    public void TheRecordIsWrittenWithNoByteOrderMark()
    {
        // A BOM would make the file unreadable to every other trash implementation, none of
        // which expect one in front of "[Trash Info]".
        using var temp = new TemporaryDirectory();
        var trash = Path.Combine(temp.Path, "Trash");
        Trash.Send(Location.FromLocalPath(temp.File("a.txt", "x")), trash);

        var bytes = File.ReadAllBytes(Path.Combine(trash, "info", "a.txt.trashinfo"));
        Assert.Equal((byte)'[', bytes[0]);
    }
}
