using Tmds.DBus.Protocol;
using Wlrix.Settings.Users.DBus.Fprint;

namespace Wlrix.Settings.Users.Services;

/// <summary>
/// fprintd on the system bus: whether there is a reader, and enrolling and deleting this user's
/// fingerprints on it.
/// </summary>
/// <remarks>
/// <para>
/// fprintd is D-Bus activated, so asking it for its devices both starts it and answers whether
/// there is anything to start it for: no fprintd installed is a <c>ServiceUnknown</c>, and fprintd
/// with no reader an empty list. Either way the panel shows no Fingerprints button.
/// </para>
/// <para>
/// A device has to be <em>claimed</em> before anything is read from or written to it, and only one
/// client holds it at a time -- a lock screen waiting for a finger holds it too. The empty user name
/// means "the caller", which is the only one this panel asks for: enrolling for somebody else needs
/// their finger on the reader anyway.
/// </para>
/// </remarks>
internal sealed class Fingerprints : IAsyncDisposable
{
    private const string BusName = "net.reactivated.Fprint";
    private const string ManagerPath = "/net/reactivated/Fprint/Manager";

    /// <summary>fprintd's finger names, in the order the list offers them.</summary>
    public static IReadOnlyList<string> Fingers { get; } =
    [
        "right-index-finger", "left-index-finger", "right-thumb", "left-thumb",
        "right-middle-finger", "left-middle-finger", "right-ring-finger", "left-ring-finger",
        "right-little-finger", "left-little-finger",
    ];

    private readonly DBusConnection _connection;
    private readonly Device _device;
    private bool _claimed;

    private Fingerprints(DBusConnection connection, Device device)
    {
        _connection = connection;
        _device = device;
    }

    /// <summary>Whether there is a reader to manage. False on any failure to ask.</summary>
    public static async Task<bool> AvailableAsync()
    {
        try
        {
            using var connection = await ConnectSystemAsync().ConfigureAwait(false);
            var devices = await new Manager(connection, BusName, ManagerPath).GetDevicesAsync().ConfigureAwait(false);
            return devices.Length > 0;
        }
        catch (Exception error) when (error is DBusExceptionBase or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Claim the default reader for the caller.</summary>
    public static async Task<Fingerprints> OpenAsync()
    {
        var connection = await ConnectSystemAsync().ConfigureAwait(false);
        try
        {
            var path = await new Manager(connection, BusName, ManagerPath).GetDefaultDeviceAsync().ConfigureAwait(false);
            var device = new Device(connection, BusName, path);
            await device.ClaimAsync(string.Empty).ConfigureAwait(false);
            return new Fingerprints(connection, device) { _claimed = true };
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static async Task<DBusConnection> ConnectSystemAsync()
    {
        var address = DBusAddress.System ?? throw new InvalidOperationException("no system bus");
        var connection = new DBusConnection(new DBusConnectionOptions(address) { AutoConnect = false });
        await connection.ConnectAsync().ConfigureAwait(false);
        return connection;
    }

    /// <summary>The reader's own name, for the dialog to say which it is talking to.</summary>
    public Task<string> NameAsync() => _device.GetNameAsync();

    /// <summary>How many touches an enrollment takes.</summary>
    public Task<int> StagesAsync() => _device.GetNumEnrollStagesAsync();

    /// <summary>The fingers enrolled for the caller. None is an empty list, not an error.</summary>
    public async Task<IReadOnlyList<string>> EnrolledAsync()
    {
        try
        {
            return await _device.ListEnrolledFingersAsync(string.Empty).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException error) when (error.ErrorName.EndsWith(".NoEnrolledPrints", StringComparison.Ordinal))
        {
            return [];
        }
    }

    /// <summary>
    /// Start enrolling <paramref name="finger"/>. <paramref name="status"/> is told each result
    /// fprintd sends -- <c>enroll-stage-passed</c>, <c>enroll-retry-scan</c>, ... -- and whether it
    /// was the last.
    /// </summary>
    public async Task<IDisposable> EnrollAsync(string finger, Action<string, bool> status)
    {
        var watch = await _device.WatchEnrollStatusAsync(args => status(args.Result, args.Done))
            .ConfigureAwait(false);
        try
        {
            await _device.EnrollStartAsync(finger).ConfigureAwait(false);
        }
        catch
        {
            watch.Dispose();
            throw;
        }
        return watch;
    }

    public Task StopEnrollAsync() => _device.EnrollStopAsync();

    public Task DeleteAsync(string finger) => _device.DeleteEnrolledFingerAsync(finger);

    public Task DeleteAllAsync() => _device.DeleteEnrolledFingers2Async();

    /// <summary>Let go of the reader, so the lock screen and <c>sudo</c> can have it.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_claimed)
        {
            _claimed = false;
            try
            {
                await _device.ReleaseAsync().ConfigureAwait(false);
            }
            catch (DBusExceptionBase)
            {
                // fprintd releases a claim whose client has gone, which is what happens next.
            }
        }
        _connection.Dispose();
    }
}
