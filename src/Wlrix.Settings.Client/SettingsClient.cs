using Tmds.DBus.Protocol;
using Wlrix.Settings.Client.DBus;

namespace Wlrix.Settings.Client;

/// <summary>
/// Talks to <c>wlrix-settings-daemon</c>, the one program that writes the wlRIX config files.
///
/// Every wlRIX component still reads its own TOML file; what goes through here is the
/// <em>writing</em>. A settings app used to hand-roll a TOML editor, a pidfile read and a
/// <c>kill(pid, SIGHUP)</c>; now it asks the daemon, which does all three once, correctly, and
/// tells every other panel what changed.
///
/// The daemon is bus-activated, so nothing has to start it: the first call does. If it is not
/// on the bus at all that is a broken install, and this class says so rather than falling back
/// to editing TOML — which would put back exactly the duplication it exists to remove.
/// </summary>
public sealed class SettingsClient : IDisposable
{
    private readonly DBusConnection _connection;
    private readonly Settings1 _proxy;
    private readonly List<IDisposable> _watches = [];
    private bool _disposed;

    private SettingsClient(DBusConnection connection, Settings1 proxy)
    {
        _connection = connection;
        _proxy = proxy;
    }

    /// <summary>The daemon's well-known name.</summary>
    public const string BusName = "com.wlrix.Settings";

    /// <summary>The object everything is served at.</summary>
    public const string ObjectPath = "/com/wlrix/Settings";

    /// <summary>
    /// This connection's unique bus name, for recognizing the echo of our own writes.
    ///
    /// Every <see cref="SettingsChangedEventArgs"/> carries the name of whoever caused it. A
    /// panel that acts on its own change will fight its own debounce timer, so compare
    /// <see cref="SettingsChangedEventArgs.Origin"/> against this and drop the match. A
    /// hand-edited file arrives as <c>external</c>, which never matches.
    /// </summary>
    /// <remarks>
    /// Empty before the connection has completed its handshake, which cannot happen here:
    /// <see cref="ConnectAsync"/> awaits it before this object exists.
    /// </remarks>
    public string UniqueName => _connection.UniqueName ?? string.Empty;

    /// <summary>
    /// Connect to the session bus and to the daemon.
    ///
    /// Throws if there is no session bus. Whether the daemon itself answers is only discovered
    /// on the first call, because bus activation means it does not exist until then.
    /// </summary>
    public static async Task<SettingsClient> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var address = DBusAddress.Session
            ?? throw new InvalidOperationException("no session bus to connect to");

