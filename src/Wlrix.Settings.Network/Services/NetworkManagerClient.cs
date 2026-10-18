using Tmds.DBus.Protocol;
using Wlrix.Settings.Network.DBus;

namespace Wlrix.Settings.Network.Services;

/// <summary>A device's state, folded into what the panel says about it.</summary>
internal enum DeviceStateKind
{
    Unmanaged,
    Unavailable,
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Failed,
}

/// <summary>One network device, as the Status list shows it.</summary>
internal sealed record DeviceStatus(string Interface, bool Wireless, DeviceStateKind State, string? Connection, string? Address);

/// <summary>
/// NetworkManager on the system bus: what is connected, the saved Wi-Fi networks, and the edits
/// this panel makes to them.
/// </summary>
/// <remarks>
/// <para>
/// The panel talks to NetworkManager directly rather than through <c>wlrix-settings-daemon</c>:
/// that daemon owns wlRIX's own config files, and NetworkManager is already the store for
/// connections. Mirroring them would make two sources of truth.
/// </para>
/// <para>
/// Editing goes through NetworkManager's polkit policy, which lets the active local session change
/// its own connections and, on most distributions, system-wide ones. Where it does not, the call
/// waits on <c>wlrix-polkit-agent</c>'s password dialog -- which is why nothing here has a
/// timeout shorter than a person takes to type.
/// </para>
/// </remarks>
internal sealed class NetworkManagerClient : IDisposable
{
    private const string BusName = "org.freedesktop.NetworkManager";
    private const string RootPath = "/org/freedesktop/NetworkManager";
    private const string SettingsPath = "/org/freedesktop/NetworkManager/Settings";

    /// <summary><c>NM_DEVICE_TYPE_ETHERNET</c> and <c>NM_DEVICE_TYPE_WIFI</c>: the two the Status list shows.</summary>
    private const uint DeviceTypeEthernet = 1;
    private const uint DeviceTypeWifi = 2;

    private readonly DBusConnection _connection;
    private readonly NetworkManager _manager;
    private readonly DBus.Settings _settings;

    private NetworkManagerClient(DBusConnection connection)
    {
        _connection = connection;
        _manager = new NetworkManager(connection, BusName, RootPath);
        _settings = new DBus.Settings(connection, BusName, SettingsPath);
    }

    /// <summary>A connection of its own, so the panel can let go of the bus when it closes.</summary>
    public static async Task<NetworkManagerClient> ConnectAsync()
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

        return new NetworkManagerClient(connection);
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>The software switch and the hardware one.</summary>
    public async Task<(bool Enabled, bool HardwareEnabled)> ReadRadioAsync() =>
        (await _manager.GetWirelessEnabledAsync().ConfigureAwait(false),
            await _manager.GetWirelessHardwareEnabledAsync().ConfigureAwait(false));

    public Task SetWirelessAsync(bool on) => _manager.SetWirelessEnabledAsync(on);

    /// <summary>The Ethernet and Wi-Fi devices, in NetworkManager's order.</summary>
    /// <remarks>
    /// The rest -- loopback, bridges, Docker's veths, tunnels, Wi-Fi P2P -- are left out. They are
    /// NetworkManager's to list and nobody's to manage from a desktop panel, and on a developer's
    /// machine they outnumber the real devices five to one.
    /// </remarks>
    public async Task<IReadOnlyList<DeviceStatus>> ReadDevicesAsync()
    {
        var list = new List<DeviceStatus>();
        foreach (var path in await _manager.GetDevicesAsync().ConfigureAwait(false))
        {
            var device = new Device(_connection, BusName, path);
            DeviceProperties properties;
            try
            {
                properties = await device.GetPropertiesAsync().ConfigureAwait(false);
            }
            catch (DBusErrorReplyException)
            {
                // Gone between the list and the read; the next refresh will not list it.
                continue;
            }

            if (properties.DeviceType is not (DeviceTypeEthernet or DeviceTypeWifi))
                continue;
            var state = StateOf(properties.State);
            string? connection = null;
            if (properties.ActiveConnection.ToString() != "/")
            {
                try
                {
                    connection = await new Active(_connection, BusName, properties.ActiveConnection)
                        .GetIdAsync().ConfigureAwait(false);
                }
                catch (DBusErrorReplyException)
                {
                    // Torn down mid-read.
                }
            }

            string? address = null;
            if (state == DeviceStateKind.Connected && properties.Ip4Config.ToString() != "/")
            {
                try
                {
                    var data = await new IP4Config(_connection, BusName, properties.Ip4Config)
                        .GetAddressDataAsync().ConfigureAwait(false);
                    address = FirstAddress(data);
                }
                catch (DBusErrorReplyException)
                {
                    // Torn down mid-read.
                }
            }

            list.Add(new DeviceStatus(properties.Interface, properties.DeviceType == DeviceTypeWifi, state, connection, address));
        }

        return list;
    }

