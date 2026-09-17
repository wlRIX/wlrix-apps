using System.Runtime.CompilerServices;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Wlrix.Files.Core.Remote;

/// <summary>SFTP, over SSH.NET.</summary>
/// <remarks>
/// The most capable of the three backends by some distance: SFTP is a filesystem protocol
/// rather than a file-transfer one, so rename, POSIX modes and timestamps all work and a move
/// within a server is genuinely a rename.
///
/// <para>
/// <b>There is no anonymous SFTP.</b> The protocol runs inside an authenticated SSH session,
/// and a server that accepted anonymous logins would be handing out a shell. The connect
/// dialog says so rather than offering a checkbox that can only fail.
/// </para>
/// </remarks>
public sealed class SftpSession(string host, int port, ShareCredentials credentials) : IRemoteSession
{
    /// <summary>The port SSH uses when a location does not name one.</summary>
    public const int DefaultPort = 22;

    private SftpClient? _client;

    /// <inheritdoc/>
    public FileSystemCapabilities Capabilities =>
        FileSystemCapabilities.Rename
        | FileSystemCapabilities.PosixMode
        | FileSystemCapabilities.RandomWriteAccess
        | FileSystemCapabilities.PreservesMTime
        | FileSystemCapabilities.CaseSensitive;

    /// <inheritdoc/>
    public bool IsConnected => _client is { IsConnected: true };

    /// <inheritdoc/>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (credentials.Anonymous)
        {
            throw new FileOperationException(FileErrorKind.AccessDenied, Root,
                "SFTP has no anonymous login");
        }

