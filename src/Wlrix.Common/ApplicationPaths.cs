namespace Wlrix.Common;

/// <summary>
/// wlRIX shared application data paths.
/// </summary>
public static class ApplicationPaths
{
    /// <summary>
    /// The local app data directory: <c>%APPDATA%/wlrix</c> on Windows, <c>~/.local/share/wlrix</c>
    /// on Unix (via <see cref="Environment.SpecialFolder.LocalApplicationData"/>.
    /// </summary>
    public static string AppData { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wlrix");

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
