using System.Text;
using Tmds.DBus.Protocol;

namespace Wlrix.Settings.Network.Services;

/// <summary>A connection's settings as NetworkManager sends them: section, then key.</summary>
internal sealed class SettingsMap : Dictionary<string, Dictionary<string, VariantValue>>
{
    public SettingsMap()
    {
    }

    public SettingsMap(IDictionary<string, Dictionary<string, VariantValue>> other)
    {
        // A copy deep enough that editing one section of the copy leaves the original alone.
        foreach (var (section, values) in other)
            this[section] = new Dictionary<string, VariantValue>(values);
    }
}

/// <summary>What it takes to join a saved network.</summary>
internal enum NetworkSecurity
{
    None,
    WpaPsk,
    Sae,
    Wep,
    /// <summary>802.1X: a username and certificates rather than a password.</summary>
    Enterprise,
    /// <summary>Enhanced Open: encrypted, but no password.</summary>
    EnhancedOpen,
}

/// <summary>One saved Wi-Fi connection, read out of its settings.</summary>
internal sealed record KnownNetwork(
    string Path,
    string Uuid,
    string Name,
    string Ssid,
    bool AutoConnect,
    int Priority,
    bool ForEveryone,
    bool AgentOwned,
    bool Hidden,
    NetworkSecurity Security)
{
    /// <summary>Whether there is a password to change.</summary>
    public bool HasPassword => Security is NetworkSecurity.WpaPsk or NetworkSecurity.Sae or NetworkSecurity.Wep;
}

/// <summary>
/// Reading a saved connection, and making edited copies of it.
/// </summary>
/// <remarks>
/// <para>
/// No bus here: settings go in as the dictionaries NetworkManager sends and come out as new ones
/// for <c>Update</c>, so every edit is a unit test. The settings are never edited in place --
/// a failed <c>Update</c> must leave nothing half-changed behind.
/// </para>
/// <para>
/// <b><c>Update</c> replaces the whole connection.</b> Settings fetched with <c>GetSettings</c>
/// carry no secrets, so sending them back as they are would send a connection with no password.
/// Every edit here therefore starts from settings with their secrets merged back in
/// (<see cref="WithSecrets"/>), which <see cref="NetworkManagerClient"/> fetches first.
/// </para>
/// </remarks>
internal static class ConnectionSettings
{
    public const string ConnectionSection = "connection";
    public const string WirelessSection = "802-11-wireless";
    public const string SecuritySection = "802-11-wireless-security";

    /// <summary><c>NM_SETTING_SECRET_FLAG_AGENT_OWNED</c>.</summary>
    private const uint AgentOwned = 0x1;

    /// <summary>A saved connection, if it is a Wi-Fi connection that joins a network.</summary>
    /// <remarks>
    /// A hotspot's profile names a network too, but it makes one rather than joining one, and it
    /// has no place in a list of networks to connect to.
    /// </remarks>
    public static KnownNetwork? Read(string path, IReadOnlyDictionary<string, Dictionary<string, VariantValue>> settings)
    {
        if (Text(settings, ConnectionSection, "type") != "802-11-wireless")
            return null;
        if (Text(settings, WirelessSection, "mode") is "ap" or "adhoc" or "mesh")
            return null;

        var security = SecurityOf(Text(settings, SecuritySection, "key-mgmt"), settings.ContainsKey(SecuritySection));
        var flagsKey = FlagsKey(security);
        var flags = flagsKey is null ? 0 : UInt(settings, SecuritySection, flagsKey) ?? 0;
        var ssid = Bytes(settings, WirelessSection, "ssid");
        return new KnownNetwork(
            Path: path,
            Uuid: Text(settings, ConnectionSection, "uuid") ?? string.Empty,
            Name: Text(settings, ConnectionSection, "id") ?? string.Empty,
            Ssid: ssid is null ? string.Empty : Encoding.UTF8.GetString(ssid),
            // NetworkManager leaves defaults out of what it sends, and both of these default on.
            AutoConnect: Bool(settings, ConnectionSection, "autoconnect") ?? true,
            Priority: Int(settings, ConnectionSection, "autoconnect-priority") ?? 0,
            ForEveryone: Strings(settings, ConnectionSection, "permissions") is not { Length: > 0 },
            AgentOwned: (flags & AgentOwned) != 0,
            Hidden: Bool(settings, WirelessSection, "hidden") ?? false,
            Security: security);
    }

    private static NetworkSecurity SecurityOf(string? keyMgmt, bool hasSection) => keyMgmt switch
    {
        null when !hasSection => NetworkSecurity.None,
        "wpa-psk" => NetworkSecurity.WpaPsk,
        "sae" => NetworkSecurity.Sae,
        "owe" => NetworkSecurity.EnhancedOpen,
        "wpa-eap" or "wpa-eap-suite-b-192" or "ieee8021x" => NetworkSecurity.Enterprise,
        // Static WEP keys have no key management; anything else unrecognized is treated the same,
        // which at worst offers a password field that does nothing.
        _ => NetworkSecurity.Wep,
    };

    /// <summary>The key a security type's password is kept under.</summary>
    public static string? SecretKey(NetworkSecurity security) => security switch
    {
        NetworkSecurity.WpaPsk or NetworkSecurity.Sae => "psk",
        NetworkSecurity.Wep => "wep-key0",
        _ => null,
    };

