using System.Runtime.CompilerServices;
using Wlrix.Files.Core.Remote;

namespace Wlrix.Files.Core.Testing;

/// <summary>
/// A remote session backed by a <see cref="FakeFileSystem"/>, with a network attached.
/// </summary>
/// <remarks>
/// The tree comes from the fake local filesystem, so this only has to add what is actually
/// remote about a remote filesystem: connecting, taking time, being dropped by the server, and
/// being cut off mid-transfer. Those are the things <see cref="RemoteFileSystem"/> exists to
/// handle and the things no test can provoke reliably against a real server.
/// </remarks>
public sealed class FakeRemoteSession : IRemoteSession
{
    private readonly FakeFileSystem _tree;
    private readonly FakeRemoteServer _server;

    internal FakeRemoteSession(FakeFileSystem tree, FakeRemoteServer server)
    {
        _tree = tree;
        _server = server;
    }

    /// <inheritdoc/>
    public FileSystemCapabilities Capabilities => _tree.Capabilities;

    /// <inheritdoc/>
    public bool IsConnected { get; private set; }

    /// <summary>Whether this session has been disposed. Asserted on, to catch a leaked lease.</summary>
    public bool Disposed { get; private set; }

    /// <summary>How many operations this particular connection has served.</summary>
    public int Operations { get; private set; }

