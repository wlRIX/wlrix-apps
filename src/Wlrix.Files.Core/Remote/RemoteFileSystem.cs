namespace Wlrix.Files.Core.Remote;

/// <summary>
/// An <see cref="IFileSystem"/> over a remote server, with the connection management.
/// </summary>
/// <remarks>
/// Everything that is hard about a remote filesystem and nothing that is protocol-specific:
/// how many connections there are, which operation is allowed to use one, what happens when the
/// server drops it, and when an unused one is closed. The protocol itself is an
/// <see cref="IRemoteSession"/>, which is what makes all of this testable without a network.
///
/// <para>
/// <b>Connections are borrowed, not shared.</b> A session does one thing at a time, so an
/// operation takes one for its duration and gives it back — and a stream holds one until it is
/// disposed, because the bytes are still arriving. Serializing everything onto a single
/// connection instead would deadlock the first copy within a share: the read stream would hold
/// the connection while the write waited for it.
/// </para>
/// </remarks>
public sealed class RemoteFileSystem : IFileSystem
{
    private readonly Func<IRemoteSession> _factory;
    private readonly RemoteOptions _options;
    private readonly SemaphoreSlim _slots;
    private readonly Lock _gate = new();
    private readonly List<Pooled> _idle = [];
    private readonly List<IRemoteSession> _live = [];
    private bool _disposed;

    /// <param name="mountKey">The mount this serves. Matches <see cref="Location.MountKey"/>.</param>
    /// <param name="capabilities">
    /// What the protocol can do, from the caller rather than from a session, because it has to
    /// be answerable before anything has connected — the operations engine asks before it
    /// decides whether a move is a rename.
    /// </param>
    /// <param name="factory">Makes a new, unconnected session.</param>
    public RemoteFileSystem(
        string mountKey,
        FileSystemCapabilities capabilities,
        Func<IRemoteSession> factory,
        RemoteOptions options = default)
    {
        MountKey = mountKey;
        Capabilities = capabilities;
        _factory = factory;
        _options = options;
        _slots = new SemaphoreSlim(options.EffectiveMaxSessions, options.EffectiveMaxSessions);
    }

    /// <inheritdoc/>
    public string MountKey { get; }

    /// <inheritdoc/>
    public FileSystemCapabilities Capabilities { get; }

    /// <summary>How many connections are currently open. For tests and the status line.</summary>
    public int OpenConnections
    {
        get
        {
            lock (_gate)
                return _live.Count;
        }
    }

    // --- the operations ---------------------------------------------------

    /// <inheritdoc/>
    public Task<FileStat> StatAsync(Location location, CancellationToken cancellationToken) =>
        RunAsync((session, token) => session.StatAsync(location, token), cancellationToken);

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(Location location, CancellationToken cancellationToken) =>
        RunAsync((session, token) => session.ExistsAsync(location, token), cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// The whole listing is read onto one borrowed connection before any of it is handed back.
    /// Yielding as it arrives would keep the connection borrowed for as long as the *consumer*
    /// takes, and a consumer that stops to render icons would hold a connection open across a
    /// user's reading speed. The batching that keeps a large directory responsive is the
    /// listing's job, one layer up.
    /// </remarks>
    public async IAsyncEnumerable<FileEntry> EnumerateAsync(
        Location location,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var entries = await RunAsync(async (session, token) =>
        {
            var read = new List<FileEntry>();
            await foreach (var entry in session.EnumerateAsync(location, token).ConfigureAwait(false))
                read.Add(entry);
            return read;
        }, cancellationToken).ConfigureAwait(false);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }
    }

    /// <inheritdoc/>
    public Task<Stream> OpenReadAsync(Location location, CancellationToken cancellationToken) =>
        OpenAsync((session, token) => session.OpenReadAsync(location, token), cancellationToken);

    /// <inheritdoc/>
    public Task<Stream> OpenWriteAsync(Location location, WriteMode mode, long? length, CancellationToken cancellationToken) =>
        OpenAsync((session, token) => session.OpenWriteAsync(location, mode, length, token), cancellationToken);

