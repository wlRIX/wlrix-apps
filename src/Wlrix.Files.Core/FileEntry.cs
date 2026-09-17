namespace Wlrix.Files.Core;

/// <summary>What a directory entry fundamentally is, as the filesystem reports it.</summary>
public enum FileKind
{
    /// <summary>Anything the filesystem does not distinguish further.</summary>
    File,
    Directory,
    /// <summary>A symlink whose target could not be resolved, or was not followed.</summary>
    Symlink,
    Fifo,
    Socket,
    BlockDevice,
    CharDevice,
    Unknown
}

/// <summary>One entry in a directory listing.</summary>
/// <remarks>
/// A sealed class rather than a record struct, and deliberately: this is wide enough
/// that at a hundred thousand entries the cost of copying it around exceeds the cost of
/// allocating it once. It is also immutable, so a listing can be handed to a background
/// sort without a snapshot.
///
/// <para>
/// Note what is absent. There is no icon, no MIME type and no thumbnail here, because
/// nothing that requires touching a file's *contents* may happen during enumeration —
/// that is what keeps a large directory fast. Those are resolved later, per visible row.
/// </para>
/// </remarks>
public sealed class FileEntry
{
    public required Location Location { get; init; }

    /// <summary>The file name, without any path.</summary>
    public required string Name { get; init; }

    public required FileKind Kind { get; init; }

    /// <summary>Size in bytes. Meaningless for directories, which report 0.</summary>
    public long Size { get; init; }

    public DateTimeOffset? Modified { get; init; }

    /// <summary>The POSIX mode bits, when the filesystem has them.</summary>
    /// <remarks>
    /// Null rather than zero on filesystems that do not: SMB and FTP have no mode to
    /// report, and rendering that as <c>----------</c> would be a lie rather than an
    /// absence.
    /// </remarks>
    public int? UnixMode { get; init; }

    /// <summary>Where a symlink points, unresolved, or null if this is not one.</summary>
    public string? SymlinkTarget { get; init; }

    /// <summary>
    /// True for a dotfile. A property rather than a computation at the call site so
    /// that "what counts as hidden" stays in one place — a <c>.hidden</c> file, which
    /// some file managers honor, would go here.
    /// </summary>
    public bool IsHidden { get; init; }

    /// <summary>True if this is a directory, or a symlink that resolves to one.</summary>
    public bool IsDirectory => Kind == FileKind.Directory;
}

/// <summary>What a <c>stat</c> of one location returned.</summary>
public sealed class FileStat
{
    public required Location Location { get; init; }
    public required FileKind Kind { get; init; }
    public long Size { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public int? UnixMode { get; init; }
    public string? SymlinkTarget { get; init; }

    public bool IsDirectory => Kind == FileKind.Directory;
}
