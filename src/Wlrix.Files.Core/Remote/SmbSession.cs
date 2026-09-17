using System.Runtime.CompilerServices;
using SMBLibrary;
using SMBLibrary.Client;
using FileAttributes = SMBLibrary.FileAttributes;

namespace Wlrix.Files.Core.Remote;

/// <summary>SMB2/3, over SMBLibrary.</summary>
/// <remarks>
/// The lowest-level of the three backends by a wide margin: SMBLibrary hands back
/// <see cref="NTStatus"/> codes rather than throwing, works in explicit file handles, and is
/// entirely synchronous — so every call here is wrapped, checked and pushed onto the thread
/// pool. That is the price of the only permissively-licensed SMB implementation for .NET.
///
/// <para>
/// <b>SMB2 only, never SMB1.</b> <c>SMB2Client</c> is chosen over <c>SMB1Client</c>
/// deliberately: SMB1 is unauthenticated-by-default, has been the vehicle for two worms, and
/// is disabled on every server worth connecting to.
/// </para>
///
/// <para>
/// <b>Port 445 only.</b> <c>SMB2Client.DirectTCPPort</c> is a readonly constant in the library
/// and there is no way past it short of reflection, so a location naming another port cannot
/// be honored — and saying so is better than connecting somewhere the user did not ask for.
/// </para>
/// </remarks>
public sealed class SmbSession(string host, int port, ShareCredentials credentials) : IRemoteSession
{
    /// <summary>The only port SMBLibrary's client will use.</summary>
    public const int DirectTcpPort = 445;

    private readonly Dictionary<string, ISMBFileStore> _trees = new(StringComparer.OrdinalIgnoreCase);
    private SMB2Client? _client;

    /// <inheritdoc/>
    /// <remarks>
    /// No <c>PosixMode</c>: the server has ACLs rather than mode bits, and inventing nine of
    /// them from a read-only attribute would be a fiction the properties dialog then shows.
    /// Not case-sensitive either, which is the safe answer for a protocol whose servers are
    /// usually not.
    /// </remarks>
    public FileSystemCapabilities Capabilities =>
        FileSystemCapabilities.Rename
        | FileSystemCapabilities.RandomWriteAccess
        | FileSystemCapabilities.PreservesMTime
        | FileSystemCapabilities.ReportsFreeSpace;

    /// <inheritdoc/>
    public bool IsConnected => _client is { IsConnected: true };

    /// <inheritdoc/>
    public Task ConnectAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (port >= 0 && port != DirectTcpPort)
        {
            throw new FileOperationException(FileErrorKind.Unsupported, Root,
                $"SMB is only reachable on port {DirectTcpPort}");
        }

        Close();
        var client = new SMB2Client();
        // The address overload when the host is already one. SMBLibrary's string overload
        // always calls Dns.GetHostAddresses, so a literal address costs a name lookup that
        // can only fail -- and on a machine whose resolver is unhappy, it does.
        var reached = System.Net.IPAddress.TryParse(host, out var address)
            ? client.Connect(address, SMBTransportType.DirectTCPTransport)
            : client.Connect(host, SMBTransportType.DirectTCPTransport);
        if (!reached)
        {
            throw new FileOperationException(FileErrorKind.ConnectionLost, Root,
                $"could not reach {host} on port {DirectTcpPort}");
        }

        // Guest is an empty user and an empty password. A server with guest access disabled
        // answers LOGON_FAILURE, which is the honest thing to report.
        var status = credentials.Anonymous
            ? client.Login(string.Empty, string.Empty, string.Empty)
            : client.Login(Domain, User, credentials.Password);

