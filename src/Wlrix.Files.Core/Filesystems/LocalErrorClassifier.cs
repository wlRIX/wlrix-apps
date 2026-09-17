using System.Runtime.InteropServices;

namespace Wlrix.Files.Core.Filesystems;

/// <summary>Turns a <c>System.IO</c> exception into a <see cref="FileOperationException"/>.</summary>
/// <remarks>
/// .NET's IO exception hierarchy is only coarsely typed — most of what matters arrives
/// as a plain <see cref="IOException"/> distinguished by its errno — so the errno is
/// the primary signal and the exception type is the fallback.
/// </remarks>
public static class LocalErrorClassifier
{
    // <asm-generic/errno-base.h> and <asm-generic/errno.h>. Named rather than inlined
    // because a bare "39" in a switch is unreadable a year later.
    private const int EPERM = 1;
    private const int ENOENT = 2;
    private const int EINTR = 4;
    private const int EIO = 5;
    private const int EACCES = 13;
    private const int EEXIST = 17;
    private const int EXDEV = 18;
    private const int ENOTDIR = 20;
    private const int EISDIR = 21;
    private const int EINVAL = 22;
    private const int ENOSPC = 28;
    private const int EROFS = 30;
    private const int ENAMETOOLONG = 36;
    private const int ENOTEMPTY = 39;
    private const int ELOOP = 40;
    private const int EDQUOT = 122;

    public static FileOperationException Classify(Exception ex, Location location) =>
        ex is FileOperationException already
            ? already
            : new FileOperationException(KindOf(ex), location, ex.Message, ex);

    public static FileErrorKind KindOf(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => FileErrorKind.NotFound,
        UnauthorizedAccessException => FileErrorKind.AccessDenied,
        PathTooLongException => FileErrorKind.InvalidName,
        NotSupportedException => FileErrorKind.Unsupported,
        IOException io => FromErrno(io),
        ArgumentException => FileErrorKind.InvalidName,
        _ => FileErrorKind.Unknown
    };

    private static FileErrorKind FromErrno(IOException io)
    {
        // HResult carries the raw errno on Unix. Zero means it was constructed
        // without one, so fall back to the type.
        var errno = io.HResult;
        return errno switch
        {
            EPERM or EACCES => FileErrorKind.AccessDenied,
            ENOENT => FileErrorKind.NotFound,
            EINTR => FileErrorKind.Interrupted,
            EEXIST => FileErrorKind.AlreadyExists,
            EXDEV => FileErrorKind.CrossDevice,
            ENOTDIR => FileErrorKind.NotADirectory,
            EISDIR => FileErrorKind.IsADirectory,
            ENOSPC or EDQUOT => FileErrorKind.NoSpace,
            EROFS => FileErrorKind.ReadOnly,
            ENAMETOOLONG or EINVAL => FileErrorKind.InvalidName,
            ENOTEMPTY => FileErrorKind.NotEmpty,
            ELOOP => FileErrorKind.TooManyLinks,
            EIO => FileErrorKind.Unknown,
            _ => io is FileNotFoundException or DirectoryNotFoundException
                ? FileErrorKind.NotFound
                : FileErrorKind.Unknown
        };
    }
}
