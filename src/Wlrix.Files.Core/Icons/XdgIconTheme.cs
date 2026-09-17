namespace Wlrix.Files.Core.Icons;

/// <summary>
/// Turns an icon name into a file, per the freedesktop Icon Theme Specification.
/// </summary>
/// <remarks>
/// A C# port of what <c>wlrix-desktop/src/icon_theme.rs</c> gets from the
/// <c>freedesktop-icons</c> crate, so the desktop and the file manager resolve the same name
/// to the same artwork.
///
/// <para>
/// <b>A theme has to be named.</b> Searching only <c>hicolor</c> and
/// <c>/usr/share/pixmaps</c> finds almost nothing a file manager wants — the mimetype and
/// place icons all live in an installed theme. That is the bug that made
/// <c>wlrix-desktop</c> draw bare magic carpets and the tray draw nothing, and the reason
/// the default here is Adwaita rather than empty.
/// </para>
///
/// <para>
/// Resolution is cached, <b>including the misses</b>, and the whole cache is dropped when the
/// theme changes. Keeping the negative entries across a change is what would make the setting
/// appear to do nothing: those are exactly the names a new theme would find.
/// </para>
///
/// <para>
/// <b>Thread-safe.</b> One instance is shared by the application and consulted from whichever
/// pool thread is rendering an icon. Resolution walks the filesystem, so the lock is held only
/// around the caches and two threads may briefly duplicate a search rather than queue for it.
/// </para>
/// </remarks>
public sealed class XdgIconTheme
{
    /// <summary>The theme every other theme ultimately inherits.</summary>
    public const string Hicolor = "hicolor";

    /// <summary>What the file manager asks for when nothing is configured.</summary>
    public const string DefaultTheme = "Adwaita";

    // Order matters: png before svg means a hand-tuned raster icon wins over the scalable
    // one at the sizes where the raster exists, which is what the theme author intended.
    private static readonly string[] Extensions = [".png", ".svg", ".xpm"];