        if (status != NTStatus.STATUS_SUCCESS)
        {
            client.Disconnect();
            throw Failed(status, Root, "login");
        }
        _client = client;
    }, cancellationToken);

    /// <summary>The domain half of a <c>DOMAIN\user</c> username, if there is one.</summary>
    private string Domain =>
        credentials.Username.Split('\\') is [var domain, _] ? domain : string.Empty;

    private string User =>
        credentials.Username.Split('\\') is [_, var user] ? user : credentials.Username;

    /// <inheritdoc/>
    public Task<FileStat> StatAsync(Location location, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var (store, path) = Resolve(location);
            // A share root has no parent to list it from, so it is described rather than
            // queried: every share is a directory and nothing else is knowable about it.
            if (path.Length == 0)
                return new FileStat { Location = location, Kind = FileKind.Directory };

            var handle = Open(store, path, location, AccessMask.GENERIC_READ,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_OPEN_REPARSE_POINT);
            try
            {
                Check(store.GetFileInformation(out var info, handle, FileInformationClass.FileBasicInformation),
                    location, "stat");
                Check(store.GetFileInformation(out var standard, handle, FileInformationClass.FileStandardInformation),
                    location, "stat");
                var basic = (FileBasicInformation)info;
                var size = (FileStandardInformation)standard;
                return new FileStat
                {
                    Location = location,
                    Kind = basic.FileAttributes.HasFlag(FileAttributes.Directory) ? FileKind.Directory : FileKind.File,
                    Size = basic.FileAttributes.HasFlag(FileAttributes.Directory) ? 0 : size.EndOfFile,
                    Modified = Timestamp(basic.LastWriteTime.Time)
                };
            }
            finally
            {
                store.CloseFile(handle);
            }
        }, cancellationToken);

    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(Location location, CancellationToken cancellationToken)
    {
        try
        {
            await StatAsync(location, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (FileOperationException ex) when (ex.Kind == FileErrorKind.NotFound)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<FileEntry> EnumerateAsync(
        Location location, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var entries = await Task.Run(() =>
        {
            var (store, path) = Resolve(location);
            var handle = Open(store, path, location, AccessMask.GENERIC_READ,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE);
            try
            {
                CheckListing(store.QueryDirectory(out var found, handle, "*",
                    FileInformationClass.FileDirectoryInformation), location);
                var read = new List<FileEntry>(found.Count);
                foreach (var item in found.OfType<FileDirectoryInformation>())
                {
                    if (item.FileName is "." or "..")
                        continue;
                    var directory = item.FileAttributes.HasFlag(FileAttributes.Directory);
                    read.Add(new FileEntry
                    {
                        Location = location.Child(item.FileName),
                        Name = item.FileName,
                        Kind = directory ? FileKind.Directory : FileKind.File,
                        Size = directory ? 0 : item.EndOfFile,
                        Modified = Timestamp(item.LastWriteTime),
                        // Windows hides by attribute, Unix by leading dot, and a share is
                        // very often serving one to the other. Both count.
                        IsHidden = item.FileAttributes.HasFlag(FileAttributes.Hidden)
                                   || item.FileName.StartsWith('.')
                    });
                }
                return read;
            }
            finally
            {
                store.CloseFile(handle);
            }
        }, cancellationToken).ConfigureAwait(false);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }
    }

    /// <inheritdoc/>
    public Task<Stream> OpenReadAsync(Location location, CancellationToken cancellationToken) =>
        Task.Run<Stream>(() =>
        {
            var (store, path) = Resolve(location);
            var handle = Open(store, path, location, AccessMask.GENERIC_READ,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE);
            Check(store.GetFileInformation(out var info, handle, FileInformationClass.FileStandardInformation),
                location, "stat");
            return new SmbStream(store, handle, location, ((FileStandardInformation)info).EndOfFile, Client(location).MaxReadSize);
        }, cancellationToken);

    /// <inheritdoc/>
    public Task<Stream> OpenWriteAsync(Location location, WriteMode mode, long? length, CancellationToken cancellationToken) =>
        Task.Run<Stream>(() =>
        {
            var (store, path) = Resolve(location);
            var disposition = mode == WriteMode.Append ? CreateDisposition.FILE_OPEN_IF : CreateDisposition.FILE_OVERWRITE_IF;
            var handle = Open(store, path, location, AccessMask.GENERIC_WRITE | AccessMask.GENERIC_READ,
                disposition, CreateOptions.FILE_NON_DIRECTORY_FILE);

            var start = 0L;
            if (mode == WriteMode.Append
                && store.GetFileInformation(out var info, handle, FileInformationClass.FileStandardInformation)
                    == NTStatus.STATUS_SUCCESS)
            {
                start = ((FileStandardInformation)info).EndOfFile;
            }
            return new SmbStream(store, handle, location, start, Client(location).MaxWriteSize) { Position = start };
        }, cancellationToken);

    /// <inheritdoc/>
    public Task CreateDirectoryAsync(Location location, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var (store, path) = Resolve(location);
            var handle = Open(store, path, location, AccessMask.GENERIC_WRITE,
                CreateDisposition.FILE_CREATE, CreateOptions.FILE_DIRECTORY_FILE);
            store.CloseFile(handle);
        }, cancellationToken);

    /// <inheritdoc/>
    public Task DeleteAsync(Location location, bool recursive, CancellationToken cancellationToken) =>
        Task.Run(() => Delete(location, recursive), cancellationToken);

    private void Delete(Location location, bool recursive)
    {
        var (store, path) = Resolve(location);
        var isDirectory = false;
        var probe = Open(store, path, location, AccessMask.GENERIC_READ,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_OPEN_REPARSE_POINT);
        try
        {
            if (store.GetFileInformation(out var info, probe, FileInformationClass.FileBasicInformation)
                == NTStatus.STATUS_SUCCESS)
            {
                isDirectory = ((FileBasicInformation)info).FileAttributes.HasFlag(FileAttributes.Directory);
            }
        }
        finally
        {
            store.CloseFile(probe);
        }

        // SMB deletes an empty directory only, so the contents are walked first. The engine
        // usually does that itself, but a directory dropped whole arrives as one call.
        if (isDirectory && recursive)
        {
            foreach (var child in ChildrenOf(store, location))
                Delete(child, true);
        }

        var options = isDirectory ? CreateOptions.FILE_DIRECTORY_FILE : CreateOptions.FILE_NON_DIRECTORY_FILE;
        var handle = Open(store, path, location, AccessMask.DELETE,
            CreateDisposition.FILE_OPEN, options | CreateOptions.FILE_DELETE_ON_CLOSE);
        try
        {
            // The deletion is requested by flag and performed by the close, so a failure to
            // set it has to be checked here rather than discovered by the file still existing.
            Check(store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true }),
                location, "delete");
        }
        finally
        {
            store.CloseFile(handle);
        }
    }

    private static List<Location> ChildrenOf(ISMBFileStore store, Location directory)
    {
        var (_, path) = Split(directory);
        var handle = Open(store, path, directory, AccessMask.GENERIC_READ,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE);
        try
        {
            CheckListing(store.QueryDirectory(out var found, handle, "*",
                FileInformationClass.FileDirectoryInformation), directory);
            return [.. found.OfType<FileDirectoryInformation>()
                .Where(item => item.FileName is not ("." or ".."))
                .Select(item => directory.Child(item.FileName))];
        }
        finally
        {
            store.CloseFile(handle);
        }
    }

    /// <inheritdoc/>
    public Task RenameAsync(Location from, Location to, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var (store, path) = Resolve(from);
            var (_, target) = Split(to);
            if (!string.Equals(ShareOf(from), ShareOf(to), StringComparison.OrdinalIgnoreCase))
            {
                // Two shares are two tree connections, and the server will not rename across
                // them. Reported as cross-device so the engine falls back to copy-and-delete.
                throw new FileOperationException(FileErrorKind.CrossDevice, to, "a rename cannot cross shares");
            }

            var handle = Open(store, path, from, AccessMask.GENERIC_WRITE | AccessMask.DELETE,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_OPEN_REPARSE_POINT);
            try
            {
                Check(store.SetFileInformation(handle,
                    new FileRenameInformationType2 { FileName = target, ReplaceIfExists = false }), to, "rename");
            }
            finally
            {
                store.CloseFile(handle);
            }
        }, cancellationToken);

    /// <inheritdoc/>
    public Task SetModifiedAsync(Location location, DateTimeOffset modified, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var (store, path) = Resolve(location);
            var handle = Open(store, path, location, AccessMask.GENERIC_WRITE,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_OPEN_REPARSE_POINT);
            try
            {
                Check(store.SetFileInformation(handle, new FileBasicInformation
                {
                    LastWriteTime = new SetFileTime { Time = modified.UtcDateTime }
                }), location, "set time");
            }
            finally
            {
                store.CloseFile(handle);
            }
        }, cancellationToken);

    /// <inheritdoc/>
    public Task SetUnixModeAsync(Location location, int mode, CancellationToken cancellationToken) =>
        throw new FileOperationException(FileErrorKind.Unsupported, location, "SMB has no mode bits");

    /// <inheritdoc/>
    public Task<FreeSpace?> GetFreeSpaceAsync(Location location, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var (store, _) = Resolve(location);
            if (store.GetFileSystemInformation(out var info, FileSystemInformationClass.FileFsSizeInformation)
                != NTStatus.STATUS_SUCCESS || info is not FileFsSizeInformation size)
            {
                return (FreeSpace?)null;
            }

            var unit = (long)size.SectorsPerAllocationUnit * size.BytesPerSector;
            return new FreeSpace(size.TotalAllocationUnits * unit, size.AvailableAllocationUnits * unit);
        }, cancellationToken);

    private static Location Root => Location.Parse("/");

    private SMB2Client Client(Location location) =>
        _client is { IsConnected: true } client
            ? client
            : throw new FileOperationException(FileErrorKind.ConnectionLost, location, "not connected");

    /// <summary>The share a location is on: the first component of its path.</summary>
    private static string ShareOf(Location location) =>
        location.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

    /// <summary>Splits a location into its share and the server-relative path within it.</summary>
    /// <remarks>
    /// SMB paths are backslash-separated, have no leading separator, and do not include the
    /// share — which is a tree connection rather than a directory.
    /// </remarks>
    private static (string Share, string Path) Split(Location location)
    {
        var parts = location.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0
            ? (string.Empty, string.Empty)
            : (parts[0], string.Join('\\', parts.Skip(1)));
    }

    /// <summary>The tree connection for a location's share, opening one if needed.</summary>
    /// <remarks>
    /// Cached per share: a tree connect is a round trip, and a window browsing one share would
    /// otherwise pay for one on every operation.
    /// </remarks>
    private (ISMBFileStore Store, string Path) Resolve(Location location)
    {
        var (share, path) = Split(location);
        if (share.Length == 0)
            throw new FileOperationException(FileErrorKind.NotFound, location, "no share named");

        if (_trees.TryGetValue(share, out var existing))
            return (existing, path);

        var store = Client(location).TreeConnect(share, out var status);
        if (status != NTStatus.STATUS_SUCCESS || store is null)
            throw Failed(status, location, $"connect to \\\\{host}\\{share}");

        _trees[share] = store;
        return (store, path);
    }

    private static object Open(
        ISMBFileStore store, string path, Location location,
        AccessMask access, CreateDisposition disposition, CreateOptions options)
    {
        var status = store.CreateFile(out var handle, out _, path, access,
            FileAttributes.Normal, ShareAccess.Read | ShareAccess.Write, disposition, options, null);
        if (status != NTStatus.STATUS_SUCCESS)
            throw Failed(status, location, "open");
        return handle;
    }

    private static void Check(NTStatus status, Location location, string what)
    {
        if (status != NTStatus.STATUS_SUCCESS)
            throw Failed(status, location, what);
    }

    /// <summary>
    /// Checks a directory enumeration, which does not end in <c>STATUS_SUCCESS</c>.
    /// </summary>
    /// <remarks>
    /// SMBLibrary's <c>QueryDirectory</c> asks repeatedly until the server says there is
    /// nothing more, and returns <i>that</i> status — so a listing that worked perfectly ends
    /// in <see cref="NTStatus.STATUS_NO_MORE_FILES"/> with every entry sitting in the result.
    /// Treating it as an error, which "anything but SUCCESS is a failure" does, throws away a
    /// complete listing and reports a working share as broken. Confirmed against a real
    /// server: twelve entries returned alongside STATUS_NO_MORE_FILES.
    ///
    /// <para>
    /// <c>STATUS_NO_SUCH_FILE</c> is the same answer from servers that use it for "the
    /// wildcard matched nothing", which is what an empty directory looks like to them.
    /// </para>
    /// </remarks>
    private static void CheckListing(NTStatus status, Location location)
    {
        if (!IsListingComplete(status))
            throw Failed(status, location, "list");
    }

    /// <summary>Whether an enumeration status means "that is all of them".</summary>
    internal static bool IsListingComplete(NTStatus status) =>
        status is NTStatus.STATUS_SUCCESS
            or NTStatus.STATUS_NO_MORE_FILES
            or NTStatus.STATUS_NO_SUCH_FILE;

    private static DateTimeOffset? Timestamp(DateTime? time) =>
        time is { } value && value != DateTime.MinValue
            ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
            : null;

    internal static FileOperationException Failed(NTStatus status, Location location, string what) =>
        new(Classify(status), location, $"{what} failed: {status}");

    /// <summary>
    /// Turns an <see cref="NTStatus"/> into something the rest of the application understands.
    /// </summary>
    /// <remarks>
    /// SMBLibrary reports rather than throws, so this is the whole of the isolation invariant
    /// for this backend: an unmapped status becomes <see cref="FileErrorKind.Unknown"/> and
    /// carries its own name in the message, which is what makes the next unmapped one easy to
    /// find and add.
    /// </remarks>
    internal static FileErrorKind Classify(NTStatus status) => status switch
    {
        NTStatus.STATUS_NO_SUCH_FILE or NTStatus.STATUS_OBJECT_NAME_NOT_FOUND
            or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND => FileErrorKind.NotFound,
        NTStatus.STATUS_ACCESS_DENIED or NTStatus.STATUS_LOGON_FAILURE
            or NTStatus.STATUS_ACCOUNT_DISABLED or NTStatus.STATUS_PASSWORD_EXPIRED
            or NTStatus.STATUS_SHARING_VIOLATION => FileErrorKind.AccessDenied,
        NTStatus.STATUS_OBJECT_NAME_COLLISION => FileErrorKind.AlreadyExists,
        NTStatus.STATUS_DIRECTORY_NOT_EMPTY => FileErrorKind.NotEmpty,
        NTStatus.STATUS_DISK_FULL => FileErrorKind.NoSpace,
        NTStatus.STATUS_NOT_A_DIRECTORY => FileErrorKind.NotADirectory,
        NTStatus.STATUS_FILE_IS_A_DIRECTORY => FileErrorKind.IsADirectory,
        NTStatus.STATUS_IO_TIMEOUT => FileErrorKind.Timeout,
        // The library has no generic "socket died" status; a torn-down session or share
        // surfaces as one of these, and an invalid handle is what a reconnected client sees
        // when it reuses one from before.
        NTStatus.STATUS_USER_SESSION_DELETED or NTStatus.STATUS_NETWORK_NAME_DELETED
            or NTStatus.STATUS_INVALID_HANDLE => FileErrorKind.ConnectionLost,
        // A share that is not there, rather than a network fault, despite the name.
        NTStatus.STATUS_BAD_NETWORK_NAME => FileErrorKind.NotFound,
        NTStatus.STATUS_NOT_SUPPORTED or NTStatus.STATUS_NOT_IMPLEMENTED => FileErrorKind.Unsupported,
        NTStatus.STATUS_CANCELLED => FileErrorKind.Interrupted,
        _ => FileErrorKind.Unknown
    };

    private void Close()
    {
        foreach (var store in _trees.Values)
        {
            try
            {
                store.Disconnect();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException
                                           or System.Net.Sockets.SocketException)
            {
                // Disconnecting a tree the server has already torn down is the ordinary case.
            }
        }
        _trees.Clear();

        if (_client is not { } client)
            return;
        _client = null;
        try
        {
            client.Logoff();
            client.Disconnect();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException
                                       or System.Net.Sockets.SocketException)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A <see cref="Stream"/> over one SMB file handle.</summary>
/// <remarks>
/// SMBLibrary works in explicit reads and writes at an offset with a server-declared maximum
/// per request, so this is the adapter between that and the <see cref="Stream"/> the rest of
/// the application copies bytes through. Chunking to <c>MaxReadSize</c> is not an
/// optimization: a request larger than the server negotiated is refused outright.
/// </remarks>
internal sealed class SmbStream(
    ISMBFileStore store, object handle, Location location, long length, uint chunk) : Stream
{
    private bool _closed;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => length;
    public override long Position { get; set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var want = (int)Math.Min(count, chunk);
        var status = store.ReadFile(out var data, handle, Position, want);
        // End of file is reported as a status rather than a short read, and it is not an error.
        if (status == NTStatus.STATUS_END_OF_FILE)
            return 0;
        if (status != NTStatus.STATUS_SUCCESS)
            throw SmbSession.Failed(status, location, "read");
        if (data is null || data.Length == 0)
            return 0;

        data.CopyTo(buffer.AsSpan(offset));
        Position += data.Length;
        return data.Length;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        var written = 0;
        while (written < count)
        {
            // One request per chunk. A single Write of a hundred megabytes would otherwise be
            // one SMB request the server rejects.
            var size = (int)Math.Min(count - written, chunk);
            var slice = buffer.AsSpan(offset + written, size).ToArray();
            var status = store.WriteFile(out var accepted, handle, Position, slice);
            if (status != NTStatus.STATUS_SUCCESS)
                throw SmbSession.Failed(status, location, "write");
            if (accepted <= 0)
                throw new FileOperationException(FileErrorKind.NoSpace, location, "the server accepted no bytes");

            Position += accepted;
            written += accepted;
            length = Math.Max(length, Position);
        }
    }

    public override void Flush() => store.FlushFileBuffers(handle);

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => length + offset
        };
        return Position;
    }

    public override void SetLength(long value)
    {
        var status = store.SetFileInformation(handle, new FileEndOfFileInformation { EndOfFile = value });
        if (status != NTStatus.STATUS_SUCCESS)
            throw SmbSession.Failed(status, location, "set length");
        length = value;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closed)
        {
            _closed = true;
            store.CloseFile(handle);
        }
        base.Dispose(disposing);
    }
}
