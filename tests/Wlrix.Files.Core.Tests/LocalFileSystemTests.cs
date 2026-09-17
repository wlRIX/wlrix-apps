using Wlrix.Files.Core.Filesystems;
using Xunit;

namespace Wlrix.Files.Core.Tests;

public class LocalFileSystemTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task EnumerationReportsNameKindSizeAndMTimeWithoutAFurtherStat()
    {
        using var dir = new TemporaryDirectory();
        dir.File("alpha.txt", "12345");
        dir.Directory_("beta");

        var fs = new LocalFileSystem();
        var entries = await Collect(fs.EnumerateAsync(dir.Location, None));

        var file = entries.Single(e => e.Name == "alpha.txt");
        Assert.Equal(FileKind.File, file.Kind);
        Assert.Equal(5, file.Size);
        Assert.NotNull(file.Modified);

        var sub = entries.Single(e => e.Name == "beta");
        Assert.True(sub.IsDirectory);
        // A directory's size is meaningless and is reported as zero rather than
        // whatever the filesystem happens to use for the directory inode.
        Assert.Equal(0, sub.Size);
    }

    [Fact]
    public async Task HiddenFilesAreReturnedAndFlaggedRatherThanFilteredOut()
    {
        // "Show hidden" is a view setting. Filtering here would mean re-reading the
        // directory every time it is toggled.
        using var dir = new TemporaryDirectory();
        dir.File(".config", "x");
        dir.File("visible", "x");

        var entries = await Collect(new LocalFileSystem().EnumerateAsync(dir.Location, None));
        Assert.Equal(2, entries.Count);
        Assert.True(entries.Single(e => e.Name == ".config").IsHidden);
        Assert.False(entries.Single(e => e.Name == "visible").IsHidden);
    }

    [Fact]
    public async Task ASymlinkToADirectoryEnumeratesAsADirectory()
    {
        // Which is what a user means by it: it opens, and it sorts with the folders.
        using var dir = new TemporaryDirectory();
        dir.Directory_("real");
        File.CreateSymbolicLink(Path.Combine(dir.Path, "link"), Path.Combine(dir.Path, "real"));

        var entries = await Collect(new LocalFileSystem().EnumerateAsync(dir.Location, None));
        Assert.True(entries.Single(e => e.Name == "link").IsDirectory);
    }

    [Fact]
    public async Task EnumeratingSomethingThatIsNotThereSaysNotFoundBeforeYieldingAnything()
    {
        // The enumerable is lazy, so without an explicit check this would surface on
        // the first MoveNext -- after a caller had already begun rendering a listing.
        var missing = Location.FromLocalPath("/nonexistent-" + Guid.NewGuid().ToString("N"));
        var ex = await Assert.ThrowsAsync<FileOperationException>(
            async () => await Collect(new LocalFileSystem().EnumerateAsync(missing, None)));
        Assert.Equal(FileErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task EnumeratingAFileSaysNotADirectory()
    {
        using var dir = new TemporaryDirectory();
        var file = Location.FromLocalPath(dir.File("a.txt", "x"));
        var ex = await Assert.ThrowsAsync<FileOperationException>(
            async () => await Collect(new LocalFileSystem().EnumerateAsync(file, None)));
        Assert.Equal(FileErrorKind.NotADirectory, ex.Kind);
    }

    [Fact]
    public async Task EnumerationIsCancellable()
    {
        using var dir = new TemporaryDirectory();
        for (var i = 0; i < 2000; i++)
            dir.File($"f{i:D5}", "x");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await Collect(new LocalFileSystem().EnumerateAsync(dir.Location, cts.Token)));
    }

    [Fact]
    public async Task ADirectoryWithManyEntriesComesBackWhole()
    {
        // The enumerator reads in chunks of 512; this crosses that boundary twice and
        // would catch an off-by-one that drops or repeats a chunk.
        using var dir = new TemporaryDirectory();
        for (var i = 0; i < 1300; i++)
            dir.File($"f{i:D5}", "");

        var entries = await Collect(new LocalFileSystem().EnumerateAsync(dir.Location, None));
        Assert.Equal(1300, entries.Count);
        Assert.Equal(1300, entries.Select(e => e.Name).Distinct().Count());
    }

    [Fact]
    public async Task CreatingADirectoryThatExistsIsAnErrorRatherThanASilentNoOp()
    {
        // Directory.CreateDirectory is happy to do nothing, but a user typing a name
        // for a new folder needs to be told the name is taken.
        using var dir = new TemporaryDirectory();
        dir.Directory_("existing");
        var fs = new LocalFileSystem();
        var ex = await Assert.ThrowsAsync<FileOperationException>(
            () => fs.CreateDirectoryAsync(dir.Location.Child("existing"), None));
        Assert.Equal(FileErrorKind.AlreadyExists, ex.Kind);
    }

    [Fact]
    public async Task RenameRefusesToOverwriteAnExistingTarget()
    {
        // Overwriting is the conflict resolver's decision to make, not a silent
        // side effect of a move.
        using var dir = new TemporaryDirectory();
        dir.File("a", "one");
        dir.File("b", "two");
        var fs = new LocalFileSystem();

        var ex = await Assert.ThrowsAsync<FileOperationException>(
            () => fs.RenameAsync(dir.Location.Child("a"), dir.Location.Child("b"), None));
        Assert.Equal(FileErrorKind.AlreadyExists, ex.Kind);
        Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(dir.Path, "b")));
    }

    [Fact]
    public async Task DeletingANonEmptyDirectoryNeedsRecursive()
    {
        using var dir = new TemporaryDirectory();
        dir.File("full/inner.txt", "x");
        var fs = new LocalFileSystem();
        var target = dir.Location.Child("full");

        var ex = await Assert.ThrowsAsync<FileOperationException>(() => fs.DeleteAsync(target, recursive: false, None));
        Assert.Equal(FileErrorKind.NotEmpty, ex.Kind);

        await fs.DeleteAsync(target, recursive: true, None);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "full")));
    }

    [Fact]
    public async Task DeletingASymlinkToADirectoryUnlinksItAndLeavesTheTargetAlone()
    {
        // Recursing into the link instead would silently wipe the real directory.
        using var dir = new TemporaryDirectory();
        dir.File("real/keep.txt", "important");
        var link = Path.Combine(dir.Path, "link");
        File.CreateSymbolicLink(link, Path.Combine(dir.Path, "real"));

        await new LocalFileSystem().DeleteAsync(Location.FromLocalPath(link), recursive: true, None);

        Assert.False(Path.Exists(link));
        Assert.True(File.Exists(Path.Combine(dir.Path, "real", "keep.txt")));
    }

    [Fact]
    public async Task WritingCreateNewRefusesAnExistingFile()
    {
        using var dir = new TemporaryDirectory();
        dir.File("taken", "x");
        var fs = new LocalFileSystem();
        var ex = await Assert.ThrowsAsync<FileOperationException>(
            () => fs.OpenWriteAsync(dir.Location.Child("taken"), WriteMode.CreateNew, null, None));
        Assert.Equal(FileErrorKind.AlreadyExists, ex.Kind);
    }

    [Fact]
    public async Task ReadAndWriteRoundTrip()
    {
        using var dir = new TemporaryDirectory();
        var fs = new LocalFileSystem();
        var target = dir.Location.Child("out.bin");
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        await using (var stream = await fs.OpenWriteAsync(target, WriteMode.Overwrite, payload.Length, None))
            await stream.WriteAsync(payload, None);

        await using var read = await fs.OpenReadAsync(target, None);
        var buffer = new MemoryStream();
        await read.CopyToAsync(buffer, None);
        Assert.Equal(payload, buffer.ToArray());
    }

    [Fact]
    public async Task StatReportsTheModeAndTheLinkTarget()
    {
        using var dir = new TemporaryDirectory();
        var real = dir.File("real.txt", "x");
        var link = Path.Combine(dir.Path, "link.txt");
        File.CreateSymbolicLink(link, real);
        File.SetUnixFileMode(real, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var fs = new LocalFileSystem();
        var stat = await fs.StatAsync(Location.FromLocalPath(real), None);
        Assert.Equal(0b110_000_000, stat.UnixMode);

        var linkStat = await fs.StatAsync(Location.FromLocalPath(link), None);
        Assert.Equal(real, linkStat.SymlinkTarget);
    }

    [Fact]
    public async Task ANonLocalLocationIsRefusedRatherThanMisinterpreted()
    {
        var fs = new LocalFileSystem();
        var ex = await Assert.ThrowsAsync<FileOperationException>(
            () => fs.StatAsync(Location.Parse("smb://host/share/x"), None));
        Assert.Equal(FileErrorKind.Unsupported, ex.Kind);
    }

    [Fact]
    public async Task FreeSpaceIsReportedForARealDirectory()
    {
        using var dir = new TemporaryDirectory();
        var space = await new LocalFileSystem().GetFreeSpaceAsync(dir.Location, None);
        Assert.NotNull(space);
        Assert.True(space!.Value.TotalBytes > 0);
    }

    private static async Task<List<FileEntry>> Collect(IAsyncEnumerable<FileEntry> source)
    {
        var list = new List<FileEntry>();
        await foreach (var entry in source)
            list.Add(entry);
        return list;
    }
}
