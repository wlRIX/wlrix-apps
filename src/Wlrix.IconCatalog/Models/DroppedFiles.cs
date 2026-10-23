// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Wlrix.Common.Desktop;

namespace Wlrix.IconCatalog.Models;

/// <summary>What a file dropped on a page becomes.</summary>
public static class DroppedFiles
{
    /// <summary>
    /// The item a dropped path is stored as. A launcher that is one of the installed
    /// applications is stored by its desktop id rather than its path, so it is the same icon as
    /// the one the catalog shows anyway, and follows that application if a newer copy shadows it.
    /// </summary>
    /// <param name="applicationDirectories">The <c>applications</c> directories, highest precedence first.</param>
    /// <param name="installedIds">The desktop ids the scan found.</param>
    public static ItemRef ItemFor(
        string path, IEnumerable<string> applicationDirectories, IReadOnlySet<string> installedIds)
    {
        if (path.EndsWith(".desktop", StringComparison.Ordinal))
        {
            foreach (var directory in applicationDirectories)
            {
                var relative = System.IO.Path.GetRelativePath(directory, path);
                if (relative.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relative))
                    continue;
                var id = relative.Replace(System.IO.Path.DirectorySeparatorChar, '-');
                if (installedIds.Contains(id))
                    return ItemRef.Application(id);
            }
        }

        return ItemRef.File(path);
    }

    /// <summary>
    /// The icon for a dropped file, or null when it is no longer there.
    /// </summary>
    /// <remarks>
    /// A launcher stands on a carpet like any installed application. Anything else draws its
    /// themed icon, as the desktop draws files: a folder, a program, or a document. A
    /// document's icon is the generic one -- the catalog has no MIME database of its own, and
    /// the file manager opens it properly either way.
    /// </remarks>
    public static CatalogIcon? Resolve(string path, CultureInfo culture)
    {
        var item = ItemRef.File(path);

        if (Directory.Exists(path))
            return new CatalogIcon { Item = item, Name = Label(path), IconNames = ["folder"], Path = path };

        if (!File.Exists(path))
            return null;

        if (path.EndsWith(".desktop", StringComparison.Ordinal) && ReadLauncher(path) is { } entry)
            return CatalogIcon.ForEntry(item, entry, culture);

        if (IsProgram(path))
        {
            return new CatalogIcon
            {
                Item = item,
                Name = Label(path),
                IconNames = ["application-x-executable"],
                Path = path,
                IsProgram = true,
            };
        }

        return new CatalogIcon { Item = item, Name = Label(path), IconNames = ["text-x-generic"], Path = path };
    }

    private static DesktopEntry? ReadLauncher(string path)
    {
        try
        {
            var entry = DesktopEntryParser.Parse(System.IO.Path.GetFileName(path), File.ReadLines(path), path);
            return entry is { Type: "Application", Exec.Length: > 0 } ? entry : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsProgram(string path)
    {
        try
        {
            const UnixFileMode anyExecute =
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & anyExecute) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Label(string path) =>
        System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name
            ? name
            : path;
}
