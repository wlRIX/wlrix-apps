using Wlrix.Files.Core.Remote;
using Wlrix.Files.Core.Testing;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// Connection management: pooling, reconnecting, giving up and closing.
/// </summary>
/// <remarks>
/// Everything here is a thing a real server does at a time of its own choosing — hanging up
/// mid-copy, refusing a reconnect, going quiet — which makes all of it impossible to provoke
/// reliably against one and easy against a fake. That split is why the protocol sits behind
/// <see cref="IRemoteSession"/> at all.
/// </remarks>
public class RemoteFileSystemTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const string Mount = "smb://nas";

    private static (RemoteFileSystem, FakeRemoteServer) Mounted(
        Action<FakeFileSystem>? build = null, RemoteOptions options = default)
    {
        var server = new FakeRemoteServer(new FakeFileSystem(Mount));
        build?.Invoke(server.Tree);
        var fs = new RemoteFileSystem(Mount, server.Tree.Capabilities, server.Open, options);
        return (fs, server);
    }

    private static Location At(string path) => Location.Parse(Mount + path);

    [Fact]
    public async Task NothingConnectsUntilSomethingIsAsked()
    {
        // Mounting a share in the sidebar must not open a connection, or a saved share list
        // would dial every server in it at startup.
        var (fs, server) = Mounted();
        Assert.Equal(0, server.Connects);
        Assert.Equal(0, fs.OpenConnections);

        await fs.ExistsAsync(At("/share"), None);
        Assert.Equal(1, server.Connects);
    }

    [Fact]
    public async Task ConnectionsAreReusedRatherThanOpenedPerOperation()
    {
        var (fs, server) = Mounted(tree => tree.AddDirectory("/share/a").AddFile("/share/b.txt", "x"));

        for (var i = 0; i < 10; i++)
            await fs.ExistsAsync(At("/share/b.txt"), None);

        Assert.Equal(1, server.Connects);
        Assert.Equal(1, fs.OpenConnections);
    }

    [Fact]
    public async Task TwoStreamsAtOnceGetTwoConnections()
    {
        // The case a single shared connection deadlocks on, and it is the ordinary one: a copy
        // within a share holds the source open while it opens the target.
        var (fs, server) = Mounted(tree => tree.AddFile("/share/from.txt", "hello").AddDirectory("/share"));

        await using var read = await fs.OpenReadAsync(At("/share/from.txt"), None);
        await using var write = await fs.OpenWriteAsync(At("/share/to.txt"), WriteMode.Overwrite, null, None);

        Assert.Equal(2, server.Connects);
        Assert.Equal(2, fs.OpenConnections);
    }

    [Fact]
    public async Task AStreamGivesItsConnectionBackWhenItIsClosed()
    {
        var (fs, server) = Mounted(tree => tree.AddFile("/share/a.txt", "hello"));

        var stream = await fs.OpenReadAsync(At("/share/a.txt"), None);
        await stream.DisposeAsync();

        // Reused rather than a second one opened, which is what proves the lease came back.
        await fs.ExistsAsync(At("/share/a.txt"), None);
        Assert.Equal(1, server.Connects);
    }

    [Fact]
    public async Task AStreamThatThrowsOnCloseStillGivesTheConnectionBack()
    {
        // A lease that is never returned is a pool slot that never comes back, and enough of
        // those is a mount that has silently stopped working.
        var (fs, _) = Mounted(tree => tree.AddFile("/share/a.txt", "hello"),
            new RemoteOptions(MaxSessions: 1));

        var stream = await fs.OpenReadAsync(At("/share/a.txt"), None);
        stream.Dispose();

        // With one slot, this can only succeed if the slot was released.
        await fs.ExistsAsync(At("/share/a.txt"), None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NoTwoOperationsShareOneConnection()
    {
        // The fake throws if it is asked to do two things at once, so this passing is the
        // assertion. A pool that handed one session to two callers would look fine until a
        // listing came back shredded.
        var (fs, _) = Mounted(tree =>
        {
            for (var i = 0; i < 40; i++)
                tree.AddFile($"/share/f{i}.txt", "x");
        });

        await Task.WhenAll(Enumerable.Range(0, 40)
            .Select(i => fs.ExistsAsync(At($"/share/f{i}.txt"), None)));
    }

    [Fact]
    public async Task NoMoreConnectionsAreOpenedThanTheMountAllows()
    {
        var (fs, server) = Mounted(tree =>
        {
            for (var i = 0; i < 30; i++)
                tree.AddFile($"/share/f{i}.txt", "x");
        }, new RemoteOptions(MaxSessions: 2));

        await Task.WhenAll(Enumerable.Range(0, 30)
            .Select(i => fs.ExistsAsync(At($"/share/f{i}.txt"), None)));

        Assert.True(server.Connects <= 2, $"opened {server.Connects} connections for a limit of 2");
    }

    [Fact]
    public async Task ADroppedConnectionIsReestablishedWithoutTheCallerNoticing()
    {
        var (fs, server) = Mounted(tree => tree.AddFile("/share/a.txt", "hello"));
        await fs.ExistsAsync(At("/share/a.txt"), None);

        // The server hung up while nobody was looking, which is how it always happens.
        server.Sessions[0].Drop();

        Assert.True(await fs.ExistsAsync(At("/share/a.txt"), None));
        Assert.Equal(2, server.Connects);
    }

    [Fact]
    public async Task ASecondDropInARowIsReportedRatherThanRetriedForever()
    {
        // One silent retry is a courtesy for a flaky network. Two in a row means something is
        // actually wrong, and the retry policy above already knows how to back off.
        var (fs, server) = Mounted(tree => tree.AddFile("/share/a.txt", "hello"));
        server.DropEverything = true;

        var error = await Assert.ThrowsAsync<FileOperationException>(
            () => fs.ExistsAsync(At("/share/a.txt"), None));

        Assert.Equal(FileErrorKind.ConnectionLost, error.Kind);
        Assert.Equal(2, server.Connects);
    }

    [Fact]
    public async Task AnErrorThatIsNotAConnectionLossIsNotRetried()
    {
        // Retrying an access denial or a missing file only takes longer to fail, and on a
        // slow link it takes noticeably longer.
        var (fs, server) = Mounted(tree => tree.AddFile("/share/a.txt", "hello"));
        server.Tree.FailAlways("/share/a.txt", FileErrorKind.AccessDenied);

        var error = await Assert.ThrowsAsync<FileOperationException>(
            () => fs.StatAsync(At("/share/a.txt"), None));

        Assert.Equal(FileErrorKind.AccessDenied, error.Kind);
        Assert.Equal(1, server.Connects);
    }

    [Fact]
    public async Task AConnectionFoundDeadInThePoolIsReplacedRatherThanHandedOut()
    {
        var (fs, server) = Mounted(tree => tree.AddFile("/share/a.txt", "hello"));
        await fs.ExistsAsync(At("/share/a.txt"), None);

        // It died sitting idle. The next borrower should get a fresh one, not this.
        server.Sessions[0].Drop();
        await fs.ExistsAsync(At("/share/a.txt"), None);

        Assert.Equal(2, server.Connects);
        Assert.Equal(1, fs.OpenConnections);
    }

    [Fact]
    public async Task AnIdleConnectionIsClosedRatherThanLeftPinningTheServer()
    {
        var (fs, server) = Mounted(
            tree => tree.AddFile("/share/a.txt", "hello"),
            new RemoteOptions(IdleTimeout: TimeSpan.FromMilliseconds(1)));

        await fs.ExistsAsync(At("/share/a.txt"), None);
        await Task.Delay(20);
        // The sweep happens when the pool is next touched.
        await fs.ExistsAsync(At("/share/a.txt"), None);

        Assert.True(server.Disposals >= 1, "the idle connection should have been closed");
    }

    [Fact]
    public async Task DisposingTheMountClosesEveryConnection()
    {
        var (fs, server) = Mounted(tree => tree.AddFile("/share/a.txt", "hello").AddDirectory("/share"));
        await using (await fs.OpenReadAsync(At("/share/a.txt"), None))
        await using (await fs.OpenWriteAsync(At("/share/b.txt"), WriteMode.Overwrite, null, None))
        {
        }

        await fs.DisposeAsync();

        Assert.Equal(0, fs.OpenConnections);
        Assert.All(server.Sessions, session => Assert.True(session.Disposed));
    }

    [Fact]
    public async Task AnOperationOnADisposedMountSaysSoRatherThanHanging()
    {
        var (fs, _) = Mounted(tree => tree.AddFile("/share/a.txt", "hello"));
        await fs.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => fs.ExistsAsync(At("/share/a.txt"), None));
    }

    [Fact]
    public async Task ListingReturnsTheRemoteLocationsRatherThanTheServersOwnPaths()
    {
        // The entries a listing yields are what the next navigation uses. Answering with
        // anything but this mount's URIs would walk the user off the share.
        var (fs, _) = Mounted(tree => tree.AddFile("/share/a.txt", "x").AddDirectory("/share/sub"));

        var entries = new List<FileEntry>();
        await foreach (var entry in fs.EnumerateAsync(At("/share"), None))
            entries.Add(entry);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal(Mount, entry.Location.MountKey));
        Assert.Contains(entries, entry => entry.Location.ToUriString() == "smb://nas/share/a.txt");
    }

    [Fact]
    public async Task AListingDoesNotHoldItsConnectionWhileTheCallerReadsIt()
    {
        // Yielding straight off the wire would keep a connection borrowed for as long as the
        // consumer takes, and the consumer here stops to render icons.
        var (fs, server) = Mounted(tree =>
        {
            for (var i = 0; i < 5; i++)
                tree.AddFile($"/share/f{i}.txt", "x");
        }, new RemoteOptions(MaxSessions: 1));

        await foreach (var _ in fs.EnumerateAsync(At("/share"), None))
        {
            // With one connection in the pool, this can only work if the listing gave it back.
            await fs.ExistsAsync(At("/share/f0.txt"), None).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ARemoteFilesystemRefusesToMakeASymlink()
    {
        // The path a link would hold is the server's, and it means nothing on this machine.
        var (fs, _) = Mounted(tree => tree.AddFile("/share/a.txt", "x"));

        var error = await Assert.ThrowsAsync<FileOperationException>(
            () => fs.CreateSymlinkAsync(At("/share/link"), "/share/a.txt", None));
        Assert.Equal(FileErrorKind.Unsupported, error.Kind);
    }

    [Fact]
    public async Task AServerThatWillNotAnswerAtAllReportsAConnectionLoss()
    {
        var (fs, server) = Mounted();
        server.RefuseConnections = true;

        var error = await Assert.ThrowsAsync<FileOperationException>(
            () => fs.ExistsAsync(At("/share"), None));
        Assert.Equal(FileErrorKind.ConnectionLost, error.Kind);
    }

    [Fact]
    public async Task CancellingWhileWaitingForAConnectionDoesNotLeakTheSlot()
    {
        var (fs, server) = Mounted(tree => tree.AddFile("/share/a.txt", "x"),
            new RemoteOptions(MaxSessions: 1));
        server.ConnectLatency = TimeSpan.FromMilliseconds(200);

        using var canceled = new CancellationTokenSource();
        var slow = fs.ExistsAsync(At("/share/a.txt"), canceled.Token);
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow);

        // The one slot has to come back, or the mount is finished.
        await fs.ExistsAsync(At("/share/a.txt"), None).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
