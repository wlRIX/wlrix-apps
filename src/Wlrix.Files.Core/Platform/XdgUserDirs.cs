namespace Wlrix.Files.Core.Platform;

/// <summary>One of the user's well-known directories.</summary>
/// <param name="Key">The XDG key, e.g. <c>XDG_DOWNLOAD_DIR</c>.</param>
/// <param name="Location">Where it is.</param>
public readonly record struct UserDirectory(string Key, Location Location);

/// <summary>
/// The user's well-known directories, per the XDG user-dirs specification.
/// </summary>
/// <remarks>
/// A C# port of the rules in <c>wlrix-desktop/src/xdg.rs</c>, which the desktop already
/// follows to find the desktop directory. The two must agree, or an icon on the desktop
/// and the same folder in the sidebar would disagree about where "Desktop" is.
///
/// <para>
/// Lookup order per key: the environment variable, then <c>user-dirs.dirs</c>, then a
/// conventional default under <c>$HOME</c>. A directory need not exist — the file is
/// written once at first login and entries outlive the folders.
/// </para>
/// </remarks>
public sealed class XdgUserDirs
{
    /// <summary>The keys the sidebar's Places section shows, in the order it shows them.</summary>
    public static readonly IReadOnlyList<string> PlacesKeys =
    [
        "XDG_DESKTOP_DIR",
        "XDG_DOCUMENTS_DIR",
        "XDG_DOWNLOAD_DIR",
        "XDG_MUSIC_DIR",
        "XDG_PICTURES_DIR",
        "XDG_VIDEOS_DIR"
    ];

    private static readonly Dictionary<string, string> Defaults = new(StringComparer.Ordinal)
    {
        ["XDG_DESKTOP_DIR"] = "Desktop",
        ["XDG_DOCUMENTS_DIR"] = "Documents",
        ["XDG_DOWNLOAD_DIR"] = "Downloads",
        ["XDG_MUSIC_DIR"] = "Music",
        ["XDG_PICTURES_DIR"] = "Pictures",
        ["XDG_VIDEOS_DIR"] = "Videos",
        ["XDG_PUBLICSHARE_DIR"] = "Public",
        ["XDG_TEMPLATES_DIR"] = "Templates"
    };

    private readonly Dictionary<string, string> _resolved;

    private XdgUserDirs(string home, Dictionary<string, string> resolved)
    {
        Home = Location.FromLocalPath(home);
        _resolved = resolved;
    }

    /// <summary>The user's home directory.</summary>
    public Location Home { get; }

    /// <summary>Reads the current user's directories.</summary>
    public static XdgUserDirs Read()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            home = "/";

        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(configHome))
            configHome = Path.Combine(home, ".config");

        var path = Path.Combine(configHome, "user-dirs.dirs");
        var lines = File.Exists(path) ? SafeReadLines(path) : [];
        return Parse(home, lines, Environment.GetEnvironmentVariable);
    }

    /// <summary>
    /// Parses <c>user-dirs.dirs</c> content. The seam the tests use, so they never
    /// depend on the machine's own configuration.
    /// </summary>
    public static XdgUserDirs Parse(string home, IEnumerable<string> lines, Func<string, string?>? environment = null)
    {
        var fromFile = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            // The file is shell syntax, always of the form
            //   XDG_MUSIC_DIR="$HOME/Music"
            // so a full shell parser is not warranted, but the quoting and the
            // literal $HOME both are.
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq].Trim();
            if (!key.StartsWith("XDG_", StringComparison.Ordinal))
                continue;

            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];
            if (value.Length == 0)
                continue;

            if (value.StartsWith("$HOME/", StringComparison.Ordinal))
                value = Path.Combine(home, value[6..]);
            else if (value == "$HOME")
                value = home;
            else if (!value.StartsWith('/'))
                continue; // relative and not $HOME-anchored: not something we can resolve

            fromFile[key] = value;
        }

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in Defaults.Keys.Concat(fromFile.Keys).Distinct(StringComparer.Ordinal))
        {
            var fromEnv = environment?.Invoke(key);
            if (!string.IsNullOrEmpty(fromEnv) && fromEnv.StartsWith('/'))
                resolved[key] = fromEnv;
            else if (fromFile.TryGetValue(key, out var v))
                resolved[key] = v;
            else if (Defaults.TryGetValue(key, out var d))
                resolved[key] = Path.Combine(home, d);
        }

        return new XdgUserDirs(home, resolved);
    }

    /// <summary>Where a key points, or null if it is not one we know.</summary>
    public Location? Get(string key) =>
        _resolved.TryGetValue(key, out var path) ? Location.FromLocalPath(path) : null;

    /// <summary>
    /// The Places entries, in sidebar order, skipping any that do not exist on disk
    /// and any that are just the home directory again.
    /// </summary>
    /// <remarks>
    /// A key pointing at <c>$HOME</c> is how the spec says "this user has no such
    /// folder", and showing Home twice under different names would be worse than
    /// omitting it.
    /// </remarks>
    public IReadOnlyList<UserDirectory> Places(Func<string, bool>? exists = null)
    {
        exists ??= Directory.Exists;
        var places = new List<UserDirectory>(PlacesKeys.Count);
        foreach (var key in PlacesKeys)
        {
            if (!_resolved.TryGetValue(key, out var path))
                continue;
            var location = Location.FromLocalPath(path);
            if (location == Home || !exists(path))
                continue;
            places.Add(new UserDirectory(key, location));
        }
        return places;
    }

    private static string[] SafeReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable user-dirs.dirs means the defaults, not a dead sidebar.
            return [];
        }
    }
}
