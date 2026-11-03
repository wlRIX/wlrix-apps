namespace Wlrix.Iconbox.Services;

/// <summary>
/// Finds the picture for a window's tile: the same file the compositor draws on its own
/// minimized-window tiles.
/// </summary>
/// <remarks>
/// A port of <c>wlrix-compositor/src/icon_image.rs</c>, which says why the pictures are fixed
/// artwork rather than a snapshot. The search has to match it exactly, or a window would show
/// one picture in the iconbox and another on the desktop when the iconbox is turned off:
/// <c>wlrix/images/&lt;app id&gt;.png</c> on every XDG data directory, user's first; an exact
/// name anywhere, then a case-insensitive one, then <c>default.png</c>.
/// </remarks>
public sealed class TileArt
{
    private const string DefaultImage = "default.png";
    private const string ImageSubdir = "wlrix/images";

    private readonly IReadOnlyList<string> _dirs;
    private readonly Dictionary<string, string?> _resolved = new(StringComparer.Ordinal);

    /// <summary>Searches the session's XDG data directories.</summary>
    public TileArt()
        : this(ImageDirs())
    {
    }

    /// <summary>Searches <paramref name="dirs"/>, most specific first.</summary>
    public TileArt(IReadOnlyList<string> dirs) => _dirs = dirs;

    /// <summary>The file for <paramref name="appId"/>, or the default, or null.</summary>
    public string? Find(string appId)
    {
        if (_resolved.TryGetValue(appId, out var known))
            return known;
        var found = Search(appId);
        _resolved[appId] = found;
        return found;
    }

    private string? Search(string appId)
    {
        if (appId.Length > 0)
        {
            var name = appId + ".png";
            var exact = _dirs.Select(dir => Path.Combine(dir, name)).FirstOrDefault(File.Exists);
            if (exact is not null)
                return exact;
            // Artwork named for a capitalized WM_CLASS (Blender.png), matched by an app id that
            // is not -- the compositor's reason, and so this one's.
            var folded = _dirs.Select(dir => Matching(dir, name)).FirstOrDefault(path => path is not null);
            if (folded is not null)
                return folded;
        }
        return _dirs.Select(dir => Path.Combine(dir, DefaultImage)).FirstOrDefault(File.Exists);
    }

    private static string? Matching(string dir, string name)
    {
        try
        {
            return Directory.EnumerateFiles(dir)
                .FirstOrDefault(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>$XDG_DATA_HOME</c> then <c>$XDG_DATA_DIRS</c>, with the spec's defaults, each joined
    /// with <c>wlrix/images</c>.
    /// </summary>
    internal static IReadOnlyList<string> ImageDirs()
    {
        var dirs = new List<string>();
        var home = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(home))
            dirs.Add(home);
        else if (Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } user)
            dirs.Add(Path.Combine(user, ".local", "share"));

        var system = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        if (string.IsNullOrEmpty(system))
            system = "/usr/local/share:/usr/share";
        dirs.AddRange(system.Split(':', StringSplitOptions.RemoveEmptyEntries));

        return [.. dirs.Select(dir => Path.Combine(dir, ImageSubdir))];
    }
}
