using System.Text;
using Tmds.DBus.Protocol;
using Wlrix.Settings.Network.Services;
using Xunit;

namespace Wlrix.Settings.Network.Tests;

/// <summary>Reading a saved connection, and the edited copies sent back to NetworkManager.</summary>
public class ConnectionSettingsTests
{
    /// <summary>A saved WPA network the way GetSettings returns one: no secrets, defaults left out.</summary>
    private static SettingsMap Wpa(string? permissions = null, uint pskFlags = 0) => new()
    {
        ["connection"] = new()
        {
            ["id"] = VariantValue.String("HomeNet"),
            ["uuid"] = VariantValue.String("u-1"),
            ["type"] = VariantValue.String("802-11-wireless"),
            ["permissions"] = VariantValue.Array(permissions is null ? Array.Empty<string>() : new[] { permissions }),
        },
        ["802-11-wireless"] = new()
        {
            ["ssid"] = VariantValue.Array(Encoding.UTF8.GetBytes("HomeNet")),
            ["mode"] = VariantValue.String("infrastructure"),
        },
        ["802-11-wireless-security"] = new()
        {
            ["key-mgmt"] = VariantValue.String("wpa-psk"),
            ["psk-flags"] = VariantValue.UInt32(pskFlags),
        },
    };

    private static KnownNetwork Read(SettingsMap settings) =>
        ConnectionSettings.Read("/settings/1", settings) ?? throw new Xunit.Sdk.XunitException("not read");

    [Fact]
    public void ASavedNetworkIsReadWithItsDefaults()
    {
        var network = Read(Wpa());
        Assert.Equal("HomeNet", network.Name);
        Assert.Equal("HomeNet", network.Ssid);
        Assert.Equal("u-1", network.Uuid);
        Assert.True(network.AutoConnect, "absent means on");
        Assert.Equal(0, network.Priority);
        Assert.True(network.ForEveryone, "no permissions means everyone");
        Assert.False(network.AgentOwned);
        Assert.Equal(NetworkSecurity.WpaPsk, network.Security);
        Assert.True(network.HasPassword);
    }

    [Fact]
    public void APerUserNetworkSaysSo()
    {
        var network = Read(Wpa("user:alice:", pskFlags: 1));
        Assert.False(network.ForEveryone);
        Assert.True(network.AgentOwned);
    }

    [Fact]
    public void HotspotsAndWiredConnectionsAreNotKnownNetworks()
    {
        var hotspot = Wpa();
        hotspot["802-11-wireless"]["mode"] = VariantValue.String("ap");
        Assert.Null(ConnectionSettings.Read("/s", hotspot));

        var wired = Wpa();
        wired["connection"]["type"] = VariantValue.String("802-3-ethernet");
        Assert.Null(ConnectionSettings.Read("/s", wired));
    }

    [Fact]
    public void OpenAndEnterpriseNetworksHaveNoPasswordToChange()
    {
        var open = Wpa();
        open.Remove("802-11-wireless-security");
        Assert.Equal(NetworkSecurity.None, Read(open).Security);
        Assert.False(Read(open).HasPassword);

        var enterprise = Wpa();
        enterprise["802-11-wireless-security"]["key-mgmt"] = VariantValue.String("wpa-eap");
        Assert.Equal(NetworkSecurity.Enterprise, Read(enterprise).Security);
        Assert.False(Read(enterprise).HasPassword);
    }

    [Fact]
    public void AnEditKeepsThePasswordItWasGiven()
    {
        // GetSettings has no psk; Update replaces everything. The secrets have to be merged back in
        // first, or changing the priority would save a network with no password.
        var secrets = new Dictionary<string, Dictionary<string, VariantValue>>
        {
            ["802-11-wireless-security"] = new() { ["psk"] = VariantValue.String("secret pass") },
        };
        var merged = ConnectionSettings.WithSecrets(Wpa(), secrets);
        var edited = ConnectionSettings.WithPriority(merged, 10);

        Assert.Equal("secret pass", edited["802-11-wireless-security"]["psk"].GetString());
        Assert.Equal("wpa-psk", edited["802-11-wireless-security"]["key-mgmt"].GetString());
        Assert.Equal(10, edited["connection"]["autoconnect-priority"].GetInt32());
    }

    [Fact]
    public void EditsAreCopiesAndLeaveTheOriginalAlone()
    {
        var original = Wpa();
        var edited = ConnectionSettings.WithAutoConnect(original, false);
        Assert.False(edited["connection"]["autoconnect"].GetBool());
        Assert.False(original["connection"].ContainsKey("autoconnect"));
    }

    [Fact]
    public void PriorityIsClampedToWhatNetworkManagerTakes()
    {
        Assert.Equal(999, ConnectionSettings.WithPriority(Wpa(), 5000)["connection"]["autoconnect-priority"].GetInt32());
        Assert.Equal(-999, ConnectionSettings.WithPriority(Wpa(), -5000)["connection"]["autoconnect-priority"].GetInt32());
    }

    [Fact]
    public void ANewPasswordGoesWhereTheOldOneWas()
    {
        var edited = ConnectionSettings.WithPassword(Wpa(pskFlags: 1), NetworkSecurity.WpaPsk, "new pass");
        Assert.Equal("new pass", edited["802-11-wireless-security"]["psk"].GetString());
        Assert.Equal(1u, edited["802-11-wireless-security"]["psk-flags"].GetUInt32());
        Assert.Throws<InvalidOperationException>(() => ConnectionSettings.WithPassword(Wpa(), NetworkSecurity.None, "x"));
    }

    [Fact]
    public void SharingAndUnsharingMoveThePasswordWithThePermission()
    {
        var mine = ConnectionSettings.ForUser(Wpa(), NetworkSecurity.WpaPsk, "alice");
        Assert.Equal(new[] { "user:alice:" }, mine["connection"]["permissions"].GetArray<string>());
        Assert.Equal(1u, mine["802-11-wireless-security"]["psk-flags"].GetUInt32());

        var shared = ConnectionSettings.ForEveryone(mine, NetworkSecurity.WpaPsk);
        Assert.Empty(shared["connection"]["permissions"].GetArray<string>());
        Assert.Equal(0u, shared["802-11-wireless-security"]["psk-flags"].GetUInt32());
    }

    [Fact]
    public void AnOpenNetworkChangesHandsWithoutASecuritySection()
    {
        var open = Wpa();
        open.Remove("802-11-wireless-security");
        var mine = ConnectionSettings.ForUser(open, NetworkSecurity.None, "alice");
        Assert.False(mine.ContainsKey("802-11-wireless-security"));
    }
}
