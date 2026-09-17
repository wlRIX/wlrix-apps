using System.Globalization;
using System.Text;

namespace Wlrix.Files.Core.Operations;

/// <summary>
/// Moving files to the trash, per the freedesktop Trash specification.
/// </summary>
/// <remarks>
/// A C# port of <c>wlrix-desktop/src/trash.rs</c>, and it has to agree with it byte for byte:
/// the desktop's Remove and the file manager's Move to Trash put things in the same place, and
/// either must be able to read back what the other wrote.
///
/// <para>
/// <b>Home trash only.</b> The spec also defines per-volume <c>.Trash</c> directories for files
/// on other filesystems, reached when the rename fails across a mount point. Copying and then
/// deleting across a filesystem is a different and much riskier operation — a half-copied tree
/// with the original already gone — so that case is refused with
/// <see cref="FileErrorKind.CrossDevice"/> and the caller offers a permanent delete instead.
/// </para>
/// </remarks>
public static class Trash
{
    /// <summary>How many numbered variants to try before giving up on a name.</summary>
    private const int MaxAttempts = 1000;

    /// <summary>The trash directory: <c>$XDG_DATA_HOME/Trash</c>, or the spec's default.</summary>
    public static string? Directory()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(dataHome))
            return Path.Combine(dataHome, "Trash");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".local", "share", "Trash");
    }

    /// <summary>Moves a local file or directory to the trash.</summary>
    /// <exception cref="FileOperationException">
    /// The location is not local, there is no home directory, the trash cannot be created, the
    /// item is on another filesystem, or the trash already holds too many files of that name.
    /// </exception>
    public static void Send(Location location, string? trashDirectory = null)
    {
        if (!location.TryGetLocalPath(out var path))
            throw new FileOperationException(FileErrorKind.Unsupported, location,
                "only local files can be trashed");

        var root = trashDirectory ?? Directory()
            ?? throw new FileOperationException(FileErrorKind.Unsupported, location,
                "no home directory, so no trash");

        var files = Path.Combine(root, "files");
        var info = Path.Combine(root, "info");
        try
        {
            System.IO.Directory.CreateDirectory(files);
            System.IO.Directory.CreateDirectory(info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FileOperationException(FileErrorKind.AccessDenied, location,
                $"could not create the trash directory: {ex.Message}", ex);
        }

        var name = location.Name;
        if (string.IsNullOrEmpty(name))
            throw new FileOperationException(FileErrorKind.InvalidName, location, "no usable name");

        if (FreeName(files, info, name) is not var (target, record))
            throw new FileOperationException(FileErrorKind.AlreadyExists, location,
                $"the trash already holds too many copies of '{name}'");

        // Written before the move: a .trashinfo with no file beside it is harmless clutter,
        // whereas a trashed file with no record cannot be put back.
        File.WriteAllText(record, TrashInfo(path), new UTF8Encoding(false));

        try
        {
            if (System.IO.Directory.Exists(path))
                System.IO.Directory.Move(path, target);
            else
                File.Move(path, target);
        }
        catch (Exception ex)
        {
            // Leave nothing behind for a move that did not happen.
            TryDelete(record);

            var kind = Filesystems.LocalErrorClassifier.KindOf(ex);
            if (kind == FileErrorKind.CrossDevice)
                throw new FileOperationException(FileErrorKind.CrossDevice, location,
                    $"{path} is on a different filesystem from the trash", ex);
            throw new FileOperationException(kind, location, $"could not trash {path}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// A name free in <b>both</b> <c>files</c> and <c>info</c>, with the two paths to use.
    /// </summary>
    /// <remarks>
    /// The pair has to be free together. Claiming one and finding the other taken would strand
    /// a file in the trash with no record of where it came from, which is the one outcome the
    /// spec's naming rule exists to prevent.
    /// </remarks>
    private static (string Target, string Record)? FreeName(string files, string info, string name)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var candidate = attempt == 0 ? name : Numbered(name, attempt + 1);
            var target = Path.Combine(files, candidate);
            var record = Path.Combine(info, candidate + ".trashinfo");

            // Path.Exists rather than File.Exists so a broken symlink still counts as taken.
            if (!Path.Exists(target) && !Path.Exists(record))
                return (target, record);
        }

        return null;
    }

    /// <summary><c>notes.txt</c> becomes <c>notes.2.txt</c>, keeping the extension where it belongs.</summary>
    private static string Numbered(string name, int number)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0
            ? $"{name[..dot]}.{number}{name[dot..]}"
            : $"{name}.{number}";
    }

    /// <summary>The <c>.trashinfo</c> body: where it came from, and when it went.</summary>
    internal static string TrashInfo(string path, DateTimeOffset? deletedAt = null)
    {
        var when = (deletedAt ?? DateTimeOffset.UtcNow).UtcDateTime;
        // The spec's YYYY-MM-DDThh:mm:ss. It asks for local time, but every reader treats the
        // field as informational and UTC is unambiguous.
        var stamp = when.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        return $"[Trash Info]\nPath={PercentEncode(path)}\nDeletionDate={stamp}\n";
    }

    /// <summary>
    /// Percent-encodes a path for the <c>Path=</c> field, leaving the separators alone.
    /// </summary>
    /// <remarks>
    /// The unreserved set plus <c>/</c>, matching the Rust side exactly. Encoding the slashes
    /// too would be valid per the URI grammar but would make the field unreadable and would
    /// disagree with what the desktop writes.
    /// </remarks>
    internal static string PercentEncode(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            if (b is >= (byte)'A' and <= (byte)'Z'
                or >= (byte)'a' and <= (byte)'z'
                or >= (byte)'0' and <= (byte)'9'
                or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~' or (byte)'/')
                sb.Append((char)b);
            else
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the caller is already reporting the real failure.
        }
    }
}
