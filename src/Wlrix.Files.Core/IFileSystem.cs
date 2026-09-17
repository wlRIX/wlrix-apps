namespace Wlrix.Files.Core;

/// <summary>What a backend can actually do.</summary>
/// <remarks>
/// The operations engine branches on these and never on the concrete type, so that
/// adding a backend does not mean revisiting every <c>if (fs is LocalFileSystem)</c>.
/// Every absent capability has a fallback: no <see cref="Rename"/> means copy and
/// delete, no <see cref="Trash"/> means ask before deleting outright.
/// </remarks>
[Flags]
public enum FileSystemCapabilities
{
    None = 0,
    /// <summary>Can rename or move within itself without copying the bytes.</summary>
    Rename = 1 << 0,
    HardLink = 1 << 1,
    SymLink = 1 << 2,
    /// <summary>Reports and accepts POSIX mode bits.</summary>
    PosixMode = 1 << 3,
    /// <summary>Has somewhere recoverable to put deleted files.</summary>
    Trash = 1 << 4,
    /// <summary>Can report changes without being polled.</summary>
    Watch = 1 << 5,
    /// <summary>Supports seeking and writing mid-stream, so a part file can resume.</summary>
    RandomWriteAccess = 1 << 6,
    /// <summary>Can be told a file's modification time, so a copy keeps it.</summary>
    PreservesMTime = 1 << 7,
    /// <summary>Reports total and free space.</summary>
    ReportsFreeSpace = 1 << 8,
    /// <summary>Distinguishes names differing only in case.</summary>
    CaseSensitive = 1 << 9
}

/// <summary>How to open something for writing.</summary>
public enum WriteMode
{
    /// <summary>Create, and fail with <see cref="FileErrorKind.AlreadyExists"/> if it is there.</summary>
    CreateNew,
    /// <summary>Create or truncate.</summary>
    Overwrite,
    /// <summary>Open at the end, creating if needed. Needs <see cref="FileSystemCapabilities.RandomWriteAccess"/>.</summary>
    Append
}

/// <summary>How much room a filesystem has.</summary>
public readonly record struct FreeSpace(long TotalBytes, long AvailableBytes);

/// <summary>
/// One filesystem, local or remote, behind a single interface.
/// </summary>
/// <remarks>
/// Exactly one instance per <see cref="Location.MountKey"/>, handed out by
/// <c>FileSystemProvider</c>, because a remote instance owns a connection and a second
/// one would open a redundant session.
///
/// <para>
/// <b>Every implementation must classify its own failures.</b> Nothing may escape but
/// <see cref="FileOperationException"/> and
/// <see cref="OperationCanceledException"/> — a leaked <c>FtpException</c> or
/// <c>NTStatus</c> would defeat the retry policy and the conflict UI at once.
/// </para>
///
/// <para>
/// Remote implementations are not safe for concurrent use on one connection, so they
/// serialize internally rather than asking every caller to remember.
/// </para>
/// </remarks>
public interface IFileSystem : IAsyncDisposable
{
    /// <summary>Which mount this serves. Matches <see cref="Location.MountKey"/>.</summary>
    string MountKey { get; }

    /// <summary>What this backend can do. Constant for the life of the instance.</summary>
    FileSystemCapabilities Capabilities { get; }

    Task<FileStat> StatAsync(Location location, CancellationToken cancellationToken);

    /// <summary>Whether something exists there, without throwing if it does not.</summary>
    Task<bool> ExistsAsync(Location location, CancellationToken cancellationToken);

    /// <summary>
    /// The entries of a directory, streamed.
    /// </summary>
    /// <remarks>
    /// An async stream rather than a list because the first screenful must be
    /// displayable long before the last entry arrives. Order is whatever the
    /// filesystem gives; sorting is the caller's job and happens off the UI thread.
    /// </remarks>
    IAsyncEnumerable<FileEntry> EnumerateAsync(Location location, CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(Location location, CancellationToken cancellationToken);

    /// <summary>Opens a file for writing.</summary>
    /// <param name="length">
    /// The expected final size when known. Some protocols want it up front; the rest
    /// ignore it.
    /// </param>
    Task<Stream> OpenWriteAsync(Location location, WriteMode mode, long? length, CancellationToken cancellationToken);

    /// <summary>Creates a directory, and any missing parents.</summary>
    Task CreateDirectoryAsync(Location location, CancellationToken cancellationToken);

    /// <summary>Deletes permanently.</summary>
    /// <param name="recursive">
    /// Required for a non-empty directory; without it one fails with
    /// <see cref="FileErrorKind.NotEmpty"/>.
    /// </param>
    Task DeleteAsync(Location location, bool recursive, CancellationToken cancellationToken);

    /// <summary>
    /// Renames or moves within this filesystem, without copying the bytes.
    /// </summary>
    /// <remarks>
    /// Both locations must share this instance's <see cref="MountKey"/>. Across mounts
    /// there is nothing to do but copy and delete, and the operations engine decides
    /// that before calling — rather than attempting a rename and catching the failure,
    /// which on some backends is indistinguishable from a real error.
    /// </remarks>
    Task RenameAsync(Location from, Location to, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a symbolic link at <paramref name="link"/> pointing at <paramref name="target"/>.
    /// </summary>
    /// <param name="target">
    /// The link's contents, written verbatim. A path, not a <see cref="Location"/>, because
    /// that is what a symlink holds — possibly relative, possibly pointing at nothing yet,
    /// and neither of those is expressible as a location.
    /// </param>
    /// <remarks>
    /// Needs <see cref="FileSystemCapabilities.SymLink"/>. There is no fallback: a backend
    /// that cannot make links must refuse rather than silently copying the file, which would
    /// leave the user with a second copy where they asked for a reference to the first.
    /// </remarks>
    Task CreateSymlinkAsync(Location link, string target, CancellationToken cancellationToken);

    /// <summary>Sets the modification time. Needs <see cref="FileSystemCapabilities.PreservesMTime"/>.</summary>
    Task SetModifiedAsync(Location location, DateTimeOffset modified, CancellationToken cancellationToken);

    /// <summary>Sets POSIX mode bits. Needs <see cref="FileSystemCapabilities.PosixMode"/>.</summary>
    Task SetUnixModeAsync(Location location, int mode, CancellationToken cancellationToken);

    /// <summary>
    /// Total and available space, or null when the backend cannot say.
    /// </summary>
    Task<FreeSpace?> GetFreeSpaceAsync(Location location, CancellationToken cancellationToken);
}
