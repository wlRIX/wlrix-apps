using Tmds.DBus.Protocol;
using Wlrix.Settings.Network.Services;
using Wlrix.Settings.Network.ViewModels;
using Xunit;

namespace Wlrix.Settings.Network.Tests;

public class ServicesTests
{
    [Fact]
    public void DeviceStatesFoldIntoWhatIsShown()
    {
        Assert.Equal(DeviceStateKind.Unmanaged, NetworkManagerClient.StateOf(10));
        Assert.Equal(DeviceStateKind.Disconnected, NetworkManagerClient.StateOf(30));
        Assert.Equal(DeviceStateKind.Connecting, NetworkManagerClient.StateOf(70));
        Assert.Equal(DeviceStateKind.Connected, NetworkManagerClient.StateOf(100));
        Assert.Equal(DeviceStateKind.Failed, NetworkManagerClient.StateOf(120));
        Assert.Equal(DeviceStateKind.Unavailable, NetworkManagerClient.StateOf(999));
    }

    [Fact]
    public void TheFirstAddressIsShownWithItsPrefix()
    {
        var data = new[]
        {
            new Dictionary<string, VariantValue>
            {
                ["address"] = VariantValue.String("192.168.1.20"),
                ["prefix"] = VariantValue.UInt32(24),
            },
        };
        Assert.Equal("192.168.1.20/24", NetworkManagerClient.FirstAddress(data));
        Assert.Null(NetworkManagerClient.FirstAddress([]));
    }

    [Fact]
    public void AStatusRowJoinsTheConnectionAndTheAddress()
    {
        var row = NetworkSettingsViewModel.Row(new DeviceStatus("wlan0", true, DeviceStateKind.Connected, "HomeNet", "10.0.0.5/24"));
        Assert.Equal("wlan0", row.Interface);
        Assert.Equal("HomeNet  10.0.0.5/24", row.Detail);
        var idle = NetworkSettingsViewModel.Row(new DeviceStatus("eth0", false, DeviceStateKind.Disconnected, null, null));
        Assert.Equal(string.Empty, idle.Detail);
    }

    [Fact]
    public void ThePromptIsAskedForTheRightNetworkAndItsAnswerRead()
    {
        using var request = System.Text.Json.JsonDocument.Parse(PasswordPrompt.Request("Café", NetworkSecurity.Sae));
        Assert.Equal("password", request.RootElement.GetProperty("mode").GetString());
        Assert.Equal("Café", request.RootElement.GetProperty("ssid").GetString());
        Assert.Equal("sae", request.RootElement.GetProperty("security").GetString());

        Assert.Equal("pw", PasswordPrompt.Password("""{"ssid":"x","security":"wpa-psk","password":"pw"}"""));
        Assert.Null(PasswordPrompt.Password(""));
        Assert.Null(PasswordPrompt.Password("garbage"));
    }
}
