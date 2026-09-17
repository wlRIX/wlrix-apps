
namespace Wlrix.Common.Desktop;

/// <summary>
/// Discovers and filters installed <c>.desktop</c> application entries across the XDG data
/// directories, applying the Desktop Entry Spec's visibility rules and precedence (an id seen in
/// an earlier directory wins).
/// </summary>
public sealed class DesktopEntryScanner
{
    private readonly string[] _currentDesktop =
        (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty)
        .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public IReadOnlyList<DesktopEntry> Scan()
    {
        var result = new List<DesktopEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var dir in ApplicationDirectories())
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.desktop", SearchOption.AllDirectories))
            {
                // Desktop-file id: path relative to the applications dir with separators → '-'.
                var id = Path.GetRelativePath(dir, file).Replace(Path.DirectorySeparatorChar, '-');
                if (!seen.Add(id))
                    continue; // an earlier (higher-precedence) directory already provided this id

                DesktopEntry? entry;
                try
                {
                    entry = DesktopEntryParser.Parse(id, File.ReadLines(file));
                }
                catch (IOException)
                {
                    continue;
                }

                if (entry is not null && ShouldShow(entry))
                    result.Add(entry);
            }
        }

        return result;
    }

    private bool ShouldShow(DesktopEntry e)
    {
        if (e.Type is not "Application" || e.NoDisplay || e.Hidden || string.IsNullOrWhiteSpace(e.Exec))
            return false;

        if (e.TryExec is { Length: > 0 } tryExec && !Executables.Exists(tryExec))
            return false;

        if (e.OnlyShowIn.Count > 0 && !e.OnlyShowIn.Any(_currentDesktop.Contains))
            return false;

        if (e.NotShowIn.Count > 0 && e.NotShowIn.Any(_currentDesktop.Contains))
            return false;

        return true;
    }

    // XDG_DATA_HOME (default ~/.local/share) first, then XDG_DATA_DIRS in order — each /applications.
    private static IEnumerable<string> ApplicationDirectories()
    {
        var home = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(home))
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        yield return Path.Combine(home, "applications");

        var dirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        if (string.IsNullOrEmpty(dirs))
            dirs = "/usr/local/share:/usr/share";
        foreach (var dir in dirs.Split(':', StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(dir, "applications");
    }
}
