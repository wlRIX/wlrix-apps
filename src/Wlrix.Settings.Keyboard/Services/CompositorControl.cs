using System.Runtime.InteropServices;

namespace Wlrix.Settings.Keyboard.Services;

/// <summary>
/// Asks the running compositor to re-read its config. The compositor drops its pid in
/// <c>$XDG_RUNTIME_DIR/wlrix-compositor.pid</c>; sending it <c>SIGHUP</c> makes it reload
/// <c>compositor.toml</c> live, which is what lets the Test box reflect a change at once.
/// </summary>
public static partial class CompositorControl
{
    private const int Sighup = 1;

    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int sig);

    /// <summary>
    /// Signals the compositor to reload. Returns <c>false</c> when no live compositor could be
    /// found (no pidfile, or the pid is stale) — the config is still written either way.
    /// </summary>
    public static bool Reload()
    {
        if (ReadPid() is not { } pid)
            return false;
        return kill(pid, Sighup) == 0;
    }

    private static int? ReadPid()
    {
        var path = Path.Combine(RuntimeDirectory(), "wlrix-compositor.pid");
        try
        {
            return int.TryParse(File.ReadAllText(path).Trim(), out var pid) ? pid : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    // Mirrors the compositor's runtime_dir(): $XDG_RUNTIME_DIR when absolute, else the temp dir.
    private static string RuntimeDirectory()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(runtime) && Path.IsPathRooted(runtime))
            return runtime;
        return Path.GetTempPath();
    }
}
