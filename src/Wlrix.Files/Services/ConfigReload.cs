using System.Runtime.InteropServices;
using Wlrix.Common;

namespace Wlrix.Files.Services;

/// <summary>
/// The pidfile and the <c>SIGHUP</c> that make <c>files.toml</c> reloadable.
/// </summary>
/// <remarks>
/// <c>wlrix-settings-daemon</c> writes the file and then signals whoever owns it, which needs a
/// pid, and a sibling process cannot read one from the environment. So it goes in a well-known
/// file under the per-user runtime directory — the same arrangement <c>wlrix-compositor</c>,
/// <c>wlrix-desktop</c> and <c>wlrix-idle</c> use, and the one the daemon looks for.
///
/// <para>
/// <b>Only the instance holding the bus name writes one.</b> A file manager is a window, not a
/// daemon, and several can be running — but the single-instance guard means only one of them
/// owns <c>com.wlrix.files</c>, every window belongs to it, and signalling any other would tell
/// a process that has already handed its arguments over and exited.
/// </para>
///
/// <para>
/// The file is removed on a clean exit. A crash leaves it stale, which is why the daemon treats
/// "no such process" as "not running" rather than trusting it — and why writing one does not
/// check for an existing one first.
/// </para>
/// </remarks>
public sealed class ConfigReload : IDisposable
{
    /// <summary>Named for the process, beside the compositor's and the desktop's.</summary>
    public const string PidFileName = "wlrix-files.pid";

    private readonly string? _path;
    private PosixSignalRegistration? _hangup;

    private ConfigReload(string? path) => _path = path;

    /// <summary>Raised on the signal, off the UI thread.</summary>
    public event Action? Reloaded;

    /// <summary>
    /// Writes the pidfile and starts listening, or does neither if it cannot.
    /// </summary>
    /// <remarks>
    /// Never throws. A read-only runtime directory costs the ability to reload settings without
    /// restarting, which is not worth failing a launch over.
    /// </remarks>
    public static ConfigReload Start(Action onReload)
    {
        var path = PidFilePath();
        var reload = new ConfigReload(path);
        reload.Reloaded += onReload;

        try
        {
            if (path is not null)
                File.WriteAllText(path, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reload = new ConfigReload(null);
            reload.Reloaded += onReload;
        }

        // The handler runs on a thread of the runtime's choosing, so whatever it calls has to
        // get itself onto the dispatcher. Kept deliberately thin for that reason.
        reload._hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, context =>
        {
            context.Cancel = true;
            reload.Reloaded?.Invoke();
        });

        return reload;
    }

    /// <summary>Where the pidfile goes.</summary>
    /// <remarks>
    /// <c>$XDG_RUNTIME_DIR</c>, which is owned by one user and cleaned up on logout, falling
    /// back to the temp directory — the same rule every other wlRIX pidfile follows, so they
    /// sit together.
    /// </remarks>
    public static string? PidFilePath()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtime) || !Path.IsPathRooted(runtime))
            runtime = Path.GetTempPath();

        return string.IsNullOrEmpty(runtime) ? null : Path.Combine(runtime, PidFileName);
    }

    public void Dispose()
    {
        _hangup?.Dispose();
        _hangup = null;

        try
        {
            if (_path is not null)
                File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale pidfile is what a crash leaves anyway, and the daemon already copes.
        }
    }
}
