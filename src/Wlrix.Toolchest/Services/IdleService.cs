using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;
using Wlrix.Toolchest.Localization;
using ZLogger;

namespace Wlrix.Toolchest.Services;

/// <summary>Locking the session, and keeping it from going idle.</summary>
public interface IIdleService
{
    /// <summary>Whether this Toolchest currently holds an idle inhibit.</summary>
    bool IsInhibited { get; }

    /// <summary>
    /// Takes or gives back the inhibit. Returns <c>null</c> once <see cref="IsInhibited"/>
    /// matches <paramref name="inhibited"/>, or a user-facing reason why it could not be made to.
    /// </summary>
    Task<string?> SetInhibitedAsync(bool inhibited);

    /// <summary>
    /// Asks the idle manager to lock now. False when nothing on the bus answered, so the caller
    /// can start a locker itself.
    /// </summary>
    Task<bool> LockAsync();
}

/// <summary>
/// <inheritdoc cref="IIdleService" />
///
/// Both go through <c>org.freedesktop.ScreenSaver</c>, which <c>wlrix-idle</c> serves. An
/// inhibit there holds off every countdown it runs -- dim, screen off, lock and sleep alike --
/// which is what Desktop → Inhibit Idle promises; the lid is left alone, since closing it is
/// something the user did rather than something that happened while they were away.
///
/// The inhibit is tied to this process's bus connection: <c>wlrix-idle</c> drops a cookie when
/// the name that took it leaves the bus. So the connection is kept open for the life of the
/// Toolchest, and a Toolchest that exits or crashes cannot leave the session stuck awake.
///
/// Lock goes the same way rather than straight to <c>wlrix-lock</c>, so the locker is whatever
/// <c>idle.toml</c>'s <c>[lock] command</c> names and <c>wlrix-idle</c> does not start a second
/// one beside a locker it already has running.
/// </summary>
public sealed class IdleService(ILogger<IdleService> logger) : IIdleService, IDisposable
{
    private const string BusName = "org.freedesktop.ScreenSaver";
    private const string ObjectPath = "/org/freedesktop/ScreenSaver";
    private const string Interface = "org.freedesktop.ScreenSaver";

    /// <summary>What the inhibit is filed under in <c>wlrix-idle</c>'s log.</summary>
    private const string ApplicationName = "wlrix-toolchest";

    private const string Reason = "Inhibit Idle in the Toolchest";

    private DBusConnection? _connection;
    private uint? _cookie;

    public bool IsInhibited => _cookie is not null;

    public async Task<string?> SetInhibitedAsync(bool inhibited)
    {
        if (inhibited == IsInhibited)
            return null;

        try
        {
            var connection = await ConnectAsync().ConfigureAwait(false);
            if (inhibited)
            {
                _cookie = await connection.CallMethodAsync(
                    Build(connection, "Inhibit", "ss", (ref MessageWriter writer) =>
                    {
                        writer.WriteString(ApplicationName);
                        writer.WriteString(Reason);
                    }),
                    static (message, _) => message.GetBodyReader().ReadUInt32()).ConfigureAwait(false);
                logger.ZLogInformation($"Holding off idle (cookie {_cookie}).");
            }
            else
            {
                var cookie = _cookie!.Value;
                await connection.CallMethodAsync(
                    Build(connection, "UnInhibit", "u", (ref MessageWriter writer) => writer.WriteUInt32(cookie))).ConfigureAwait(false);
                _cookie = null;
                logger.ZLogInformation($"Let go of the idle inhibit (cookie {cookie}).");
            }

            return null;
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or InvalidOperationException)
        {
            logger.ZLogWarning(ex, $"Could not {(inhibited ? "take" : "release")} the idle inhibit.");
            return inhibited ? Strings.InhibitIdleFailed(ex.Message) : Strings.ReleaseIdleFailed(ex.Message);
        }
    }

    public async Task<bool> LockAsync()
    {
        try
        {
            var connection = await ConnectAsync().ConfigureAwait(false);
            await connection.CallMethodAsync(Build(connection, "Lock", null, null)).ConfigureAwait(false);
            logger.ZLogInformation($"Asked {BusName} to lock.");
            return true;
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or InvalidOperationException)
        {
            logger.ZLogWarning(ex, $"Could not ask {BusName} to lock.");
            return false;
        }
    }

    private async Task<DBusConnection> ConnectAsync()
    {
        if (_connection is { } existing)
            return existing;

        if (DBusAddress.Session is not { } address)
            throw new InvalidOperationException("There is no session bus.");

        var connection = new DBusConnection(new DBusConnectionOptions(address) { AutoConnect = false });
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        _connection = connection;
        return connection;
    }

    // MessageWriter is a ref struct over the connection's send buffer and cannot be held across
    // the await that sends it, so each call's message is built here and handed back finished.
    private static MessageBuffer Build(
        DBusConnection connection, string member, string? signature, WriteBody? body)
    {
        // Disposed by hand rather than with `using`, which would forbid passing it by ref.
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(
                destination: BusName,
                path: ObjectPath,
                @interface: Interface,
                member: member,
                signature: signature);
            body?.Invoke(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private delegate void WriteBody(ref MessageWriter writer);

    public void Dispose()
    {
        // Closing the connection is what releases the inhibit, if one is still held.
        _connection?.Dispose();
        _connection = null;
    }
}