    /// <inheritdoc/>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(_server.ConnectLatency, cancellationToken).ConfigureAwait(false);
        if (_server.RefuseConnections)
            throw new FileOperationException(FileErrorKind.ConnectionLost, Location.Parse("/"), "refused");
        _server.Connects++;
        IsConnected = true;
    }

    /// <summary>Simulates the server hanging up. The next operation discovers it.</summary>
    public void Drop() => IsConnected = false;

    /// <inheritdoc/>
    public Task<FileStat> StatAsync(Location location, CancellationToken cancellationToken) =>
        Guarded(location, () => _tree.StatAsync(Local(location), cancellationToken));

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(Location location, CancellationToken cancellationToken) =>
        Guarded(location, () => _tree.ExistsAsync(Local(location), cancellationToken));

    /// <inheritdoc/>
    public async IAsyncEnumerable<FileEntry> EnumerateAsync(
        Location location, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Begin(location);
        try
        {
            await foreach (var entry in Listing(location, cancellationToken).ConfigureAwait(false))
                yield return entry;
        }
        finally
        {
            // Without this the session stays marked busy for ever after one listing, and the
            // next borrower trips the concurrency check for something it did not do.
            Interlocked.Decrement(ref _busy);
        }
    }

    private async IAsyncEnumerable<FileEntry> Listing(
        Location location, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var entry in _tree.EnumerateAsync(Local(location), cancellationToken).ConfigureAwait(false))
        {
            // Re-keyed onto the remote mount: the tree underneath is a local fake, and a
            // listing that answered with file:// URIs would take the caller somewhere else
            // entirely on the next navigation.
            yield return new FileEntry
            {
                Location = location.Child(entry.Name),
                Name = entry.Name,
                Kind = entry.Kind,
                Size = entry.Size,
                Modified = entry.Modified,
                UnixMode = entry.UnixMode,
                IsHidden = entry.IsHidden
            };
        }
    }

    /// <inheritdoc/>
    public Task<Stream> OpenReadAsync(Location location, CancellationToken cancellationToken) =>
        Guarded(location, () => _tree.OpenReadAsync(Local(location), cancellationToken));

    /// <inheritdoc/>
    public Task<Stream> OpenWriteAsync(Location location, WriteMode mode, long? length, CancellationToken cancellationToken) =>
        Guarded(location, () => _tree.OpenWriteAsync(Local(location), mode, length, cancellationToken));

    /// <inheritdoc/>
    public Task CreateDirectoryAsync(Location location, CancellationToken cancellationToken) =>
        Guarded(location, async () =>
        {
            await _tree.CreateDirectoryAsync(Local(location), cancellationToken).ConfigureAwait(false);
            return true;
        });

    /// <inheritdoc/>
    public Task DeleteAsync(Location location, bool recursive, CancellationToken cancellationToken) =>
        Guarded(location, async () =>
        {
            await _tree.DeleteAsync(Local(location), recursive, cancellationToken).ConfigureAwait(false);
            return true;
        });

    /// <inheritdoc/>
    public Task RenameAsync(Location from, Location to, CancellationToken cancellationToken) =>
        Guarded(from, async () =>
        {
            await _tree.RenameAsync(Local(from), Local(to), cancellationToken).ConfigureAwait(false);
            return true;
        });

    /// <inheritdoc/>
    public Task SetModifiedAsync(Location location, DateTimeOffset modified, CancellationToken cancellationToken) =>
        Guarded(location, async () =>
        {
            await _tree.SetModifiedAsync(Local(location), modified, cancellationToken).ConfigureAwait(false);
            return true;
        });

    /// <inheritdoc/>
    public Task SetUnixModeAsync(Location location, int mode, CancellationToken cancellationToken) =>
        Guarded(location, async () =>
        {
            await _tree.SetUnixModeAsync(Local(location), mode, cancellationToken).ConfigureAwait(false);
            return true;
        });

    /// <inheritdoc/>
    public Task<FreeSpace?> GetFreeSpaceAsync(Location location, CancellationToken cancellationToken) =>
        Guarded(location, () => _tree.GetFreeSpaceAsync(Local(location), cancellationToken));

    /// <summary>The same path on the local fake that holds the tree.</summary>
    private static Location Local(Location location) => Location.Parse(location.Path);

    private void Begin(Location location)
    {
        if (Disposed)
            throw new ObjectDisposedException(nameof(FakeRemoteSession));
        if (!IsConnected || _server.DropEverything)
            throw new FileOperationException(FileErrorKind.ConnectionLost, location, "the server hung up");
        Operations++;
        _server.Operations++;
        // Concurrency is the invariant this fake exists to police: a real session is one
        // connection and cannot be doing two things at once, and a pool that handed the same
        // one to two callers would look fine until a listing arrived shredded.
        if (Interlocked.Increment(ref _busy) > 1)
            throw new InvalidOperationException("two operations on one session at the same time");
    }

    private int _busy;

    private async Task<T> Guarded<T>(Location location, Func<Task<T>> work)
    {
        Begin(location);
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _busy);
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        IsConnected = false;
        _server.Disposals++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>The server the fake sessions connect to: one tree, and a way to misbehave.</summary>
public sealed class FakeRemoteServer
{
    private readonly FakeFileSystem _tree;

    public FakeRemoteServer(FakeFileSystem? tree = null) => _tree = tree ?? new FakeFileSystem();

    /// <summary>The tree every session serves. Build it as you would a local fake.</summary>
    public FakeFileSystem Tree => _tree;

    /// <summary>How long connecting takes.</summary>
    public TimeSpan ConnectLatency { get; set; } = TimeSpan.Zero;

    /// <summary>Whether new connections are refused.</summary>
    public bool RefuseConnections { get; set; }

    /// <summary>Whether every operation reports the connection as gone.</summary>
    public bool DropEverything { get; set; }

    /// <summary>How many connections have been established, reconnects included.</summary>
    public int Connects { get; set; }

    /// <summary>How many operations every session together has served.</summary>
    public int Operations { get; set; }

    /// <summary>How many sessions have been disposed.</summary>
    public int Disposals { get; set; }

    /// <summary>Every session this server has handed out.</summary>
    public List<FakeRemoteSession> Sessions { get; } = [];

    /// <summary>Makes a session, as a factory would.</summary>
    public FakeRemoteSession Open()
    {
        var session = new FakeRemoteSession(_tree, this);
        Sessions.Add(session);
        return session;
    }
}
