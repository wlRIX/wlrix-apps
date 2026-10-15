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

    [Fact]
    public void EmptyingTakesTheFilesAndTheirRecords()
    {
        using var temp = new TemporaryDirectory();
        var trash = Path.Combine(temp.Path, "Trash");
        Trash.Send(Location.FromLocalPath(temp.File("a.txt", "a")), trash);
        Trash.Send(Location.FromLocalPath(temp.File("dir/inner.txt", "b")[..^"/inner.txt".Length]), trash);
        Assert.False(Trash.IsEmpty(trash));

        var problems = Trash.Empty(trash);

        Assert.Empty(problems);
        Assert.True(Trash.IsEmpty(trash));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(trash, "info")));
    }

    [Fact]
    public void EmptyingDeletesALinkNotWhatItPointsAt()
    {
        // The one mistake emptying must never make: a link to the user's own files, thrown
        // away, taking those files with it.
        using var temp = new TemporaryDirectory();
        var kept = temp.File("precious/keep.txt", "keep");
        var trash = Path.Combine(temp.Path, "Trash");
        var link = Path.Combine(temp.Path, "shortcut");
        Directory.CreateSymbolicLink(link, Path.GetDirectoryName(kept)!);
        Trash.Send(Location.FromLocalPath(link), trash);

        Trash.Empty(trash);

        Assert.True(File.Exists(kept));
        Assert.True(Trash.IsEmpty(trash));
    }

    [Fact]
    public void PuttingBackReturnsAnItemToWhereItCameFrom()
    {
        using var temp = new TemporaryDirectory();
        var file = temp.File("docs/plan.txt", "plan");
        var trash = Path.Combine(temp.Path, "Trash");
        Trash.Send(Location.FromLocalPath(file), trash);
        // Its folder went too, since; putting back makes it again.
        Directory.Delete(Path.GetDirectoryName(file)!);

        var back = Trash.Restore(Location.FromLocalPath(Path.Combine(trash, "files", "plan.txt")), trash);

        Assert.Equal(Location.FromLocalPath(file), back);
        Assert.Equal("plan", File.ReadAllText(file));
        Assert.False(File.Exists(Path.Combine(trash, "info", "plan.txt.trashinfo")));
    }

    [Fact]
    public void PuttingBackNeverReplacesWhatIsThereNow()
    {
        using var temp = new TemporaryDirectory();
        var file = temp.File("plan.txt", "old");
        var trash = Path.Combine(temp.Path, "Trash");
        Trash.Send(Location.FromLocalPath(file), trash);
        File.WriteAllText(file, "new");

        var error = Assert.Throws<FileOperationException>(() =>
            Trash.Restore(Location.FromLocalPath(Path.Combine(trash, "files", "plan.txt")), trash));

        Assert.Equal(FileErrorKind.AlreadyExists, error.Kind);
        Assert.Equal("new", File.ReadAllText(file));
        Assert.True(File.Exists(Path.Combine(trash, "files", "plan.txt")));
    }

    [Fact]
    public void ARecordedPathIsDecoded()
    {
        using var temp = new TemporaryDirectory();
        var record = temp.File("x.trashinfo", "[Trash Info]\nPath=/home/me/My%20Notes/%E3%83%A1%E3%83%A2.txt\nDeletionDate=2026-10-15T10:00:00\n");
        Assert.Equal("/home/me/My Notes/メモ.txt", Trash.ReadOriginalPath(record));
    }

    [Fact]
    public void TheDumpsterIsRecognizedByItsFilesDirectory()
    {
        using var temp = new TemporaryDirectory();
        var trash = Path.Combine(temp.Path, "Trash");
        Assert.True(Trash.IsTrash(Location.FromLocalPath(Path.Combine(trash, "files")), trash));
        Assert.True(Trash.IsTrash(Location.FromLocalPath(Path.Combine(trash, "files") + "/"), trash));
        Assert.False(Trash.IsTrash(Location.FromLocalPath(trash), trash));
        Assert.False(Trash.IsTrash(temp.Location, trash));
    }
}
