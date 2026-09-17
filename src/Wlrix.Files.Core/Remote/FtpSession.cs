using System.Runtime.CompilerServices;
using FluentFTP;
using FluentFTP.Exceptions;

namespace Wlrix.Files.Core.Remote;

/// <summary>FTP, over FluentFTP.</summary>
/// <remarks>
/// The least capable of the three, and the one whose limitations are protocol rather than
/// library. FTP has no notion of ownership or permission bits, its directory listings are
/// server-formatted text that FluentFTP parses heuristically, and its timestamps are
/// frequently minute-resolution — so <see cref="FileSystemCapabilities.PreservesMTime"/> is
/// claimed only because <c>MFMT</c> usually works, and nothing claims POSIX modes.
///
/// <para>
/// A session is one control connection and is stateful in ways the others are not: the working
/// directory and the transfer type live on the server. That is why the pool above gives one
/// connection to one operation at a time, and why a concurrent transfer gets its own.
/// </para>
/// </remarks>
public sealed class FtpSession(string host, int port, ShareCredentials credentials) : IRemoteSession
{
    /// <summary>The port FTP uses when a location does not name one.</summary>
    public const int DefaultPort = 21;

    /// <summary>The account name an anonymous login uses, by long convention.</summary>
    public const string AnonymousUser = "anonymous";

    private AsyncFtpClient? _client;

    /// <inheritdoc/>
    /// <remarks>
    /// No <c>PosixMode</c>: FTP has no mode bits to set, and claiming it would have the
    /// operations engine try to preserve permissions it cannot read. No <c>CaseSensitive</c>
    /// either — that depends on the server's own filesystem and is not knowable from here, and
    /// the conservative answer avoids treating <c>README</c> and <c>readme</c> as two files.
    /// </remarks>
    public FileSystemCapabilities Capabilities =>
        FileSystemCapabilities.Rename
        | FileSystemCapabilities.PreservesMTime;

    /// <inheritdoc/>
    public bool IsConnected => _client is { IsConnected: true };

    /// <inheritdoc/>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await DisposeClientAsync().ConfigureAwait(false);

        // Anonymous FTP is a real account with a conventional name and any password. Sending
        // an email address, as browsers once did, is a privacy leak for no benefit.
        var user = credentials.Anonymous ? AnonymousUser : credentials.Username;
        var password = credentials.Anonymous ? AnonymousUser : credentials.Password;