    /// <summary><c>NMDeviceState</c>, folded.</summary>
    internal static DeviceStateKind StateOf(uint code) => code switch
    {
        10 => DeviceStateKind.Unmanaged,
        20 => DeviceStateKind.Unavailable,
        30 => DeviceStateKind.Disconnected,
        >= 40 and <= 90 => DeviceStateKind.Connecting,
        100 => DeviceStateKind.Connected,
        110 => DeviceStateKind.Disconnecting,
        120 => DeviceStateKind.Failed,
        _ => DeviceStateKind.Unavailable,
    };

    /// <summary>The first <c>address/prefix</c> in an IP4Config's <c>AddressData</c>.</summary>
    internal static string? FirstAddress(Dictionary<string, VariantValue>[] data)
    {
        if (data.Length == 0 || !data[0].TryGetValue("address", out var address))
            return null;
        var text = address.GetString();
        return data[0].TryGetValue("prefix", out var prefix) ? $"{text}/{prefix.GetUInt32()}" : text;
    }

    /// <summary>Every saved Wi-Fi connection the user may see, sorted by name.</summary>
    public async Task<IReadOnlyList<KnownNetwork>> ReadKnownAsync()
    {
        var list = new List<KnownNetwork>();
        foreach (var path in await _settings.ListConnectionsAsync().ConfigureAwait(false))
        {
            try
            {
                var settings = await Saved(path).GetSettingsAsync().ConfigureAwait(false);
                if (ConnectionSettings.Read(path.ToString(), settings) is { } known)
                    list.Add(known);
            }
            catch (DBusErrorReplyException)
            {
                // Deleted between the list and the read.
            }
        }

        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }

    /// <summary>Call <paramref name="changed"/> whenever a saved connection comes or goes.</summary>
    public async Task WatchAsync(Action changed)
    {
        await _settings.WatchNewConnectionAsync(_ => changed()).ConfigureAwait(false);
        await _settings.WatchConnectionRemovedAsync(_ => changed()).ConfigureAwait(false);
    }

    /// <summary>
    /// Change a saved connection: read it with its secrets, apply <paramref name="edit"/>, and
    /// send the whole of it back.
    /// </summary>
    /// <remarks>
    /// The secrets are fetched first because <c>Update</c> replaces the connection outright, and
    /// <c>GetSettings</c> never includes them: an edit to the priority alone would otherwise save
    /// a connection with no password. For an agent-owned password NetworkManager asks the agent
    /// -- <c>wlrix-network</c>, which reads it from the keyring -- and hands the new one back to it
    /// when the update is saved.
    /// </remarks>
    public async Task EditAsync(KnownNetwork network, Func<SettingsMap, SettingsMap> edit)
    {
        var saved = Saved(network.Path);
        var settings = new SettingsMap(await saved.GetSettingsAsync().ConfigureAwait(false));
        if (ConnectionSettings.SecretKey(network.Security) is not null)
        {
            var secrets = await saved.GetSecretsAsync(ConnectionSettings.SecuritySection).ConfigureAwait(false);
            settings = ConnectionSettings.WithSecrets(settings, secrets);
        }

        await saved.UpdateAsync(edit(settings)).ConfigureAwait(false);
    }

    /// <summary>
    /// A new password. No secrets are fetched: the one secret there is is being replaced, and a
    /// password that cannot be fetched -- the reason to change it, often -- must not stop it.
    /// </summary>
    public async Task SetPasswordAsync(KnownNetwork network, string password)
    {
        var saved = Saved(network.Path);
        var settings = new SettingsMap(await saved.GetSettingsAsync().ConfigureAwait(false));
        await saved.UpdateAsync(ConnectionSettings.WithPassword(settings, network.Security, password)).ConfigureAwait(false);
    }

    /// <summary>Forget a network. NetworkManager tells the agent, which takes its password out of the keyring.</summary>
    public Task DeleteAsync(KnownNetwork network) => Saved(network.Path).DeleteAsync();

    /// <summary>Join a saved network, on whichever device NetworkManager picks.</summary>
    public Task ActivateAsync(KnownNetwork network) =>
        _manager.ActivateConnectionAsync(new ObjectPath(network.Path), new ObjectPath("/"), new ObjectPath("/"));

    private Connection Saved(string path) => new(_connection, BusName, new ObjectPath(path));

    private Connection Saved(ObjectPath path) => new(_connection, BusName, path);
}
