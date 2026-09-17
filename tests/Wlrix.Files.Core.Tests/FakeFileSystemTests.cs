using Wlrix.Files.Core.Testing;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// The fake is the instrument every later test measures with, so it gets tested itself.
/// A fault that does not fire, or a tree that does not reflect an operation, would make
/// the operations-engine suite quietly meaningless.
/// </summary>
public class FakeFileSystemTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task FailOnceFiresOnceAndThenGetsOutOfTheWay()
    {
        // This is what makes a retry ladder testable: fail, fail, then succeed.
        var fs = new FakeFileSystem().AddFile("/a.txt", "hello");
        fs.FailOnce("/a.txt", FileErrorKind.Timeout);

        var ex = await Assert.ThrowsAsync<FileOperationException>(() => fs.StatAsync(Location.Parse("/a.txt"), None));
        Assert.Equal(FileErrorKind.Timeout, ex.Kind);

        var stat = await fs.StatAsync(Location.Parse("/a.txt"), None);
        Assert.Equal(5, stat.Size);
    }

    [Fact]
    public async Task QueuedFailuresComeBackInOrder()
    {
        var fs = new FakeFileSystem().AddFile("/a.txt");
        fs.FailOnce("/a.txt", FileErrorKind.ConnectionLost);
        fs.FailOnce("/a.txt", FileErrorKind.Timeout);

        Assert.Equal(FileErrorKind.ConnectionLost,
            (await Assert.ThrowsAsync<FileOperationException>(() => fs.StatAsync(Location.Parse("/a.txt"), None))).Kind);
        Assert.Equal(FileErrorKind.Timeout,
            (await Assert.ThrowsAsync<FileOperationException>(() => fs.StatAsync(Location.Parse("/a.txt"), None))).Kind);
        await fs.StatAsync(Location.Parse("/a.txt"), None);
    }

    [Fact]
    public async Task FailAlwaysKeepsFailingUntilCleared()
    {
        var fs = new FakeFileSystem().AddFile("/a.txt");
        fs.FailAlways("/a.txt", FileErrorKind.AccessDenied);

        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<FileOperationException>(() => fs.StatAsync(Location.Parse("/a.txt"), None));

        fs.ClearFaults();
        await fs.StatAsync(Location.Parse("/a.txt"), None);
    }

    [Fact]
    public async Task OperationCountMakesRetryBehaviorAssertable()
    {
        var fs = new FakeFileSystem().AddFile("/a.txt");
        await fs.StatAsync(Location.Parse("/a.txt"), None);
        await fs.ExistsAsync(Location.Parse("/a.txt"), None);
        Assert.Equal(2, fs.OperationCount);
    }

    [Fact]
    public async Task LatencyMakesCancellationReachable()
    {
        var fs = new FakeFileSystem { Latency = TimeSpan.FromSeconds(30) };
        fs.AddFile("/a.txt");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fs.StatAsync(Location.Parse("/a.txt"), cts.Token));
    }

    [Fact]
    public async Task WritesLandInTheTreeSoTheResultCanBeAsserted()
    {
        var fs = new FakeFileSystem().AddDirectory("/out");
        await using (var stream = await fs.OpenWriteAsync(Location.Parse("/out/x.txt"), WriteMode.Overwrite, null, None))
            await stream.WriteAsync("payload"u8.ToArray(), None);

        Assert.Equal("payload"u8.ToArray(), fs.ContentOf("/out/x.txt"));
    }

    [Fact]
    public async Task RenameMovesAWholeSubtree()
    {
        var fs = new FakeFileSystem().AddFile("/src/deep/inner.txt", "x").AddDirectory("/dst");
        await fs.RenameAsync(Location.Parse("/src"), Location.Parse("/dst/moved"), None);

        Assert.False(fs.Contains("/src"));
        Assert.Equal("x"u8.ToArray(), fs.ContentOf("/dst/moved/deep/inner.txt"));
    }

    [Fact]
    public async Task ClearingTheRenameCapabilityMakesRenameUnsupported()
    {
        // How a test drives the engine down its copy-and-delete fallback.
        var fs = new FakeFileSystem().AddFile("/a.txt");
        fs.Capabilities &= ~FileSystemCapabilities.Rename;
        var ex = await Assert.ThrowsAsync<FileOperationException>(
            () => fs.RenameAsync(Location.Parse("/a.txt"), Location.Parse("/b.txt"), None));
        Assert.Equal(FileErrorKind.Unsupported, ex.Kind);
    }

    [Fact]
    public async Task DeletingANonEmptyDirectoryNeedsRecursiveHereToo()
    {
        var fs = new FakeFileSystem().AddFile("/d/inner.txt");
        var ex = await Assert.ThrowsAsync<FileOperationException>(
            () => fs.DeleteAsync(Location.Parse("/d"), recursive: false, None));
        Assert.Equal(FileErrorKind.NotEmpty, ex.Kind);

        await fs.DeleteAsync(Location.Parse("/d"), recursive: true, None);
        Assert.False(fs.Contains("/d"));
    }

    [Fact]
    public void SnapshotGivesAReadableWholeTreeAssertion()
    {
        var fs = new FakeFileSystem().AddFile("/a/b.txt").AddDirectory("/a/c");
        Assert.Equal(["/a/", "/a/b.txt", "/a/c/"], fs.Snapshot());
    }
}