    private readonly IReadOnlyList<string> _baseDirectories;
    private readonly IReadOnlyList<string> _pixmapDirectories;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IconThemeIndex?> _themes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Name, int Size, int Scale), string?> _resolved = [];
    private string _theme = DefaultTheme;

    public XdgIconTheme(IReadOnlyList<string>? baseDirectories = null, IReadOnlyList<string>? pixmapDirectories = null)
    {
        _baseDirectories = baseDirectories ?? [.. DefaultBaseDirectories()];
        _pixmapDirectories = pixmapDirectories ?? ["/usr/share/pixmaps"];
    }

    /// <summary>
    /// The theme searched first. Empty means "no named theme", which is the pre-setting
    /// behavior: hicolor and pixmaps only.
    /// </summary>
    public string Theme
    {
        get => _theme;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_theme, value, StringComparison.Ordinal))
                return; // A no-op assignment must not throw away a warm cache.
            _theme = value;
            Clear();
        }
    }

    /// <summary>Forgets every resolution, hits and misses alike.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _resolved.Clear();
            _themes.Clear();
        }
    }

    /// <summary>The icon directories, in precedence order.</summary>
    public static IEnumerable<string> DefaultBaseDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(dataHome) && !string.IsNullOrEmpty(home))
            dataHome = Path.Combine(home, ".local", "share");
        if (!string.IsNullOrEmpty(dataHome))
            yield return Path.Combine(dataHome, "icons");

        // Legacy, but still populated by some installers and named by the spec.
        if (!string.IsNullOrEmpty(home))
            yield return Path.Combine(home, ".icons");

        var dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        if (string.IsNullOrEmpty(dataDirs))
            dataDirs = "/usr/local/share:/usr/share";
        foreach (var dir in dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(dir, "icons");
    }

    /// <summary>
    /// Finds the file for an icon name, or null.
    /// </summary>
    /// <param name="name">
    /// An icon name, or an absolute path — which is taken as-is, because that is what a
    /// <c>.desktop</c> file's <c>Icon=</c> is allowed to hold.
    /// </param>
    /// <param name="size">The size wanted. A request, not a promise.</param>
    /// <param name="scale">The display scale, for HiDPI theme directories.</param>
    public string? Lookup(string name, int size, int scale = 1)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        // Not cached: it is one existence check, and caching it would mean holding a stale
        // answer for a file the user can replace at any time.
        if (Path.IsPathRooted(name))
            return File.Exists(name) ? name : null;

        var key = (name, size, scale);
        lock (_gate)
        {
            if (_resolved.TryGetValue(key, out var cached))
                return cached;
        }

        var found = Search(name, size, scale);

        lock (_gate)
            _resolved[key] = found;
        return found;
    }

    /// <summary>The first of several candidate names that resolves.</summary>
    /// <remarks>
    /// What <see cref="Mime.SharedMimeDatabase.IconNamesFor"/> produces is a most-specific-first
    /// list, and this walks it: <c>text-markdown</c> if the theme has it, else the generic
    /// hint, else <c>text-x-generic</c>.
    /// </remarks>
    public string? LookupAny(IEnumerable<string> names, int size, int scale = 1)
    {
        foreach (var name in names)
        {
            if (Lookup(name, size, scale) is { } path)
                return path;
        }
        return null;
    }

    private string? Search(string name, int size, int scale)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrEmpty(_theme) && FindInTheme(_theme, name, size, scale, visited) is { } themed)
            return themed;

        // hicolor is what every theme inherits, and where an application's own installed
        // icon usually is. Reached explicitly in case the configured theme did not name it.
        if (FindInTheme(Hicolor, name, size, scale, visited) is { } fallback)
            return fallback;

        return FindInPixmaps(name);
    }

    /// <summary>Searches a theme and then, depth-first, the themes it inherits.</summary>
    private string? FindInTheme(string theme, string name, int size, int scale, HashSet<string> visited)
    {
        // A cycle in Inherits= would otherwise recurse forever, and themes in the wild do
        // occasionally inherit each other.
        if (!visited.Add(theme))
            return null;

        if (LoadTheme(theme) is not { } index)
            return null;

        if (FindInDirectories(theme, index, name, size, scale) is { } found)
            return found;

        foreach (var parent in index.Inherits)
        {
            if (FindInTheme(parent, name, size, scale, visited) is { } inherited)
                return inherited;
        }

        return null;
    }

    /// <summary>
    /// The spec's two-pass directory search: an exact size match anywhere in the theme wins,
    /// and only if there is none does the closest size get used.
    /// </summary>
    private string? FindInDirectories(string theme, IconThemeIndex index, string name, int size, int scale)
    {
        foreach (var directory in index.Directories)
        {
            if (!directory.Matches(size, scale))
                continue;
            if (FirstExisting(theme, directory.Path, name) is { } exact)
                return exact;
        }

        string? closest = null;
        var best = int.MaxValue;
        foreach (var directory in index.Directories)
        {
            var distance = directory.Distance(size, scale);
            if (distance >= best)
                continue;
            if (FirstExisting(theme, directory.Path, name) is not { } candidate)
                continue;
            closest = candidate;
            best = distance;
        }

        return closest;
    }

    /// <summary>
    /// The first existing file for a name in one theme subdirectory, across every base
    /// directory.
    /// </summary>
    /// <remarks>
    /// A theme can be split across base directories — a user's override in
    /// <c>~/.local/share/icons/Adwaita</c> beside the system's — so this walks all of them
    /// in precedence order rather than stopping at the first that contains the theme.
    /// </remarks>
    private string? FirstExisting(string theme, string subdirectory, string name)
    {
        foreach (var basePath in _baseDirectories)
        {
            foreach (var extension in Extensions)
            {
                var candidate = Path.Combine(basePath, theme, subdirectory, name + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    /// <summary>The unthemed fallback: a flat directory of loose icons.</summary>
    private string? FindInPixmaps(string name)
    {
        foreach (var directory in _pixmapDirectories)
        {
            foreach (var extension in Extensions)
            {
                var candidate = Path.Combine(directory, name + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    /// <summary>Reads and caches a theme's index, including the fact that it has none.</summary>
    private IconThemeIndex? LoadTheme(string theme)
    {
        lock (_gate)
        {
            if (_themes.TryGetValue(theme, out var cached))
                return cached;
        }

        IconThemeIndex? index = null;
        foreach (var basePath in _baseDirectories)
        {
            var path = Path.Combine(basePath, theme, "index.theme");
            if (!File.Exists(path))
                continue;
            try
            {
                index = IconThemeIndex.Parse(theme, File.ReadAllLines(path));
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable theme is one we do not have, not a reason to fail.
            }
        }

        lock (_gate)
            _themes[theme] = index;
        return index;
    }
}
