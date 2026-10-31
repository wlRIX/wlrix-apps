using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;
using Wlrix.SoftwareManager.DBus;
using Wlrix.SoftwareManager.Localization;
using ZLogger;

namespace Wlrix.SoftwareManager.Services;

/// <summary>Restarting the machine, for the completion dialog's third choice.</summary>
public interface ISystemRestart
{
    /// <summary>Whether this session may restart the machine. A failed probe answers no.</summary>
    Task<bool> CanRestartAsync();

    /// <summary>Asks for the restart. Returns <c>null</c> once asked, or a reason why it could not be.</summary>
    Task<string?> RestartAsync();
}

/// <summary>
/// Talks to <c>systemd-logind</c> on the system bus, the same way <c>Wlrix.Shutdown</c> does and
/// generated from the same trimmed interface description. See its <c>LogindPowerService</c> for
/// why the call is interactive and what <c>challenge</c> means.
/// </summary>
public sealed class LogindSystemRestart(ILogger<LogindSystemRestart> logger) : ISystemRestart
{
    private const string BusName = "org.freedesktop.login1";
    private const string ObjectPath = "/org/freedesktop/login1";

    public async Task<bool> CanRestartAsync()
    {
        try
        {
            using var connection = await ConnectAsync().ConfigureAwait(false);
            var answer = await new Manager(connection, BusName, ObjectPath).CanRebootAsync().ConfigureAwait(false);
            return answer is "yes" or "challenge";
        }
        catch (Exception ex)
        {
            logger.ZLogWarning(ex, $"Could not ask logind whether this session may restart.");
            return false;
        }
    }

    public async Task<string?> RestartAsync()
    {
        try
        {
            using var connection = await ConnectAsync().ConfigureAwait(false);
            await new Manager(connection, BusName, ObjectPath).RebootAsync(true).ConfigureAwait(false);
            logger.ZLogInformation($"Asked logind to restart the system.");
            return null;
        }
        catch (Exception ex)
        {
            logger.ZLogWarning(ex, $"Could not restart the system.");
            return Strings.RestartFailed(ex is DBusErrorReplyException { ErrorMessage.Length: > 0 } reply
                ? reply.ErrorMessage
                : ex.Message);
        }
    }

    private static async Task<DBusConnection> ConnectAsync()
    {
        var address = DBusAddress.System ?? throw new InvalidOperationException("No system bus address.");
        var connection = new DBusConnection(new DBusConnectionOptions(address) { AutoConnect = false });
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
