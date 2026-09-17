namespace Wlrix.Files.Core.Remote;

/// <summary>
/// One live connection to a remote server, speaking that server's protocol.
/// </summary>
/// <remarks>
/// The seam the three remote backends sit behind, and the reason there is one: none of
/// <c>SMB2Client</c>, <c>AsyncFtpClient</c> or <c>SftpClient</c> can be driven by a test, and
/// two of the three cannot be driven by CI at all. Everything hard about remote filesystems —
/// serializing, reconnecting, giving up, cancelling a transfer halfway — lives in
/// <see cref="RemoteFileSystem"/> above this interface and is tested against a fake.
///
/// <para>
/// <b>An implementation is not safe for concurrent use.</b> Every one of the three underlying
/// clients is stateful on one connection — FTP most obviously, where the working directory and
/// transfer type are server-side — so a session does one thing at a time and
/// <see cref="RemoteFileSystem"/> is what guarantees it.
/// </para>
///
/// <para>
/// <b>Nothing but <see cref="FileOperationException"/> and
/// <see cref="OperationCanceledException"/> may escape.</b> A leaked <c>FtpException</c> or
/// <c>NTStatus</c> defeats the retry policy and the conflict dialog at once, and the only place
/// that knows how to classify one is the session that produced it.
/// </para>
/// </remarks>
public interface IRemoteSession : IAsyncDisposable
{
    /// <summary>What this backend can do, once connected.</summary>
    FileSystemCapabilities Capabilities { get; }

    /// <summary>Whether the connection is believed to be up.</summary>
    /// <remarks>
    /// Believed, not proven. A server that has gone away is discovered on the next operation,
    /// not by asking — which is why <see cref="RemoteFileSystem"/> retries once rather than
    /// checking first.
    /// </remarks>
    bool IsConnected { get; }

    /// <summary>Opens the connection and authenticates. Called again after a drop.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    Task<FileStat> StatAsync(Location location, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(Location location, CancellationToken cancellationToken);

    IAsyncEnumerable<FileEntry> EnumerateAsync(Location location, CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(Location location, CancellationToken cancellationToken);

    Task<Stream> OpenWriteAsync(Location location, WriteMode mode, long? length, CancellationToken cancellationToken);

    Task CreateDirectoryAsync(Location location, CancellationToken cancellationToken);

    Task DeleteAsync(Location location, bool recursive, CancellationToken cancellationToken);

    Task RenameAsync(Location from, Location to, CancellationToken cancellationToken);

    Task SetModifiedAsync(Location location, DateTimeOffset modified, CancellationToken cancellationToken);

    Task SetUnixModeAsync(Location location, int mode, CancellationToken cancellationToken);

    Task<FreeSpace?> GetFreeSpaceAsync(Location location, CancellationToken cancellationToken);
}

/// <summary>How a remote mount manages its connections.</summary>
/// <param name="MaxSessions">
/// How many connections one mount may open.
/// <para>
/// More than one, and that is not an optimization. A copy within a share opens a read stream
/// and a write stream at the same time, and a session can only do one thing at a time — with a
/// single connection the second open would wait for the first to finish, which waits for the
/// second. Four is enough for a copy, a listing and a thumbnail to overlap.
/// </para>
/// </param>
/// <param name="IdleTimeout">
/// How long an unused connection is kept. Servers drop idle clients on their own schedule and
/// an SMB session left open pins a server-side handle, so this is politeness as much as tidiness.
/// </param>
/// <param name="ReconnectAttempts">
/// How many times a dropped connection is transparently re-established and the operation
/// retried before the failure is handed to the retry policy.
/// <para>
/// One. A network that dropped once is worth a silent retry; a network that drops twice in a
/// row is worth telling somebody about, and the retry policy above already backs off.
/// </para>
/// </param>
public readonly record struct RemoteOptions(
    int MaxSessions = 4,
    TimeSpan IdleTimeout = default,
    int ReconnectAttempts = 1)
{
    // Every field needs one of these. `default(RemoteOptions)` does not run the constructor,
    // so the defaults written above it apply only to callers who name the type -- and the
    // caller who writes nothing at all is exactly the one relying on them. Read directly,
    // ReconnectAttempts was zero for every default-constructed mount, which turned the single
    // transparent retry into no retry at all and looked like a working feature.

    /// <summary>How many connections one mount may open. Never less than one.</summary>
    public int EffectiveMaxSessions => MaxSessions > 0 ? MaxSessions : 4;

    /// <summary>How long an unused connection is kept.</summary>
    public TimeSpan EffectiveIdleTimeout => IdleTimeout > TimeSpan.Zero ? IdleTimeout : TimeSpan.FromMinutes(1);

    /// <summary>How many silent reconnect-and-retry attempts a dropped connection gets.</summary>
    public int EffectiveReconnectAttempts => ReconnectAttempts > 0 ? ReconnectAttempts : 1;
}
