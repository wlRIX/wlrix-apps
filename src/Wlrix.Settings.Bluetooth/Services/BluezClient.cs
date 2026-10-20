using Tmds.DBus.Protocol;
using Wlrix.Settings.Bluetooth.DBus;

namespace Wlrix.Settings.Bluetooth.Services;

/// <summary>The adapter, as the This Computer group shows it.</summary>
internal sealed record AdapterInfo(string Path, string Name, bool Powered, bool Blocked, bool Discoverable, bool Discovering);

/// <summary>One device the adapter knows: paired, or found by the scan.</summary>
internal sealed record DeviceInfo(
    string Path,
    string Address,
    string Name,
    bool HasName,
    string Icon,
    bool Paired,
    bool Connected,
    byte? Battery);

/// <summary>Everything one <c>GetManagedObjects</c> says.</summary>
internal sealed record BluezSnapshot(AdapterInfo? Adapter, IReadOnlyList<DeviceInfo> Devices);

/// <summary>
/// bluetoothd on the system bus: the adapter, its devices, and what this panel asks of them.
/// </summary>
/// <remarks>
/// <para>
/// The panel talks to BlueZ directly, as Network Settings talks to NetworkManager: BlueZ is the
/// store for adapters and pairings, and mirroring it through <c>wlrix-settings-daemon</c> would make
/// two sources of truth. bluetoothd's own D-Bus policy lets the user at the seat do everything here.
/// </para>
/// <para>
/// Pairing asks questions -- "does the headset show 123456?" -- which bluetoothd puts to the
/// default agent, and that is <c>wlrix-bluetooth</c>, not this window. So <see cref="PairAsync"/>
/// waits as long as the person takes to answer the prompt the daemon puts up.
/// </para>
/// </remarks>
internal sealed class BluezClient : IDisposable
{
    private const string BusName = "org.bluez";

    private readonly DBusConnection _connection;

    private BluezClient(DBusConnection connection) => _connection = connection;

    /// <summary>A connection of its own, so the panel can let go of the bus -- and the scan with
    /// it, which BlueZ stops when the client that started it leaves -- when it closes.</summary>
    public static async Task<BluezClient> ConnectAsync()
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
        return new BluezClient(connection);
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>Read the adapter and its devices.</summary>
    public async Task<BluezSnapshot> ReadAsync()
    {
        var objects = await new ObjectManager(_connection, BusName, "/").GetManagedObjectsAsync().ConfigureAwait(false);
        return Parse(objects.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value));
    }

    /// <summary>The first adapter by path, and its devices: paired first, then by name.</summary>
    internal static BluezSnapshot Parse(IReadOnlyDictionary<string, Dictionary<string, Dictionary<string, VariantValue>>> objects)
    {
        var adapter = objects
            .Where(pair => pair.Value.ContainsKey("org.bluez.Adapter1"))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                var properties = pair.Value["org.bluez.Adapter1"];
                return new AdapterInfo(
                    pair.Key,
                    Text(properties, "Alias") ?? Text(properties, "Name") ?? string.Empty,
                    Bool(properties, "Powered") ?? false,
                    Text(properties, "PowerState") == "off-blocked",
                    Bool(properties, "Discoverable") ?? false,
                    Bool(properties, "Discovering") ?? false);
            })
            .FirstOrDefault();
        if (adapter is null)
            return new BluezSnapshot(null, []);

        var devices = new List<DeviceInfo>();
        foreach (var (path, interfaces) in objects)
        {
            if (!interfaces.TryGetValue("org.bluez.Device1", out var properties))
                continue;
            if (Get(properties, "Adapter") is not { Type: VariantValueType.ObjectPath } owner
                || owner.GetObjectPathAsString() != adapter.Path)
                continue;
            var address = Text(properties, "Address") ?? string.Empty;
            var name = Text(properties, "Name");
            byte? battery = interfaces.TryGetValue("org.bluez.Battery1", out var level)
                && Get(level, "Percentage") is { Type: VariantValueType.Byte } percent
                ? percent.GetByte()
                : null;
            devices.Add(new DeviceInfo(
                path,
                address,
                Text(properties, "Alias") is { Length: > 0 } alias ? alias : name ?? address,
                name is { Length: > 0 },
                Text(properties, "Icon") ?? string.Empty,
                Bool(properties, "Paired") ?? false,
                Bool(properties, "Connected") ?? false,
                battery));
        }
        devices.Sort((a, b) => a.Paired != b.Paired
            ? b.Paired.CompareTo(a.Paired)
            : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return new BluezSnapshot(adapter, devices);
    }

    public Task SetPoweredAsync(string adapter, bool on) =>
        new Adapter1(_connection, BusName, adapter).SetPoweredAsync(on);

    public Task SetDiscoverableAsync(string adapter, bool on) =>
        new Adapter1(_connection, BusName, adapter).SetDiscoverableAsync(on);

    public Task StartDiscoveryAsync(string adapter) =>
        new Adapter1(_connection, BusName, adapter).StartDiscoveryAsync();

    public Task StopDiscoveryAsync(string adapter) =>
        new Adapter1(_connection, BusName, adapter).StopDiscoveryAsync();

    /// <summary>
    /// Pair, then trust and connect, as a person pairing a headset means: trusted, so it may
    /// reconnect on its own without asking every time, and connected, since that is why it was
    /// paired.
    /// </summary>
    public async Task PairAsync(string device)
    {
        var proxy = new Device1(_connection, BusName, device);
        await proxy.PairAsync().ConfigureAwait(false);
        await proxy.SetTrustedAsync(true).ConfigureAwait(false);
        await proxy.ConnectAsync().ConfigureAwait(false);
    }

    public Task ConnectAsync(string device) => new Device1(_connection, BusName, device).ConnectAsync();

    public Task DisconnectAsync(string device) => new Device1(_connection, BusName, device).DisconnectAsync();

    /// <summary>Forget a device: unpair it and drop it from the adapter's list.</summary>
    public Task RemoveAsync(string adapter, string device) =>
        new Adapter1(_connection, BusName, adapter).RemoveDeviceAsync(new ObjectPath(device));

    private static VariantValue? Get(Dictionary<string, VariantValue> properties, string key)
    {
        if (!properties.TryGetValue(key, out var value))
            return null;
        return value.Type == VariantValueType.Variant ? value.GetVariantValue() : value;
    }

    private static string? Text(Dictionary<string, VariantValue> properties, string key) =>
        Get(properties, key) is { Type: VariantValueType.String } value ? value.GetString() : null;

    private static bool? Bool(Dictionary<string, VariantValue> properties, string key) =>
        Get(properties, key) is { Type: VariantValueType.Bool } value ? value.GetBool() : null;
}
