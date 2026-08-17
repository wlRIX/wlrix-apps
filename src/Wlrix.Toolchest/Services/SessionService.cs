using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Wlrix.Toolchest.Services;

/// <summary>Ending the wlRIX session.</summary>
public interface ISessionService
{
    /// <summary>
    /// Asks the compositor to stop, which ends the session. Returns <c>null</c> once it has
    /// been asked, or a user-facing reason why it could not be.
    /// </summary>
    string? LogOut();
}

/// <summary>
/// <inheritdoc cref="ISessionService" />
///
/// <c>wlrix-session</c> starts the compositor and exits when it does — "the compositor is the
/// session" — so logging out means asking the compositor to stop and letting everything above
/// it unwind: the session tears down and <c>wlrix-greeter</c> comes back up. The compositor
/// drops its pid in a well-known file for exactly this, and <c>SIGTERM</c> is the signal it
/// installs a handler for.
///
/// A stale pidfile is the awkward case: a crashed compositor leaves one behind, and signaling
/// whatever pid has since been recycled would be worse than doing nothing. So "no such process"
/// is reported rather than trusted blindly, and a pidfile naming 0 (the whole process group) or
/// 1 (init) is refused outright.
///
/// This is the same reasoning, and the same pidfile, as <c>wlrix-desktop/src/session.rs</c>.
/// </summary>
public sealed class SessionService(ILogger<SessionService> logger) : ISessionService
{
    /// <summary>
    /// The compositor's pidfile, beside its log under the per-user runtime directory. Kept in
    /// step with <c>wlrix-compositor/src/pidfile.rs</c> by hand — the repos build standalone,
    /// so there is no shared constant to point at.
    /// </summary>
    private const string PidName = "wlrix-compositor.pid";

    private const int Sigterm = 15;

    // Linux errno values, for telling a stale pidfile apart from a real failure.
    private const int Eperm = 1;
    private const int Esrch = 3;

    public string? LogOut()
    {
        var path = PidFile();

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"Could not read {path}.\n{ex.Message}");
        }

        if (ParsePid(text) is not { } pid)
            return Fail($"{path} does not name a compositor to stop.");

        if (Kill(pid, Sigterm) == 0)
        {
            logger.ZLogInformation($"Asked the compositor (pid {pid}) to stop.");
            return null;
        }

        return Fail(Marshal.GetLastPInvokeError() switch
        {
            // The pidfile outlived the compositor that wrote it.
            Esrch => $"There is no process {pid}; the compositor's pidfile is stale.",
            Eperm => $"Not allowed to signal process {pid}.",
            var errno => $"Could not signal process {pid} (errno {errno}).",
        });
    }

    /// <summary>
    /// Where the pidfile lives: <c>$XDG_RUNTIME_DIR</c>, else the temp directory — the same rule
    /// the compositor follows when it writes the file.
    /// </summary>
    internal static string PidFile()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtime) || !Path.IsPathRooted(runtime))
            runtime = Path.GetTempPath();

        return Path.Combine(runtime, PidName);
    }

    /// <summary>The pid a pidfile's contents name, if it is one worth signaling.</summary>
    internal static int? ParsePid(string text) =>
        int.TryParse(text.Trim(), out var pid) && pid > 1 ? pid : null;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);

    private string Fail(string message)
    {
        logger.ZLogWarning($"Could not log out: {message}");
        return message;
    }
}
