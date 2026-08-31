using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;
using Wlrix.Shutdown.DBus;
using Wlrix.Shutdown.Localization;
using ZLogger;

namespace Wlrix.Shutdown.Services;

/// <summary>What this session is actually allowed to do to the machine.</summary>
/// <param name="CanPowerOff">Whether powering off is available.</param>
/// <param name="CanRestart">Whether restarting is available.</param>
/// <param name="CanFirmwareSetup">
/// Whether the machine can be sent to its firmware setup screen. False on anything that did not
/// boot through EFI, which is what gates the second checkbox.
/// </param>
public readonly record struct PowerCapabilities(bool CanPowerOff, bool CanRestart, bool CanFirmwareSetup)
{
    /// <summary>What is assumed before a probe has answered, and after one has failed.</summary>
    public static PowerCapabilities None => new(false, false, false);
}

/// <summary>Taking the machine down.</summary>
public interface IPowerService
{
    /// <summary>What this session may do. Never throws: a failed probe answers <see cref="PowerCapabilities.None"/>.</summary>
    Task<PowerCapabilities> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Powers the machine off. Returns <c>null</c> once it has been asked, or a user-facing
    /// reason why it could not be.
    /// </summary>
    Task<string?> PowerOffAsync();

    /// <summary>
    /// Restarts the machine, stopping at the firmware setup screen if
    /// <paramref name="toFirmwareSetup"/>. Returns <c>null</c> once it has been asked, or a
    /// user-facing reason why it could not be.
    /// </summary>
    Task<string?> RestartAsync(bool toFirmwareSetup);
}

/// <summary>
/// <inheritdoc cref="IPowerService" />
///
/// Talks to <c>systemd-logind</c> on the <em>system</em> bus — the one place on a systemd box
/// that will take the machine down for an unprivileged user, and the one every other desktop
/// asks. No <c>pkexec</c>, no helper binary: logind's own polkit actions
/// (<c>org.freedesktop.login1.power-off</c>, <c>.reboot</c>,
/// <c>.set-reboot-to-firmware-setup</c>) are <c>allow_active: yes</c> by default, so an active
/// local session is already authorized and nothing prompts.
///
/// The calls are made interactively (<c>PowerOff(true)</c>, <c>Reboot(true)</c>), which costs
/// nothing when the session is already authorized — polkit does not prompt for a permission it
/// would grant anyway — and is what makes the second-user case work at all: with somebody else
/// logged in, logind answers <c>challenge</c> and wants <c>reboot-multiple-sessions</c>
/// authenticated. Non-interactive would simply refuse there. With no authentication agent
/// registered, polkit answers rather than blocking, so this cannot hang waiting for a prompt
/// nobody can see.
///
/// This is also the one place that returns prose rather than throwing, the same shape as the
/// Toolchest's <c>ISessionService.LogOut</c>: every failure here is something to put in a
/// dialog and leave the window open behind, not something to crash on.
/// </summary>
public sealed class LogindPowerService(ILogger<LogindPowerService> logger) : IPowerService, IDisposable
{
    /// <summary>logind's well-known name on the system bus.</summary>
    private const string BusName = "org.freedesktop.login1";

    /// <summary>The manager object, where all six of the methods used here live.</summary>
    private const string ObjectPath = "/org/freedesktop/login1";

    private DBusConnection? _connection;
    private Manager? _manager;
    private bool _disposed;

    public async Task<PowerCapabilities> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var manager = await ConnectAsync(cancellationToken).ConfigureAwait(false);

            // Three round trips rather than one: logind has no batched form of these, and they
            // are cheap enough that asking once at startup is not worth a cache.
            var powerOff = await manager.CanPowerOffAsync().ConfigureAwait(false);
            var reboot = await manager.CanRebootAsync().ConfigureAwait(false);
            var firmware = await manager.CanRebootToFirmwareSetupAsync().ConfigureAwait(false);

            logger.ZLogInformation(
                $"logind: CanPowerOff={powerOff}, CanReboot={reboot}, CanRebootToFirmwareSetup={firmware}");

