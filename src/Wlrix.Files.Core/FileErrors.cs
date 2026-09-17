namespace Wlrix.Files.Core;

/// <summary>
/// Why a filesystem operation failed, in terms that mean the same thing on every
/// backend.
/// </summary>
/// <remarks>
/// This taxonomy is what makes the rest of the design possible. The retry policy needs
/// to know whether a failure is worth retrying, the conflict UI needs to recognize
/// <see cref="AlreadyExists"/>, and the error strings need something stable to
/// localize against — and none of that can work against
/// <c>SMBLibrary.NTStatus</c>, an <c>FtpException</c> and an <c>IOException</c> at
/// once. Every backend maps into this and lets nothing else out.
/// </remarks>
public enum FileErrorKind
{
    Unknown,
    NotFound,
    AccessDenied,
    AlreadyExists,
    /// <summary>The target filesystem is full, or a quota was hit.</summary>
    NoSpace,
    /// <summary>A directory was expected to be empty and was not.</summary>
    NotEmpty,
    /// <summary>The source and target are on different filesystems, so a rename cannot work.</summary>
    CrossDevice,
    /// <summary>Not a directory, when one was required (or the reverse).</summary>
    NotADirectory,
    IsADirectory,
    /// <summary>Too many levels of symbolic links.</summary>
    TooManyLinks,
    /// <summary>The name is not usable on the target filesystem.</summary>
    InvalidName,
    /// <summary>The filesystem or the object is read-only.</summary>
    ReadOnly,
    Timeout,
    /// <summary>The connection to a remote dropped. Distinct from <see cref="Timeout"/>: reconnecting may fix it.</summary>
    ConnectionLost,
    /// <summary>Authentication failed or credentials are needed.</summary>
    AuthenticationFailed,
    /// <summary>The operation was cut short and the target may be half-written.</summary>
    Interrupted,
    /// <summary>The backend cannot do this at all, whatever the arguments.</summary>
    Unsupported
}

/// <summary>A filesystem operation that failed, classified.</summary>
/// <remarks>
/// The only exception type an <see cref="IFileSystem"/> is allowed to throw for a
/// failure of the filesystem itself. A backend leaking its own library's exception
/// type defeats the taxonomy, so each one classifies at its boundary and wraps.
/// <see cref="OperationCanceledException"/> is the deliberate exception to the rule:
/// cancellation is not a failure and must stay distinguishable from one.
/// </remarks>
public sealed class FileOperationException : IOException
{
    public FileOperationException(FileErrorKind kind, Location location, string? message = null, Exception? inner = null)
        : base(message ?? DefaultMessage(kind, location), inner)
    {
        Kind = kind;
        Location = location;
    }

    public FileErrorKind Kind { get; }

    /// <summary>The location the operation was working on when it failed.</summary>
    public Location Location { get; }

    /// <summary>
    /// Whether retrying unchanged could plausibly succeed. Transient transport
    /// trouble only — a permission problem will fail identically forever, and
    /// retrying it just delays telling the user.
    /// </summary>
    public bool IsTransient => Kind is FileErrorKind.Timeout or FileErrorKind.ConnectionLost or FileErrorKind.Interrupted;

    // Deliberately unlocalized: these reach a log, and the log is English throughout
    // to match the Rust side. A message shown to the user is built from Kind by the
    // UI, which has the string catalog.
    private static string DefaultMessage(FileErrorKind kind, Location location) => kind switch
    {
        FileErrorKind.NotFound => $"no such file or directory: {location}",
        FileErrorKind.AccessDenied => $"permission denied: {location}",
        FileErrorKind.AlreadyExists => $"already exists: {location}",
        FileErrorKind.NoSpace => $"no space left on device: {location}",
        FileErrorKind.NotEmpty => $"directory not empty: {location}",
        FileErrorKind.CrossDevice => $"different filesystem: {location}",
        FileErrorKind.NotADirectory => $"not a directory: {location}",
        FileErrorKind.IsADirectory => $"is a directory: {location}",
        FileErrorKind.TooManyLinks => $"too many levels of symbolic links: {location}",
        FileErrorKind.InvalidName => $"unusable name: {location}",
        FileErrorKind.ReadOnly => $"read-only: {location}",
        FileErrorKind.Timeout => $"timed out: {location}",
        FileErrorKind.ConnectionLost => $"connection lost: {location}",
        FileErrorKind.AuthenticationFailed => $"authentication failed: {location}",
        FileErrorKind.Interrupted => $"interrupted: {location}",
        FileErrorKind.Unsupported => $"unsupported operation: {location}",
        _ => $"failed: {location}"
    };
}
