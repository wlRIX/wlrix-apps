using Wlrix.Files.Core.Operations;
using Wlrix.Files.Core.Testing;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// Making a reference rather than a copy — the "Make Reference" the desktop's Selected menu
/// has drawn grayed out since it was written, and what Control+Shift asks a drag for.
/// </summary>
public class LinkOperationTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static OperationJob Job(FakeFileSystem fs, IConflictResolver? conflicts = null) =>
        new(new SingleProvider(fs), conflicts);

    private sealed class SingleProvider(FakeFileSystem fs) : IFileSystemProvider
    {
        public Task<IFileSystem> GetAsync(Location location, CancellationToken cancellationToken) =>
            Task.FromResult<IFileSystem>(fs);
    }

    [Fact]
    public async Task LinkingAFilePutsAReferenceAtTheTargetAndLeavesTheOriginal()
    {
        var fs = new FakeFileSystem().AddFile("/src/a.txt", "hello").AddDirectory("/dst");
        var result = await Job(fs).RunAsync(
            FileOperation.Link([Location.Parse("/src/a.txt")], Location.Parse("/dst")), null, None);

        Assert.True(result.Succeeded);
        Assert.Contains("/dst/a.txt -> /src/a.txt", fs.Snapshot());
        Assert.Equal("hello"u8.ToArray(), fs.ContentOf("/src/a.txt"));
    }

    [Fact]
    public async Task TheLinkHoldsAnAbsolutePathSoItSurvivesBeingMoved()
    {
        var fs = new FakeFileSystem().AddFile("/a/b/deep.txt", "x").AddDirectory("/elsewhere");
        await Job(fs).RunAsync(
            FileOperation.Link([Location.Parse("/a/b/deep.txt")], Location.Parse("/elsewhere")), null, None);

        Assert.Contains("/elsewhere/deep.txt -> /a/b/deep.txt", fs.Snapshot());
    }

    [Fact]
    public async Task LinkingADirectoryMakesOneLinkRatherThanMirroringTheTree()
    {
        // The scan must not walk into it. If it did, linking a home directory would plan a
        // million items and reproduce the source's whole shape in links.
        var fs = new FakeFileSystem()
            .AddFile("/src/tree/one.txt", "1")
            .AddFile("/src/tree/sub/two.txt", "2")
            .AddDirectory("/dst");

        var result = await Job(fs).RunAsync(
            FileOperation.Link([Location.Parse("/src/tree")], Location.Parse("/dst")), null, None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.ItemsDone);
        Assert.Contains("/dst/tree -> /src/tree", fs.Snapshot());
        Assert.DoesNotContain(fs.Snapshot(), path => path.StartsWith("/dst/tree/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABackendWithoutSymlinksRefusesRatherThanCopyingTheFileInstead()
    {
        // The failure that matters: answering "make me a reference" with a second copy would
        // look like success and only be discovered once the two drifted apart.
        var fs = new FakeFileSystem().AddFile("/src/a.txt", "hello").AddDirectory("/dst");
        fs.Capabilities &= ~FileSystemCapabilities.SymLink;

        var result = await Job(fs).RunAsync(
            FileOperation.Link([Location.Parse("/src/a.txt")], Location.Parse("/dst")),
            null, None);

        Assert.False(result.Succeeded);
        Assert.Equal(FileErrorKind.Unsupported, Assert.Single(result.Errors).Kind);
        Assert.DoesNotContain("/dst/a.txt", fs.Snapshot());
    }

    [Fact]
    public async Task AnExistingNameGoesThroughTheSameConflictResolverAsACopy()
    {
        var fs = new FakeFileSystem().AddFile("/src/a.txt", "new").AddFile("/dst/a.txt", "old");

        var skipped = await Job(fs, FixedConflictResolver.SkipAll).RunAsync(
            FileOperation.Link([Location.Parse("/src/a.txt")], Location.Parse("/dst")), null, None);
        Assert.Equal(1, skipped.ItemsSkipped);
        Assert.Equal("old"u8.ToArray(), fs.ContentOf("/dst/a.txt"));

        var overwritten = await Job(fs, FixedConflictResolver.OverwriteAll).RunAsync(
            FileOperation.Link([Location.Parse("/src/a.txt")], Location.Parse("/dst")), null, None);
        Assert.True(overwritten.Succeeded);
        Assert.Contains("/dst/a.txt -> /src/a.txt", fs.Snapshot());
    }

    [Fact]
    public async Task ARemoteSourceCannotBeLinkedBecauseThePathWouldMeanNothing()
    {
        var fs = new FakeFileSystem("smb://host").AddFile("/share/a.txt", "hello").AddDirectory("/share/dst");
        var result = await Job(fs).RunAsync(
            FileOperation.Link([Location.Parse("smb://host/share/a.txt")], Location.Parse("smb://host/share/dst")),
            null, None);

        Assert.False(result.Succeeded);
        Assert.Equal(FileErrorKind.Unsupported, Assert.Single(result.Errors).Kind);
    }

    [Fact]
    public async Task ALinkToSomethingThatIsNotThereIsStillMade()
    {
        // A dangling link is a real thing a filesystem holds, and refusing to make one would
        // mean stat-ing every source first — which is exactly what the scan skips.
        var fs = new FakeFileSystem().AddDirectory("/dst");
        var result = await Job(fs).RunAsync(
            FileOperation.Link([Location.Parse("/gone/missing.txt")], Location.Parse("/dst")), null, None);

        Assert.True(result.Succeeded);
        Assert.Contains("/dst/missing.txt -> /gone/missing.txt", fs.Snapshot());
    }

    [Fact]
    public async Task TheRealFilesystemMakesALinkTheOperatingSystemAgreesIsOne()
    {
        using var dir = new TemporaryDirectory();
        dir.File("original.txt", "contents");
        dir.Directory_("refs");

        var job = new OperationJob(new FileSystemProvider());
        var result = await job.RunAsync(FileOperation.Link(
            [Location.FromLocalPath(Path.Combine(dir.Path, "original.txt"))],
            Location.FromLocalPath(Path.Combine(dir.Path, "refs"))), null, None);

        Assert.True(result.Succeeded);
        var link = new FileInfo(Path.Combine(dir.Path, "refs", "original.txt"));
        Assert.Equal(Path.Combine(dir.Path, "original.txt"), link.LinkTarget);
        Assert.Equal("contents", File.ReadAllText(link.FullName));
    }
}
