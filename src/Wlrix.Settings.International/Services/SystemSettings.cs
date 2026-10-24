using Tmds.DBus.Protocol;
using Wlrix.Settings.International.DBus;

namespace Wlrix.Settings.International.Services;

/// <summary>The machine's clock as timedated reports it.</summary>
public sealed record ClockState(string TimeZone, bool Ntp, bool CanNtp, bool Synchronized);

/// <summary>
/// The machine-wide half: systemd-localed for the system language and keyboard, systemd-timedated
/// for the time zone and the clock.
/// </summary>
/// <remarks>
/// <para>
/// Both daemons run as root, write their own files (<c>/etc/locale.conf</c>,
/// <c>/etc/vconsole.conf</c>, the X11 keyboard config, <c>/etc/localtime</c>) and ask polkit whether
/// the caller may. Every call passes <c>interactive</c>, so polkit asks the session's agent --
/// <c>wlrix-polkit-agent</c> -- for an administrator's password rather than refusing outright, and
/// no call here has a timeout shorter than a person takes to type one.
/// </para>
/// <para>
/// Same connection shape as User Accounts' AccountsClient: the system bus, connected once.
/// </para>
/// </remarks>
internal sealed class SystemSettings : IDisposable
{
    private const string LocaleBus = "org.freedesktop.locale1";
    private const string LocalePath = "/org/freedesktop/locale1";
    private const string TimeBus = "org.freedesktop.timedate1";
    private const string TimePath = "/org/freedesktop/timedate1";

    private readonly DBusConnection _connection;
    private readonly locale1 _locale;
    private readonly timedate1 _time;
    private IDisposable? _watch;

    private SystemSettings(DBusConnection connection)
    {
        _connection = connection;
        _locale = new locale1(connection, LocaleBus, LocalePath);
        _time = new timedate1(connection, TimeBus, TimePath);
    }

    /// <summary>The clock changed: NTP was turned on or off, synchronized, or the zone moved.</summary>
    public event EventHandler? ClockChanged;

    public static async Task<SystemSettings> ConnectAsync()
    {
        var address = DBusAddress.System ?? throw new InvalidOperationException("no system bus");
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
        var settings = new SystemSettings(connection);
        settings._watch = await settings._time
            .WatchPropertiesChangedAsync((IChangedtimedate1Properties _) => settings.ClockChanged?.Invoke(settings, EventArgs.Empty),
                emitOnCapturedContext: false)
            .ConfigureAwait(false);
        return settings;
    }

    public void Dispose()
    {
        _watch?.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// The system language: <c>/etc/locale.conf</c>'s <c>LANG</c>, and the formats when they differ.
    /// </summary>
    public async Task<(string? Language, string? Formats)> GetLocaleAsync()
    {
        var properties = await _locale.GetPropertiesAsync().ConfigureAwait(false);
        var values = LocaleConf.Parse(string.Join('\n', properties.Locale));
        return (values.GetValueOrDefault("LANG"), LocaleConf.FormatsOf(values));
    }

    /// <summary>The system keyboard, as localed keeps it for X11 and the login screen's fallback.</summary>
    public async Task<KeyboardSetting> GetKeyboardAsync()
    {
        var properties = await _locale.GetPropertiesAsync().ConfigureAwait(false);
        return KeyboardSetting.Parse(properties.X11Layout, properties.X11Variant, properties.X11Model, properties.X11Options);
    }

    public async Task<ClockState> GetClockAsync()
    {
        var properties = await _time.GetPropertiesAsync().ConfigureAwait(false);
        return new ClockState(properties.Timezone, properties.NTP, properties.CanNTP, properties.NTPSynchronized);
    }

    /// <summary>Set <c>/etc/locale.conf</c>: <c>LANG</c>, and the formats variables when they differ.</summary>
    public Task SetLocaleAsync(string language, string? formats)
    {
        var assignments = new List<string> { $"LANG={language}" };
        if (formats is { Length: > 0 } && formats != language)
            assignments.AddRange(LocaleConf.FormatVariables.Select(variable => $"{variable}={formats}"));
        return _locale.SetLocaleAsync([.. assignments], true);
    }

    /// <summary>
    /// Set the system keyboard. <c>convert</c> is on, so localed also picks the nearest console
    /// keymap and the text consoles match.
    /// </summary>
    public Task SetKeyboardAsync(KeyboardSetting keyboard) =>
        _locale.SetX11KeyboardAsync(keyboard.Layout, keyboard.Model, keyboard.Variant, keyboard.Options, true, true);

    public Task SetTimeZoneAsync(string zone) => _time.SetTimezoneAsync(zone, true);

    public Task SetNtpAsync(bool enabled) => _time.SetNTPAsync(enabled, true);

    /// <summary>
    /// Whether <paramref name="error"/> is polkit saying no: the password dialog dismissed, or the
    /// wrong password. That is an answer rather than a failure, and is reported as one.
    /// </summary>
    public static bool IsRefusal(Exception error) =>
        error is DBusErrorReplyException reply
        && (reply.ErrorName.EndsWith(".AccessDenied", StringComparison.Ordinal)
            || reply.ErrorName.EndsWith(".InteractiveAuthorizationRequired", StringComparison.Ordinal)
            || reply.ErrorName.EndsWith(".NotAuthorized", StringComparison.Ordinal));

    /// <summary>What the daemon said when it refused, without the error name in front.</summary>
    public static string Message(Exception error) =>
        error is DBusErrorReplyException reply ? reply.ErrorMessage : error.Message;

    /// <summary>Set the clock. timedated refuses while NTP is on, which is why the panel disables this then.</summary>
    public Task SetTimeAsync(DateTimeOffset time) =>
        _time.SetTimeAsync(time.ToUnixTimeMilliseconds() * 1000, false, true);
}
