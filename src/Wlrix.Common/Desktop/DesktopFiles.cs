namespace Wlrix.Common.Desktop;

/// <summary>
/// Where an application's entry lives, and where the user's own copy of it goes.
/// </summary>
/// <remarks>
/// The spec's way to change an installed application for one person: a file with the same
/// desktop-file id in <c>$XDG_DATA_HOME/applications</c>, which every launcher finds first and
/// so uses instead of the system one. Removing that file brings the installed entry back.
/// </remarks>
public static class DesktopFiles
{
    /// <summary>The user's <c>applications</c> directory: the first, highest-precedence one.</summary>
    public static string UserDirectory => DesktopEntryScanner.ApplicationDirectories().First();

    /// <summary>Where the user's copy of the entry with desktop-file id <paramref name="id"/> goes.</summary>
    /// <remarks>
    /// At the top of the directory: an id made from subdirectories (<c>kde4-foo.desktop</c> for
    /// <c>kde4/foo.desktop</c>) is the same id written flat, so the flat file shadows it too.
    /// </remarks>
    public static string UserPath(string id) => Path.Combine(UserDirectory, id);

    /// <summary>Whether <paramref name="path"/> is in the user's <c>applications</c> directory.</summary>
    public static bool IsUserFile(string path)
    {
        var relative = Path.GetRelativePath(UserDirectory, path);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    /// <summary>
    /// The installed entry with desktop-file id <paramref name="id"/> that the user's copy would
    /// hide: the first one found in the system directories, or null when there is none.
    /// </summary>
    public static string? Installed(string id)
    {
        foreach (var directory in DesktopEntryScanner.ApplicationDirectories().Skip(1))
        {
            if (!Directory.Exists(directory))
                continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.desktop", SearchOption.AllDirectories))
                {
                    if (Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '-') == id)
                        return file;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable corner of one directory; the rest may still have it.
            }
        }
        return null;
    }
}
