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
    /// The directory the trashed things themselves are in, <c>Trash/files</c>: what the Dumpster
    /// shows, and what the desktop's Dumpster icon opens. Null with no home directory.
    /// </summary>
    public static string? FilesDirectory(string? trashDirectory = null) =>
        (trashDirectory ?? Directory()) is { } root ? Path.Combine(root, "files") : null;

    /// <summary>
    /// Whether <paramref name="location"/> is the Dumpster itself, so a drop there means "throw
    /// away" rather than "move here", and its listing offers Put Back and Empty.
    /// </summary>
    public static bool IsTrash(Location location, string? trashDirectory = null) =>
        location.TryGetLocalPath(out var path)
        && FilesDirectory(trashDirectory) is { } files
        && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(files)), StringComparison.Ordinal);

    /// <summary>Whether there is anything in the trash.</summary>
    public static bool IsEmpty(string? trashDirectory = null)
    {
        if (FilesDirectory(trashDirectory) is not { } files || !System.IO.Directory.Exists(files))
            return true;
        try
        {
            return !System.IO.Directory.EnumerateFileSystemEntries(files).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Deletes everything in the trash, for good: each trashed item, its record, and the
    /// spec's <c>directorysizes</c> cache.
    /// </summary>
    /// <returns>What could not be deleted, by name, with why. Empty when it all went.</returns>
    /// <remarks>
    /// Symbolic links are deleted, never followed. A link in the trash points somewhere the
    /// user did not throw away -- trashing a link to their home directory must not, on
    /// emptying, take the home directory with it.
    /// </remarks>
    public static IReadOnlyList<string> Empty(string? trashDirectory = null)
    {
        var problems = new List<string>();
        if ((trashDirectory ?? Directory()) is not { } root)
            return problems;

        var files = Path.Combine(root, "files");
        if (System.IO.Directory.Exists(files))
        {
            foreach (var entry in SafeEntries(files, problems))
            {
                try
                {
                    DeleteTree(entry);
                    // The record goes with the file, and only once the file has gone: a record
                    // left behind for something that would not delete still says where it
                    // came from.
                    TryDelete(Path.Combine(root, "info", Path.GetFileName(entry) + ".trashinfo"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problems.Add($"{Path.GetFileName(entry)}: {ex.Message}");
                }
            }
        }

        // Records with nothing beside them, left by a crash between writing one and moving
        // its file. Emptying is when they stop meaning anything.
        var info = Path.Combine(root, "info");
        if (System.IO.Directory.Exists(info))
        {
            foreach (var record in SafeEntries(info, problems))
            {
                var name = Path.GetFileName(record);
                if (name.EndsWith(".trashinfo", StringComparison.Ordinal)
                    && !Path.Exists(Path.Combine(files, name[..^".trashinfo".Length])))
                    TryDelete(record);
            }
        }

        TryDelete(Path.Combine(root, "directorysizes"));
        return problems;
    }

    /// <summary>
    /// Puts a trashed item back where it was thrown away from, returning that location.
    /// </summary>
    /// <exception cref="FileOperationException">
    /// The item is not in the trash, has no record of where it came from, or something else
    /// now has its old name.
    /// </exception>
    public static Location Restore(Location item, string? trashDirectory = null)
    {
        if (!item.TryGetLocalPath(out var path)
            || (trashDirectory ?? Directory()) is not { } root
            || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),
                Path.GetFullPath(Path.Combine(root, "files")), StringComparison.Ordinal))
            throw new FileOperationException(FileErrorKind.Unsupported, item, "this is not in the dumpster");

        var name = Path.GetFileName(path);
        var record = Path.Combine(root, "info", name + ".trashinfo");
        var original = ReadOriginalPath(record)
            ?? throw new FileOperationException(FileErrorKind.NotFound, item,
                $"there is no record of where {name} came from");
        var destination = Location.FromLocalPath(original);

        // Path.Exists so a dangling link in the way still counts as taken. Never replaced: the
        // thing there now is something the user made since, and putting back the old one must
        // not cost them the new one.
        if (Path.Exists(original))
            throw new FileOperationException(FileErrorKind.AlreadyExists, destination,
                $"{original} already exists");

        try
        {
            if (Path.GetDirectoryName(original) is { } parent)
                System.IO.Directory.CreateDirectory(parent);
            if (System.IO.Directory.Exists(path) && new FileInfo(path).LinkTarget is null)
                System.IO.Directory.Move(path, original);
            else
                File.Move(path, original);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FileOperationException(FileErrorKind.Unknown, item,
                $"could not put {name} back: {ex.Message}", ex);
        }

        TryDelete(record);
        return destination;
    }

    /// <summary>
    /// The original path a <c>.trashinfo</c> records, or null for a record that is missing or
    /// does not say. A relative path is relative to the trash's own volume, which for the home
    /// trash is the home directory's root -- in practice, <c>/</c>.
    /// </summary>
    internal static string? ReadOriginalPath(string record)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var inGroup = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inGroup = line == "[Trash Info]";
                continue;
            }
            if (inGroup && line.StartsWith("Path=", StringComparison.Ordinal))
            {
                var decoded = Uri.UnescapeDataString(line["Path=".Length..]);
                return decoded.Length == 0 ? null : Path.IsPathRooted(decoded) ? decoded : "/" + decoded;
            }
        }
        return null;
    }

    /// <summary>Deletes a file, a link or a whole directory tree, never following a link.</summary>
    private static void DeleteTree(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || !System.IO.Directory.Exists(path))
        {
            File.Delete(path);
            return;
        }

        foreach (var child in System.IO.Directory.EnumerateFileSystemEntries(path))
            DeleteTree(child);
        System.IO.Directory.Delete(path);
    }

    /// <summary>A directory's entries, or none with the reason recorded.</summary>
    private static string[] SafeEntries(string directory, List<string> problems)
    {
        try
        {
            return System.IO.Directory.GetFileSystemEntries(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{directory}: {ex.Message}");
            return [];
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