    /// <summary>The flags key that says who keeps <see cref="SecretKey"/>.</summary>
    public static string? FlagsKey(NetworkSecurity security) => security switch
    {
        NetworkSecurity.WpaPsk or NetworkSecurity.Sae => "psk-flags",
        NetworkSecurity.Wep => "wep-key-flags",
        _ => null,
    };

    /// <summary>The settings with their secrets put back, as <c>Update</c> needs them.</summary>
    public static SettingsMap WithSecrets(
        IDictionary<string, Dictionary<string, VariantValue>> settings,
        IDictionary<string, Dictionary<string, VariantValue>> secrets)
    {
        var merged = new SettingsMap(settings);
        foreach (var (section, values) in secrets)
        {
            if (!merged.TryGetValue(section, out var target))
                merged[section] = target = new Dictionary<string, VariantValue>();
            foreach (var (key, value) in values)
                target[key] = value;
        }

        return merged;
    }

    public static SettingsMap WithAutoConnect(IDictionary<string, Dictionary<string, VariantValue>> settings, bool on) =>
        With(settings, ConnectionSection, "autoconnect", VariantValue.Bool(on));

    /// <summary>
    /// Higher is preferred when several saved networks are in range. NetworkManager takes -999
    /// to 999; anything past that is clamped rather than refused.
    /// </summary>
    public static SettingsMap WithPriority(IDictionary<string, Dictionary<string, VariantValue>> settings, int priority) =>
        With(settings, ConnectionSection, "autoconnect-priority", VariantValue.Int32(Math.Clamp(priority, -999, 999)));

    /// <summary>A new password, kept wherever the old one was: the flags are left as they are.</summary>
    public static SettingsMap WithPassword(
        IDictionary<string, Dictionary<string, VariantValue>> settings, NetworkSecurity security, string password)
    {
        var key = SecretKey(security) ?? throw new InvalidOperationException($"{security} has no password");
        return With(settings, SecuritySection, key, VariantValue.String(password));
    }

    /// <summary>
    /// Everyone on the machine may use it, and NetworkManager keeps its password in the system
    /// profile. The password must be in <paramref name="settings"/> already (see
    /// <see cref="WithSecrets"/>), or the connection is left with none.
    /// </summary>
    public static SettingsMap ForEveryone(IDictionary<string, Dictionary<string, VariantValue>> settings, NetworkSecurity security)
    {
        var shared = With(settings, ConnectionSection, "permissions", VariantValue.Array(Array.Empty<string>()));
        return FlagsKey(security) is { } flags ? With(shared, SecuritySection, flags, VariantValue.UInt32(0)) : shared;
    }

    /// <summary>
    /// Only <paramref name="user"/> may use it, and the password moves to their keyring:
    /// NetworkManager hands an agent-owned secret to the user's secret agent, <c>wlrix-network</c>,
    /// to keep. Written <c>user:name:</c>, as NetworkManager writes it.
    /// </summary>
    public static SettingsMap ForUser(
        IDictionary<string, Dictionary<string, VariantValue>> settings, NetworkSecurity security, string user)
    {
        var owned = With(settings, ConnectionSection, "permissions", VariantValue.Array(new[] { $"user:{user}:" }));
        return FlagsKey(security) is { } flags ? With(owned, SecuritySection, flags, VariantValue.UInt32(AgentOwned)) : owned;
    }

    private static SettingsMap With(
        IDictionary<string, Dictionary<string, VariantValue>> settings, string section, string key, VariantValue value)
    {
        var copy = new SettingsMap(settings);
        if (!copy.TryGetValue(section, out var values))
            copy[section] = values = new Dictionary<string, VariantValue>();
        values[key] = value;
        return copy;
    }

    private static VariantValue? Get(IReadOnlyDictionary<string, Dictionary<string, VariantValue>> settings, string section, string key)
    {
        if (!settings.TryGetValue(section, out var values) || !values.TryGetValue(key, out var value))
            return null;
        // Values arrive as the variant's contents; one wrapped once more is unwrapped.
        return value.Type == VariantValueType.Variant ? value.GetVariantValue() : value;
    }

    internal static string? Text(IReadOnlyDictionary<string, Dictionary<string, VariantValue>> settings, string section, string key) =>
        Get(settings, section, key) is { Type: VariantValueType.String } value ? value.GetString() : null;

    internal static bool? Bool(IReadOnlyDictionary<string, Dictionary<string, VariantValue>> settings, string section, string key) =>
        Get(settings, section, key) is { Type: VariantValueType.Bool } value ? value.GetBool() : null;

    internal static int? Int(IReadOnlyDictionary<string, Dictionary<string, VariantValue>> settings, string section, string key) =>
        Get(settings, section, key) is { Type: VariantValueType.Int32 } value ? value.GetInt32() : null;

    internal static uint? UInt(IReadOnlyDictionary<string, Dictionary<string, VariantValue>> settings, string section, string key) =>
        Get(settings, section, key) is { Type: VariantValueType.UInt32 } value ? value.GetUInt32() : null;

    internal static byte[]? Bytes(IReadOnlyDictionary<string, Dictionary<string, VariantValue>> settings, string section, string key) =>
        Get(settings, section, key) is { Type: VariantValueType.Array } value ? value.GetArray<byte>() : null;

    internal static string[]? Strings(IReadOnlyDictionary<string, Dictionary<string, VariantValue>> settings, string section, string key) =>
        Get(settings, section, key) is { Type: VariantValueType.Array } value ? value.GetArray<string>() : null;
}