            return new PowerCapabilities(Allowed(powerOff), Allowed(reboot), Allowed(firmware));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A machine whose logind cannot be reached is one this window cannot do anything
            // to, and saying so belongs on the OK path, where the user asked for something.
            // Here it only decides which checkboxes are worth drawing.
            logger.ZLogWarning(ex, $"Could not ask logind what this session may do.");
            return PowerCapabilities.None;
        }
    }

    public async Task<string?> PowerOffAsync()
    {
        try
        {
            var manager = await ConnectAsync().ConfigureAwait(false);
            await manager.PowerOffAsync(true).ConfigureAwait(false);
            logger.ZLogInformation($"Asked logind to power the system off.");
            return null;
        }
        catch (Exception ex)
        {
            return Fail(Strings.PowerOffFailed(Detail(ex)), ex);
        }
    }

    public async Task<string?> RestartAsync(bool toFirmwareSetup)
    {
        Manager manager;
        try
        {
            manager = await ConnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Fail(Strings.RestartFailed(Detail(ex)), ex);
        }

        // The flag first, and on its own: it is the half that can be refused separately, and a
        // reboot that has already been asked for cannot be called back to report that the
        // firmware screen was not arranged after all. Failing here leaves the machine up.
        if (toFirmwareSetup)
        {
            try
            {
                await manager.SetRebootToFirmwareSetupAsync(true).ConfigureAwait(false);
                logger.ZLogInformation($"Armed the firmware-setup flag for the next boot.");
            }
            catch (Exception ex)
            {
                return Fail(Strings.FirmwareSetupFailed(Detail(ex)), ex);
            }
        }

        try
        {
            await manager.RebootAsync(true).ConfigureAwait(false);
            logger.ZLogInformation($"Asked logind to restart the system.");
            return null;
        }
        catch (Exception ex)
        {
            return Fail(Strings.RestartFailed(Detail(ex)), ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _connection?.Dispose();
        _connection = null;
        _manager = null;
    }

    /// <summary>
    /// Whether one of logind's <c>Can*</c> answers means the thing can be offered.
    ///
    /// The four it gives are <c>yes</c>, <c>no</c>, <c>na</c> and <c>challenge</c>.
    /// <c>challenge</c> counts as yes: it means allowed once somebody authenticates, which is
    /// the ordinary answer when a second user is logged in, and the interactive calls above
    /// are what give polkit its chance to ask. <c>na</c> does not — it means the operation
    /// does not apply to this machine at all, which is what a box that did not boot through
    /// EFI says to <c>CanRebootToFirmwareSetup</c>, and no amount of authenticating changes
    /// it.
    /// </summary>
    internal static bool Allowed(string answer) => answer is "yes" or "challenge";

    /// <summary>
    /// The connection and proxy, made on first use and kept.
    ///
    /// Its own connection rather than <c>DBusConnection.System</c>: the shared one is an
    /// autoconnect connection whose lifetime is the process's, and this class is disposable
    /// precisely so the app can drop the bus when the window closes. Same reasoning as
    /// <c>Wlrix.Settings.Client</c>, for a different property of the shared connection.
    /// </summary>
    private async Task<Manager> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_manager is { } existing)
            return existing;

        var address = DBusAddress.System
            ?? throw new InvalidOperationException(Strings.NoSystemBus);

        var connection = new DBusConnection(new DBusConnectionOptions(address) { AutoConnect = false });
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        _connection = connection;
        return _manager = new Manager(connection, BusName, ObjectPath);
    }

    /// <summary>What to show the user underneath our own sentence about what failed.</summary>
    private static string Detail(Exception ex) => ex switch
    {
        // Already prose, and already localized -- it came from Strings.NoSystemBus.
        InvalidOperationException => ex.Message,
        // A refused method carries the remote error's own message, which says considerably more
        // than "Interactive authentication required" would if we reduced it to a category.
        DBusErrorReplyException reply => reply.ErrorMessage is { Length: > 0 } text ? text : reply.ErrorName,
        _ => ex.Message,
    };

    private string Fail(string message, Exception ex)
    {
        logger.ZLogWarning(ex, $"{message}");
        return message;
    }
}
