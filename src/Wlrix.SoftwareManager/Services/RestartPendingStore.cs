using Microsoft.Extensions.Logging;
using Wlrix.Common;
using ZLogger;

namespace Wlrix.SoftwareManager.Services;

/// <summary>Remembers that a restart is owed, for as long as this boot lasts.</summary>
public interface IRestartPendingStore
{
    /// <summary>Whether a restart has been found to be needed since the machine booted.</summary>
    bool IsPending();

    /// <summary>Records that a restart is needed.</summary>
    void MarkPending();
}

/// <summary>
/// Keeps the current <c>boot_id</c> in <c>&lt;AppData&gt;/software-manager/restart-pending</c>.
/// The id changes on every boot, so restarting clears the flag without anything having to run.
/// Without this, choosing Continue and reopening the window later would forget the restart.
/// </summary>
public sealed class RestartPendingStore(ILogger<RestartPendingStore> logger) : IRestartPendingStore
{
    private const string BootIdPath = "/proc/sys/kernel/random/boot_id";

    private static string Path =>
        System.IO.Path.Combine(ApplicationPaths.AppData, "software-manager", "restart-pending");

    public bool IsPending()
    {
        try
        {
            return File.Exists(Path) && File.ReadAllText(Path).Trim() == File.ReadAllText(BootIdPath).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.ZLogWarning(ex, $"Could not read the pending restart flag.");
            return false;
        }
    }

    public void MarkPending()
    {
        try
        {
            ApplicationPaths.EnsureDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, File.ReadAllText(BootIdPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.ZLogWarning(ex, $"Could not save the pending restart flag.");
        }
    }
}
