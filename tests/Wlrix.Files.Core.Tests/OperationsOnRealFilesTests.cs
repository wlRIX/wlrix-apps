using Wlrix.Files.Core.Operations;
using Wlrix.Files.Core.Platform;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// The engine against the real <see cref="Filesystems.LocalFileSystem"/> and a real disk.
/// </summary>
/// <remarks>
/// Everything else exercises it through FakeFileSystem, which is right for the awkward paths
/// but proves nothing about the local backend underneath. These are the ones that would catch
/// a disagreement between the two — a rename that refuses an existing target where the fake
/// allows it, say, or a part file that the real filesystem will not move into place.
/// </remarks>
public class OperationsOnRealFilesTests
{
    private static OperationJob Job(IConflictResolver? conflicts = null) =>
        new(new FileSystemProvider(), conflicts, FixedRetryPolicy.SkipAll, MountTable.Read());

    [Fact]
    public async Task CopyingATreeReproducesItOnDisk()
    {
        using var temp = new TemporaryDirectory();
        temp.File("src/one.txt", "first");
        temp.File("src/deep/two.txt", "second");
        temp.Directory_("src/deep/empty");
        temp.Directory_("dst");

        var result = await Job().RunAsync(FileOperation.Copy(
            [Location.FromLocalPath(Path.Combine(temp.Path, "src"))],
            Location.FromLocalPath(Path.Combine(temp.Path, "dst"))));

        Assert.True(result.Succeeded);
        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(temp.Path, "dst/src/one.txt")));
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(temp.Path, "dst/src/deep/two.txt")));
        Assert.True(Directory.Exists(Path.Combine(temp.Path, "dst/src/deep/empty")));
        // The originals are untouched by a copy.
        Assert.True(File.Exists(Path.Combine(temp.Path, "src/one.txt")));
    }

    [Fact]
    public async Task NoPartFileIsLeftOnDisk()
    {
        using var temp = new TemporaryDirectory();
        temp.File("src/a.txt", "x");
        temp.Directory_("dst");

        await Job().RunAsync(FileOperation.Copy(
            [Location.FromLocalPath(Path.Combine(temp.Path, "src/a.txt"))],
            Location.FromLocalPath(Path.Combine(temp.Path, "dst"))));

        Assert.Empty(Directory.GetFiles(temp.Path, "*" + OperationJob.PartSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AMoveWithinOneFilesystemIsARenameAndKeepsTheContent()
    {
        using var temp = new TemporaryDirectory();
        temp.File("src/a.txt", "payload");
        temp.Directory_("dst");

        var result = await Job().RunAsync(FileOperation.Move(
            [Location.FromLocalPath(Path.Combine(temp.Path, "src/a.txt"))],
            Location.FromLocalPath(Path.Combine(temp.Path, "dst"))));

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(Path.Combine(temp.Path, "src/a.txt")));
        Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(temp.Path, "dst/a.txt")));
    }

    [Fact]
    public async Task CopyingPreservesTheModificationTime()
    {
        // The local filesystem advertises PreservesMTime, so a copied file must not come out
        // dated now -- which would reorder a listing sorted by date.
        using var temp = new TemporaryDirectory();
        var source = temp.File("src/a.txt", "x");
        temp.Directory_("dst");
        var when = new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, when);

        await Job().RunAsync(FileOperation.Copy(
            [Location.FromLocalPath(source)],
            Location.FromLocalPath(Path.Combine(temp.Path, "dst"))));

        var copied = File.GetLastWriteTimeUtc(Path.Combine(temp.Path, "dst/a.txt"));
        Assert.True(Math.Abs((copied - when).TotalSeconds) < 2, $"expected {when}, got {copied}");
    }

    [Fact]
    public async Task AConflictIsRenamedAroundOnDisk()
    {
        using var temp = new TemporaryDirectory();
        temp.File("src/a.txt", "new");
        temp.File("dst/a.txt", "old");

        await Job(new AutoRenameResolver()).RunAsync(FileOperation.Copy(
            [Location.FromLocalPath(Path.Combine(temp.Path, "src/a.txt"))],
            Location.FromLocalPath(Path.Combine(temp.Path, "dst"))));

        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(temp.Path, "dst/a.txt")));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(temp.Path, "dst/a (copy).txt")));
    }

    [Fact]
    public async Task TrashingThroughTheEngineMovesTheFileToTheTrash()
    {
        using var temp = new TemporaryDirectory();
        var file = temp.File("doomed.txt", "x");
        var trashHome = temp.Directory_("xdg");

        // A scratch XDG_DATA_HOME so the real trash is never touched.
        var previous = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", trashHome);
        try
        {
            var result = await Job().RunAsync(FileOperation.SendToTrash([Location.FromLocalPath(file)]));
            Assert.True(result.Succeeded);
            Assert.False(File.Exists(file));
            Assert.Equal("x", await File.ReadAllTextAsync(Path.Combine(trashHome, "Trash/files/doomed.txt")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", previous);
        }
    }

    [Fact]
    public async Task DeletingATreeRemovesItEntirely()
    {
        using var temp = new TemporaryDirectory();
        temp.File("doomed/deep/inner.txt", "x");

        var result = await Job().RunAsync(FileOperation.Delete(
            [Location.FromLocalPath(Path.Combine(temp.Path, "doomed"))]));

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "doomed")));
    }

    [Fact]
    public async Task NewFolderAndRenameWorkOnDisk()
    {
        using var temp = new TemporaryDirectory();
        var made = Path.Combine(temp.Path, "fresh");

        Assert.True((await Job().RunAsync(FileOperation.NewDirectory(Location.FromLocalPath(made)))).Succeeded);
        Assert.True(Directory.Exists(made));

        var renamed = Path.Combine(temp.Path, "renamed");
        Assert.True((await Job().RunAsync(FileOperation.Rename(
            Location.FromLocalPath(made), Location.FromLocalPath(renamed)))).Succeeded);
        Assert.True(Directory.Exists(renamed));
        Assert.False(Directory.Exists(made));
    }

    [Fact]
    public async Task AFailedMoveLeavesTheOriginalsInPlace()
    {
        // The data-loss case, against a real disk this time. A directory the copy cannot
        // write into makes one file fail; nothing may be deleted from the source.
        using var temp = new TemporaryDirectory();
        temp.File("src/one.txt", "1");
        temp.File("src/two.txt", "2");
        var dst = temp.Directory_("dst");

        // Copying a directory onto a *file* of the same name fails when the target
        // subdirectory cannot be created.
        await File.WriteAllTextAsync(Path.Combine(dst, "src"), "in the way");

        var result = await Job().RunAsync(FileOperation.Move(
            [Location.FromLocalPath(Path.Combine(temp.Path, "src"))],
            Location.FromLocalPath(dst)));

        Assert.False(result.Succeeded);
        Assert.True(File.Exists(Path.Combine(temp.Path, "src/one.txt")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "src/two.txt")));
    }
}