        var client = new AsyncFtpClient(host, user, password, port < 0 ? DefaultPort : port);
        try
        {
            await client.Connect(cancellationToken).ConfigureAwait(false);
            _client = client;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw Classify(ex, Root);
        }
    }

    /// <inheritdoc/>
    public Task<FileStat> StatAsync(Location location, CancellationToken cancellationToken) =>
        Run(location, async client =>
        {
            var info = await client.GetObjectInfo(location.Path, true, cancellationToken).ConfigureAwait(false);
            if (info is null)
                throw new FileOperationException(FileErrorKind.NotFound, location);
            return new FileStat
            {
                Location = location,
                Kind = KindOf(info),
                Size = info.Type == FtpObjectType.Directory ? 0 : Math.Max(0, info.Size),
                Modified = Timestamp(info.Modified)
            };
        });

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(Location location, CancellationToken cancellationToken) =>
        Run(location, async client =>
            await client.FileExists(location.Path, cancellationToken).ConfigureAwait(false)
            || await client.DirectoryExists(location.Path, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc/>
    public async IAsyncEnumerable<FileEntry> EnumerateAsync(
        Location location, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = Client(location);
        List<FtpListItem> items;
        try
        {
            items = [];
            await foreach (var item in client.GetListingEnumerable(location.Path, cancellationToken).ConfigureAwait(false))
                items.Add(item);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Classify(ex, location);
        }

        foreach (var item in items)
        {
            if (item.Name is "." or "..")
                continue;
            yield return new FileEntry
            {
                Location = location.Child(item.Name),
                Name = item.Name,
                Kind = KindOf(item),
                Size = item.Type == FtpObjectType.Directory ? 0 : Math.Max(0, item.Size),
                Modified = Timestamp(item.Modified),
                IsHidden = item.Name.StartsWith('.')
            };
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Binary, always. FluentFTP's default is ASCII for some transfers, and an ASCII transfer
    /// rewrites line endings — which silently corrupts every file that is not text and changes
    /// the length of every file that is.
    /// </remarks>
    public Task<Stream> OpenReadAsync(Location location, CancellationToken cancellationToken) =>
        Run(location, async client => (Stream)await client
            .OpenRead(location.Path, FtpDataType.Binary, 0, true, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc/>
    public Task<Stream> OpenWriteAsync(Location location, WriteMode mode, long? length, CancellationToken cancellationToken) =>
        Run(location, async client => mode == WriteMode.Append
            ? (Stream)await client.OpenAppend(location.Path, FtpDataType.Binary, true, cancellationToken).ConfigureAwait(false)
            : await client.OpenWrite(location.Path, FtpDataType.Binary, true, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc/>
    public Task CreateDirectoryAsync(Location location, CancellationToken cancellationToken) =>
        Run(location, async client =>
        {
            await client.CreateDirectory(location.Path, true, cancellationToken).ConfigureAwait(false);
            return true;
        });

    /// <inheritdoc/>
    public Task DeleteAsync(Location location, bool recursive, CancellationToken cancellationToken) =>
        Run(location, async client =>
        {
            if (await client.FileExists(location.Path, cancellationToken).ConfigureAwait(false))
            {
                await client.DeleteFile(location.Path, cancellationToken).ConfigureAwait(false);
                return true;
            }
            if (!await client.DirectoryExists(location.Path, cancellationToken).ConfigureAwait(false))
                throw new FileOperationException(FileErrorKind.NotFound, location);
            if (!recursive && await HasContentsAsync(client, location, cancellationToken).ConfigureAwait(false))
                throw new FileOperationException(FileErrorKind.NotEmpty, location);

            // FluentFTP's DeleteDirectory recurses on its own, which is what a non-recursive
            // caller must not get -- hence the emptiness check above rather than a flag.
            await client.DeleteDirectory(location.Path, cancellationToken).ConfigureAwait(false);
            return true;
        });

    private static async Task<bool> HasContentsAsync(AsyncFtpClient client, Location location, CancellationToken cancellationToken)
    {
        await foreach (var item in client.GetListingEnumerable(location.Path, cancellationToken).ConfigureAwait(false))
        {
            if (item.Name is not ("." or ".."))
                return true;
        }
        return false;
    }

    /// <inheritdoc/>
    public Task RenameAsync(Location from, Location to, CancellationToken cancellationToken) =>
        Run(from, async client =>
        {
            await client.Rename(from.Path, to.Path, cancellationToken).ConfigureAwait(false);
            return true;
        });

    /// <inheritdoc/>
    public Task SetModifiedAsync(Location location, DateTimeOffset modified, CancellationToken cancellationToken) =>
        Run(location, async client =>
        {
            await client.SetModifiedTime(location.Path, modified.UtcDateTime, cancellationToken).ConfigureAwait(false);
            return true;
        });

    /// <inheritdoc/>
    public Task SetUnixModeAsync(Location location, int mode, CancellationToken cancellationToken) =>
        throw new FileOperationException(FileErrorKind.Unsupported, location, "FTP has no mode bits");

    /// <inheritdoc/>
    /// <remarks>Nothing in the protocol reports it, and no capability claims otherwise.</remarks>
    public Task<FreeSpace?> GetFreeSpaceAsync(Location location, CancellationToken cancellationToken) =>
        Task.FromResult<FreeSpace?>(null);

    private static Location Root => Location.Parse("/");

    /// <summary>A listing timestamp, or null when the server did not give one.</summary>
    /// <remarks>
    /// FluentFTP reports "unknown" as <c>DateTime.MinValue</c>, and passing that through would
    /// have every file on such a server sorted as if it were written in the year one.
    /// </remarks>
    private static DateTimeOffset? Timestamp(DateTime value) =>
        value == DateTime.MinValue ? null : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static FileKind KindOf(FtpListItem item) => item.Type switch
    {
        FtpObjectType.Directory => FileKind.Directory,
        FtpObjectType.Link => FileKind.Symlink,
        _ => FileKind.File
    };

    private AsyncFtpClient Client(Location location) =>
        _client is { IsConnected: true } client
            ? client
            : throw new FileOperationException(FileErrorKind.ConnectionLost, location, "not connected");

    private async Task<T> Run<T>(Location location, Func<AsyncFtpClient, Task<T>> work)
    {
        var client = Client(location);
        try
        {
            return await work(client).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not FileOperationException)
        {
            throw Classify(ex, location);
        }
    }

    /// <summary>
    /// Turns a FluentFTP exception into one the rest of the application understands.
    /// </summary>
    /// <remarks>
    /// FTP's reply codes carry the real answer and the exception type mostly does not, so this
    /// reads the code first: 550 alone covers "no such file", "permission denied" and "not a
    /// directory" depending on the server's mood, and 552 is the only reliable "disk full".
    /// </remarks>
    internal static FileOperationException Classify(Exception ex, Location location)
    {
        if (ex is FtpAuthenticationException auth)
            return new FileOperationException(FileErrorKind.AccessDenied, location, auth.Message, auth);
        if (ex is FtpCommandException command)
            return new FileOperationException(FromCode(command.CompletionCode, command.Message), location, command.Message, command);

        return ex switch
        {
            FtpSecurityNotAvailableException => new(FileErrorKind.AccessDenied, location, ex.Message, ex),
            System.Net.Sockets.SocketException => new(FileErrorKind.ConnectionLost, location, ex.Message, ex),
            TimeoutException => new(FileErrorKind.Timeout, location, ex.Message, ex),
            IOException => new(FileErrorKind.ConnectionLost, location, ex.Message, ex),
            ObjectDisposedException => new(FileErrorKind.ConnectionLost, location, ex.Message, ex),
            FtpException => new(FileErrorKind.Unknown, location, ex.Message, ex),
            _ => new(FileErrorKind.Unknown, location, ex.Message, ex)
        };
    }

    /// <summary>What an FTP reply code means, as far as one can be trusted to mean anything.</summary>
    internal static FileErrorKind FromCode(string? code, string message) => code switch
    {
        "421" => FileErrorKind.ConnectionLost,
        "425" or "426" => FileErrorKind.ConnectionLost,
        "450" or "451" => FileErrorKind.Interrupted,
        "452" or "552" => FileErrorKind.NoSpace,
        "530" or "532" => FileErrorKind.AccessDenied,
        // The catch-all failure. Which one it is has to come out of the text, and servers do
        // at least agree on the word: "permission" or "denied" for the one, and everything
        // else is far more often a path that is not there.
        "550" => message.Contains("permission", StringComparison.OrdinalIgnoreCase)
                 || message.Contains("denied", StringComparison.OrdinalIgnoreCase)
            ? FileErrorKind.AccessDenied
            : FileErrorKind.NotFound,
        "553" => FileErrorKind.AccessDenied,
        _ => FileErrorKind.Unknown
    };

    private async Task DisposeClientAsync()
    {
        if (_client is not { } client)
            return;
        _client = null;
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FtpException or IOException or ObjectDisposedException
                                       or System.Net.Sockets.SocketException)
        {
            // Closing a connection the server has already dropped is the ordinary case.
        }
    }

    public async ValueTask DisposeAsync() => await DisposeClientAsync().ConfigureAwait(false);
}
