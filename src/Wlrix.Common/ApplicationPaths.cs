namespace Wlrix.Common;

/// <summary>
/// wlRIX shared application data paths.
/// </summary>
public static class ApplicationPaths
{
    /// <summary>
    /// The local app data directory: <c>%APPDATA%/wlrix</c> on Windows, <c>~/.local/share/wlrix</c>
    /// on Unix.
    /// </summary>
    /// <remarks>
    /// Always absolute, and that is the whole point of the work below.
    /// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> uses
    /// <see cref="Environment.SpecialFolderOption.None"/>, which answers
    /// <see cref="string.Empty"/> when the directory does not exist yet — and
    /// <c>Path.Combine("", "wlrix")</c> is the relative path <c>wlrix</c>, which every
    /// application then writes its state into relative to wherever it happened to be
    /// launched from. A first run with <c>XDG_DATA_HOME</c> pointing somewhere not yet
    /// created is exactly when that happens, and the symptom is a <c>wlrix/</c> directory
    /// appearing in whatever the working directory was.
    /// </remarks>
    public static string AppData { get; } = Path.Combine(DataHome(), "wlrix");

    /// <summary>The user's data directory, created if it is not there.</summary>
    private static string DataHome()
    {
        // Create, not None: it both makes the directory and, having made it, returns the path
        // rather than an empty string.
        var known = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        if (Path.IsPathRooted(known))
            return known;

        // Creation itself failed — a read-only home, or no home at all. The XDG variable is
        // still the user's stated preference even if nothing has made it yet.
        if (Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
            && Path.IsPathRooted(xdg))
        {
            return xdg;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.IsPathRooted(home)
            ? Path.Combine(home, ".local", "share")
            // Nowhere left. The temp directory is a poor place to keep settings, but it is
            // absolute, which is the property that stops state landing in the working
            // directory of whatever launched the application.
            : Path.Combine(Path.GetTempPath(), "wlrix-data");
    }

    /// <summary>
    /// Ensures the directory at <paramref name="path"/> exists, creating it (and any missing
    /// parents) if necessary. Returns <paramref name="path"/> for convenient chaining.
    /// </summary>
    /// <param name="path">The directory path to ensure exists.</param>
    /// <returns><paramref name="path"/>.</returns>
    public static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