        await DisposeClientAsync().ConfigureAwait(false);
        var client = new SftpClient(host, port < 0 ? DefaultPort : port,
            credentials.Username, credentials.Password);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            _client = client;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            client.Dispose();
            throw Classify(ex, Root);
        }
    }

    /// <inheritdoc/>
    public Task<FileStat> StatAsync(Location location, CancellationToken cancellationToken) =>
        Run(location, async client =>
        {
            var file = await client.GetAsync(location.Path, cancellationToken).ConfigureAwait(false);
            return Stat(location, file);
        });

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(Location location, CancellationToken cancellationToken) =>
        Run(location, client => client.ExistsAsync(location.Path, cancellationToken));

    /// <inheritdoc/>
    public async IAsyncEnumerable<FileEntry> EnumerateAsync(
        Location location, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = Client(location);
        // Materialized inside the try, because the enumerator itself throws on a dead
        // connection and an iterator method cannot have a try/catch around a yield.
        List<ISftpFile> files;
        try
        {
            files = [];
            await foreach (var file in client.ListDirectoryAsync(location.Path, cancellationToken).ConfigureAwait(false))
                files.Add(file);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Classify(ex, location);
        }

        foreach (var file in files)
        {
            // The server lists these two; they are navigation, not contents.
            if (file.Name is "." or "..")
                continue;
            yield return Entry(location.Child(file.Name), file);
        }
    }

    /// <inheritdoc/>
    public Task<Stream> OpenReadAsync(Location location, CancellationToken cancellationToken) =>
        Run(location, client => Task.FromResult<Stream>(client.OpenRead(location.Path)));

    /// <inheritdoc/>
    public Task<Stream> OpenWriteAsync(Location location, WriteMode mode, long? length, CancellationToken cancellationToken) =>
        Run(location, client => Task.FromResult<Stream>(
            client.Open(location.Path, mode == WriteMode.Append ? FileMode.Append : FileMode.Create,
                FileAccess.Write)));

    /// <inheritdoc/>
    public Task CreateDirectoryAsync(Location location, CancellationToken cancellationToken) =>
        Run(location, async client =>
        {
            await client.CreateDirectoryAsync(location.Path, cancellationToken).ConfigureAwait(false);
            return true;
        });

    /// <inheritdoc/>
    public Task DeleteAsync(Location location, bool recursive, CancellationToken cancellationToken) =>
        Run(location, async client =>
        {
            var file = await client.GetAsync(location.Path, cancellationToken).ConfigureAwait(false);
            if (!file.IsDirectory)
            {
                await client.DeleteFileAsync(location.Path, cancellationToken).ConfigureAwait(false);
                return true;
            }

            // SFTP's rmdir only removes an empty directory, so a recursive delete is walked
            // here. The engine already deletes depth-first, but a directory dropped whole --
            // a Trash on a share, say -- arrives as one call and has to be handled.
            if (recursive)
                await ClearAsync(client, location, cancellationToken).ConfigureAwait(false);
            await client.DeleteDirectoryAsync(location.Path, cancellationToken).ConfigureAwait(false);
            return true;
        });

    private static async Task ClearAsync(SftpClient client, Location directory, CancellationToken cancellationToken)
    {
        await foreach (var file in client.ListDirectoryAsync(directory.Path, cancellationToken).ConfigureAwait(false))
        {
            if (file.Name is "." or "..")
                continue;
            var child = directory.Child(file.Name);
            if (file.IsDirectory)
            {
                await ClearAsync(client, child, cancellationToken).ConfigureAwait(false);
                await client.DeleteDirectoryAsync(child.Path, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await client.DeleteFileAsync(child.Path, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public Task RenameAsync(Location from, Location to, CancellationToken cancellationToken) =>
        Run(from, async client =>
        {
            await client.RenameFileAsync(from.Path, to.Path, cancellationToken).ConfigureAwait(false);
            return true;
        });

    /// <inheritdoc/>
    public Task SetModifiedAsync(Location location, DateTimeOffset modified, CancellationToken cancellationToken) =>
        Run(location, client =>
        {
            client.SetLastWriteTimeUtc(location.Path, modified.UtcDateTime);
            return Task.FromResult(true);
        });

    /// <inheritdoc/>
    public Task SetUnixModeAsync(Location location, int mode, CancellationToken cancellationToken) =>
        Run(location, client =>
        {
            client.ChangePermissions(location.Path, (short)mode);
            return Task.FromResult(true);
        });

    /// <inheritdoc/>
    /// <remarks>
    /// SFTP's <c>statvfs</c> extension is optional and many servers do not answer it, so this
    /// reports nothing rather than guessing. The status bar shows no free-space figure for a
    /// share, which is honest — the alternative is a number that is silently wrong.
    /// </remarks>
    public Task<FreeSpace?> GetFreeSpaceAsync(Location location, CancellationToken cancellationToken) =>
        Task.FromResult<FreeSpace?>(null);

    private static Location Root => Location.Parse("/");

    private SftpClient Client(Location location) =>
        _client is { IsConnected: true } client
            ? client
            : throw new FileOperationException(FileErrorKind.ConnectionLost, location, "not connected");

    private async Task<T> Run<T>(Location location, Func<SftpClient, Task<T>> work)
    {
        var client = Client(location);
        try
        {
            return await work(client).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Classify(ex, location);
        }
    }

    private static FileStat Stat(Location location, ISftpFile file) => new()
    {
        Location = location,
        Kind = KindOf(file),
        Size = file.IsDirectory ? 0 : file.Length,
        Modified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
        UnixMode = Mode(file)
    };

    private static FileEntry Entry(Location location, ISftpFile file) => new()
    {
        Location = location,
        Name = file.Name,
        Kind = KindOf(file),
        Size = file.IsDirectory ? 0 : file.Length,
        Modified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
        UnixMode = Mode(file),
        IsHidden = file.Name.StartsWith('.')
    };

    private static FileKind KindOf(ISftpFile file) => file switch
    {
        { IsDirectory: true } => FileKind.Directory,
        { IsSymbolicLink: true } => FileKind.Symlink,
        { IsSocket: true } => FileKind.Socket,
        { IsNamedPipe: true } => FileKind.Fifo,
        { IsBlockDevice: true } => FileKind.BlockDevice,
        { IsCharacterDevice: true } => FileKind.CharDevice,
        { IsRegularFile: true } => FileKind.File,
        _ => FileKind.Unknown
    };

    /// <summary>The POSIX mode bits, rebuilt from the nine flags SSH.NET exposes.</summary>
    /// <remarks>
    /// The library decodes the mode into booleans and does not offer the number back, so it is
    /// reassembled. Worth doing rather than reporting null: SFTP is the one remote protocol
    /// that genuinely has POSIX modes, and a properties dialog showing <c>----------</c> for a
    /// server that knows perfectly well would be a lie rather than an absence.
    /// </remarks>
    private static int Mode(ISftpFile file)
    {
        var mode = 0;
        if (file.OwnerCanRead) mode |= 0x100;
        if (file.OwnerCanWrite) mode |= 0x80;
        if (file.OwnerCanExecute) mode |= 0x40;
        if (file.GroupCanRead) mode |= 0x20;
        if (file.GroupCanWrite) mode |= 0x10;
        if (file.GroupCanExecute) mode |= 0x8;
        if (file.OthersCanRead) mode |= 0x4;
        if (file.OthersCanWrite) mode |= 0x2;
        if (file.OthersCanExecute) mode |= 0x1;
        return mode;
    }

    /// <summary>
    /// Turns an SSH.NET exception into one the rest of the application understands.
    /// </summary>
    /// <remarks>
    /// The isolation invariant: nothing but a <see cref="FileOperationException"/> leaves this
    /// class. A leaked <c>SshException</c> would defeat the retry policy and the conflict
    /// dialog at once, neither of which has ever heard of SSH.NET.
    /// </remarks>
    internal static FileOperationException Classify(Exception ex, Location location) => ex switch
    {
        SftpPathNotFoundException => new(FileErrorKind.NotFound, location, ex.Message, ex),
        SftpPermissionDeniedException => new(FileErrorKind.AccessDenied, location, ex.Message, ex),
        SshAuthenticationException => new(FileErrorKind.AccessDenied, location, ex.Message, ex),
        SshConnectionException => new(FileErrorKind.ConnectionLost, location, ex.Message, ex),
        SshOperationTimeoutException => new(FileErrorKind.Timeout, location, ex.Message, ex),
        // A dead socket surfaces as one of these rather than as an SshException, and reporting
        // it as anything but a connection loss would skip the one silent reconnect.
        System.Net.Sockets.SocketException => new(FileErrorKind.ConnectionLost, location, ex.Message, ex),
        IOException => new(FileErrorKind.ConnectionLost, location, ex.Message, ex),
        ObjectDisposedException => new(FileErrorKind.ConnectionLost, location, ex.Message, ex),
        SshException => new(FileErrorKind.Unknown, location, ex.Message, ex),
        _ => new(FileErrorKind.Unknown, location, ex.Message, ex)
    };

    private async Task DisposeClientAsync()
    {
        if (_client is not { } client)
            return;
        _client = null;
        try
        {
            client.Dispose();
        }
        catch (Exception ex) when (ex is SshException or IOException or ObjectDisposedException)
        {
            // Closing a connection the server has already dropped is the ordinary case.
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await DisposeClientAsync().ConfigureAwait(false);
}