        // Our own connection rather than the shared `DBusConnection.Session`, and the reason is
        // not tidiness: the shared one is an autoconnect connection, and an autoconnect
        // connection refuses to tell you its `UniqueName`. Without that there is no way to
        // recognize the echo of our own writes, which is the one thing a settings panel cannot
        // do without. Same arrangement Avalonia's own DBusHelper uses, for its own reasons.
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
        return new SettingsClient(connection, new Settings1(connection, BusName, ObjectPath));
    }

    /// <summary>Every namespace the daemon serves: compositor, desktop, idle, portal, session.</summary>
    public Task<string[]> ListNamespacesAsync() => _proxy.ListNamespacesAsync();

    /// <summary>
    /// The schema for a whole namespace, in one round trip: type, range, choices, unit,
    /// default and prose for each setting.
    ///
    /// This is what lets a panel render and validate without hardcoding a single wlRIX key
    /// name or default. Ordered by key, so a caller gets the same layout twice.
    /// </summary>
    public async Task<IReadOnlyList<SettingDescription>> DescribeAsync(string ns)
    {
        var described = await _proxy.DescribeAllAsync(ns).ConfigureAwait(false);
        return described
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => SettingDescription.From(entry.Value))
            .ToList();
    }

    /// <summary>The schema for one setting.</summary>
    public async Task<SettingDescription> DescribeAsync(string ns, string key) =>
        SettingDescription.From(await _proxy.DescribeAsync(Key(ns, key)).ConfigureAwait(false));

    /// <summary>
    /// The effective value of every setting in a namespace: what the file says, or the
    /// declared default.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, object?>> GetAllAsync(string ns)
    {
        var values = await _proxy.GetAllAsync(ns).ConfigureAwait(false);
        return values.ToDictionary(entry => entry.Key, entry => Values.ToClr(entry.Value));
    }

    /// <summary>
    /// Where each value in a namespace comes from: <c>user</c>, <c>system</c> or
    /// <c>default</c>.
    ///
    /// What lets a panel gray out a Reset that would do nothing, and be honest that a value it
    /// is showing came from <c>/etc/wlrix</c> rather than from the person using it.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> SourcesAsync(string ns) =>
        await _proxy.SourcesAsync(ns).ConfigureAwait(false);

    /// <summary>
    /// Write several settings at once.
    ///
    /// One call is one write per file and one signal per owner, however many keys are in it, so
    /// a panel applying four keyboard fields does not make the compositor recompile its keymap
    /// four times. Every key is validated before any file is touched, so a batch with one bad
    /// value leaves nothing half-applied.
    ///
    /// Prefer this over calling <see cref="SetAsync"/> in a loop: the difference is not
    /// efficiency, it is that a loop can be observed halfway through.
    /// </summary>
    public async Task<ApplyResult> SetManyAsync(IReadOnlyDictionary<string, object> values)
    {
        var wire = new Dictionary<string, VariantValue>(values.Count);
        foreach (var (key, value) in values)
            wire[key] = Values.ToVariant(key, value);
        return new ApplyResult(await _proxy.SetManyAsync(wire).ConfigureAwait(false));
    }

    /// <summary>Write one setting. Sugar for a one-entry <see cref="SetManyAsync"/>.</summary>
    public Task<ApplyResult> SetAsync(string key, object value) =>
        SetManyAsync(new Dictionary<string, object> { [key] = value });

    /// <summary>
    /// Remove settings, so each falls back to its default.
    ///
    /// The key is deleted from the file rather than set to the default: an absent key <em>is</em>
    /// the default throughout wlRIX, and writing today's value would pin it forever.
    /// </summary>
    public async Task<ApplyResult> ResetAsync(params string[] keys) =>
        new(await _proxy.ResetAsync(keys).ConfigureAwait(false));

    /// <summary>Which config files will not currently parse, and why.</summary>
    public async Task<IReadOnlyDictionary<string, string>> InvalidAsync() =>
        await _proxy.GetInvalidAsync().ConfigureAwait(false);

    /// <summary>
    /// Start listening for changes. Raises <see cref="Changed"/>, <see cref="FileInvalid"/> and
    /// <see cref="FileRecovered"/> until the client is disposed.
    ///
    /// Separate from <see cref="ConnectAsync"/> so a caller that only writes pays for no match
    /// rules. Handlers run on a bus thread; marshal to the UI thread yourself.
    /// </summary>
    public async Task WatchAsync()
    {
        _watches.Add(await _proxy
            .WatchChangedAsync(
                signal => Changed?.Invoke(
                    this,
                    new SettingsChangedEventArgs(
                        signal.Values.ToDictionary(e => e.Key, e => Values.ToClr(e.Value)),
                        signal.Origin)),
                emitOnCapturedContext: false)
            .ConfigureAwait(false));

        _watches.Add(await _proxy
            .WatchFileInvalidAsync(
                signal => FileInvalid?.Invoke(
                    this,
                    new SettingsFileInvalidEventArgs(signal.Namespace, signal.Path, signal.Message)),
                emitOnCapturedContext: false)
            .ConfigureAwait(false));

        _watches.Add(await _proxy
            .WatchFileRecoveredAsync(
                signal => FileRecovered?.Invoke(
                    this,
                    new SettingsFileInvalidEventArgs(signal.Namespace, signal.Path, string.Empty)),
                emitOnCapturedContext: false)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// A setting's effective value changed — because somebody called Set, or because the file
    /// was edited by hand.
    ///
    /// Carries only the keys that actually moved, and the origin of the change. Raised on a bus
    /// thread.
    /// </summary>
    public event EventHandler<SettingsChangedEventArgs>? Changed;

    /// <summary>A config file was edited into something that will not parse.</summary>
    public event EventHandler<SettingsFileInvalidEventArgs>? FileInvalid;

    /// <summary>...and then fixed. A <see cref="Changed"/> with whatever moved follows.</summary>
    public event EventHandler<SettingsFileInvalidEventArgs>? FileRecovered;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var watch in _watches)
            watch.Dispose();
        _watches.Clear();
        // Ours to close: `ConnectAsync` built it rather than taking the shared session one.
        _connection.Dispose();
    }

    private static string Key(string ns, string key) => key.StartsWith(ns + ".", StringComparison.Ordinal) ? key : $"{ns}.{key}";
}