    /// <inheritdoc/>
    public Task CreateDirectoryAsync(Location location, CancellationToken cancellationToken) =>
        RunAsync(async (session, token) =>
        {
            await session.CreateDirectoryAsync(location, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <inheritdoc/>
    public Task DeleteAsync(Location location, bool recursive, CancellationToken cancellationToken) =>
        RunAsync(async (session, token) =>
        {
            await session.DeleteAsync(location, recursive, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <inheritdoc/>
    public Task RenameAsync(Location from, Location to, CancellationToken cancellationToken) =>
        RunAsync(async (session, token) =>
        {
            await session.RenameAsync(from, to, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <inheritdoc/>
    public Task SetModifiedAsync(Location location, DateTimeOffset modified, CancellationToken cancellationToken) =>
        RunAsync(async (session, token) =>
        {
            await session.SetModifiedAsync(location, modified, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <inheritdoc/>
    public Task SetUnixModeAsync(Location location, int mode, CancellationToken cancellationToken) =>
        RunAsync(async (session, token) =>
        {
            await session.SetUnixModeAsync(location, mode, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <inheritdoc/>
    public Task<FreeSpace?> GetFreeSpaceAsync(Location location, CancellationToken cancellationToken) =>
        RunAsync((session, token) => session.GetFreeSpaceAsync(location, token), cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// A remote filesystem cannot make a symlink that means anything here — the path it would
    /// hold is the server's, not this machine's — so it refuses rather than writing one that
    /// points nowhere. <see cref="FileSystemCapabilities.SymLink"/> is absent for the same
    /// reason and the engine checks that first; this is the backstop.
    /// </remarks>
    public Task CreateSymlinkAsync(Location link, string target, CancellationToken cancellationToken) =>
        throw new FileOperationException(FileErrorKind.Unsupported, link, "remote symlinks are not supported");

    // --- borrowing a connection -------------------------------------------

    /// <summary>Runs one operation on a borrowed connection, reconnecting once if it drops.</summary>
    private async Task<T> RunAsync<T>(
        Func<IRemoteSession, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        var lease = await BorrowAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await AttemptAsync(lease, work, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Return(lease);
        }
    }

    /// <summary>Opens a stream that holds its connection until the stream is disposed.</summary>
    private async Task<Stream> OpenAsync(
        Func<IRemoteSession, CancellationToken, Task<Stream>> open,
        CancellationToken cancellationToken)
    {
        var lease = await BorrowAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = await AttemptAsync(lease, open, cancellationToken).ConfigureAwait(false);
            return new LeasedStream(stream, this, lease);
        }
        catch
        {
            // The lease is only handed to the stream once there is a stream to hand it to.
            Return(lease);
            throw;
        }
    }

    /// <summary>Runs the work, re-establishing a dropped connection and trying again.</summary>
    /// <remarks>
    /// A connection is only discovered to be gone by using it, so this is a retry rather than
    /// a check. The retry is limited and deliberately narrow: only
    /// <see cref="FileErrorKind.ConnectionLost"/>, because retrying an access denial or a
    /// missing file would just take longer to fail.
    /// </remarks>
    private async Task<T> AttemptAsync<T>(
        Lease lease,
        Func<IRemoteSession, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!lease.Session.IsConnected)
                    await lease.Session.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return await work(lease.Session, cancellationToken).ConfigureAwait(false);
            }
            catch (FileOperationException ex)
                when (ex.Kind == FileErrorKind.ConnectionLost && attempt < _options.EffectiveReconnectAttempts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReplaceAsync(lease).ConfigureAwait(false);
            }
        }
    }

    private async Task<Lease> BorrowAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            // Swept before anything is taken, not only when one is given back. A connection
            // that has sat past the timeout is the one most likely to have been dropped at
            // the far end, so handing it out is precisely the failure the timeout exists to
            // avoid -- and a mount used steadily by one caller would otherwise never sweep at
            // all, because its one connection is never idle for long.
            SweepIdle();

            // Newest first: an idle pool drains from the back, so the connections that go
            // untouched long enough to be swept are the ones that were going to be anyway.
            for (var i = _idle.Count - 1; i >= 0; i--)
            {
                var pooled = _idle[i];
                _idle.RemoveAt(i);
                if (pooled.Session.IsConnected)
                    return new Lease(pooled.Session);
                // Dropped while it sat there. Discarded rather than handed out, so the
                // reconnect happens on a fresh session instead of on a known-dead one.
                Retire(pooled.Session);
            }
        }

        var session = _factory();
        lock (_gate)
            _live.Add(session);
        return new Lease(session);
    }

    private void Return(Lease lease)
    {
        lock (_gate)
        {
            if (_disposed || !lease.Session.IsConnected)
                Retire(lease.Session);
            else
                _idle.Add(new Pooled(lease.Session, DateTimeOffset.UtcNow));

            SweepIdle();
        }
        _slots.Release();
    }

    /// <summary>Throws away a lease's dead session and gives the lease a fresh one.</summary>
    private async Task ReplaceAsync(Lease lease)
    {
        var dead = lease.Session;
        lock (_gate)
            Retire(dead);
        await Quietly(dead).ConfigureAwait(false);

        var session = _factory();
        lock (_gate)
            _live.Add(session);
        lease.Session = session;
    }

    /// <summary>Closes connections nobody has wanted for a while.</summary>
    /// <remarks>
    /// Swept when the pool is touched rather than on a timer. A mount that nobody is using is a
    /// mount that will not be sweeping either — but it is also not holding anything a timer
    /// would usefully free, and a timer per mount is a thread's worth of wakeups for the sake
    /// of tidiness nobody can see. The mount itself is disposed when the last window closes.
    /// </remarks>
    private void SweepIdle()
    {
        var cutoff = DateTimeOffset.UtcNow - _options.EffectiveIdleTimeout;
        for (var i = _idle.Count - 1; i >= 0; i--)
        {
            if (_idle[i].Since > cutoff)
                continue;
            var session = _idle[i].Session;
            _idle.RemoveAt(i);
            Retire(session);
            _ = Quietly(session);
        }
    }

    /// <summary>Forgets a session. Must be called under the gate.</summary>
    private void Retire(IRemoteSession session) => _live.Remove(session);

    private static async Task Quietly(IRemoteSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileOperationException or IOException or ObjectDisposedException)
        {
            // Closing a connection that is already gone is the ordinary case, not an error.
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<IRemoteSession> closing;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            closing = [.. _live];
            _live.Clear();
            _idle.Clear();
        }

        foreach (var session in closing)
            await Quietly(session).ConfigureAwait(false);
    }

    /// <summary>A borrowed connection. Mutable, because a reconnect swaps the session under it.</summary>
    private sealed class Lease(IRemoteSession session)
    {
        public IRemoteSession Session { get; set; } = session;
    }

    private readonly record struct Pooled(IRemoteSession Session, DateTimeOffset Since);

    /// <summary>A stream that gives its connection back when it is closed.</summary>
    /// <remarks>
    /// The bytes are still arriving after the call that opened it returned, so the connection
    /// cannot go back in the pool until the caller is finished with it. Everything is
    /// forwarded rather than inherited from a wrapper base, because the one method that
    /// matters here is the one that disposes.
    /// </remarks>
    private sealed class LeasedStream(Stream inner, RemoteFileSystem owner, Lease lease) : Stream
    {
        private bool _returned;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        public override async ValueTask DisposeAsync()
        {
            if (!_returned)
            {
                _returned = true;
                try
                {
                    await inner.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    // Given back even if closing the stream threw: a lease that is never
                    // returned is a connection slot that never comes back, and four of those
                    // is a mount that has stopped working with no error to show for it.
                    owner.Return(lease);
                }
            }
            await base.DisposeAsync().ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_returned)
            {
                _returned = true;
                try
                {
                    inner.Dispose();
                }
                finally
                {
                    owner.Return(lease);
                }
            }
            base.Dispose(disposing);
        }
    }
}
