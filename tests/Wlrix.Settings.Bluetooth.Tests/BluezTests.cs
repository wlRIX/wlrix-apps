using Tmds.DBus.Protocol;
using Wlrix.Settings.Bluetooth.Services;
using Wlrix.Settings.Bluetooth.ViewModels;
using Xunit;

namespace Wlrix.Settings.Bluetooth.Tests;

public class BluezTests
{
    private static Dictionary<string, Dictionary<string, VariantValue>> Device(string alias, bool paired, bool connected, bool named = true)
    {
        var properties = new Dictionary<string, VariantValue>
        {
            ["Adapter"] = VariantValue.ObjectPath(new ObjectPath("/org/bluez/hci0")),
            ["Address"] = VariantValue.String("AA:BB:CC:DD:EE:FF"),
            ["Alias"] = VariantValue.String(alias),
            ["Paired"] = VariantValue.Bool(paired),
            ["Connected"] = VariantValue.Bool(connected),
        };
        if (named)
            properties["Name"] = VariantValue.String(alias);
        return new() { ["org.bluez.Device1"] = properties };
    }

    private static Dictionary<string, Dictionary<string, Dictionary<string, VariantValue>>> Laptop()
    {
        var objects = new Dictionary<string, Dictionary<string, Dictionary<string, VariantValue>>>
        {
            ["/org/bluez/hci0"] = new()
            {
                ["org.bluez.Adapter1"] = new()
                {
                    ["Alias"] = VariantValue.String("laptop"),
                    ["Powered"] = VariantValue.Bool(true),
                    ["PowerState"] = VariantValue.String("on"),
                    ["Discovering"] = VariantValue.Bool(true),
                },
            },
            ["/org/bluez/hci0/dev_1"] = Device("zebra speaker", false, false),
            ["/org/bluez/hci0/dev_2"] = Device("Headset", true, true),
            ["/org/bluez/hci0/dev_3"] = Device("AA-BB-CC-DD-EE-FF", false, false, named: false),
        };
        objects["/org/bluez/hci0/dev_2"]["org.bluez.Battery1"] = new() { ["Percentage"] = VariantValue.Byte(80) };
        return objects;
    }

    [Fact]
    public void TheAdapterAndItsDevicesAreRead()
    {
        var snapshot = BluezClient.Parse(Laptop());
        Assert.Equal("laptop", snapshot.Adapter!.Name);
        Assert.True(snapshot.Adapter.Powered && snapshot.Adapter.Discovering && !snapshot.Adapter.Blocked);
        // Paired first, then by name.
        Assert.Equal(["Headset", "AA-BB-CC-DD-EE-FF", "zebra speaker"], snapshot.Devices.Select(d => d.Name));
        Assert.Equal((byte)80, snapshot.Devices[0].Battery);
        Assert.False(snapshot.Devices[1].HasName, "a beacon with only an address");
    }

    [Fact]
    public void NoAdapterIsAnEmptySnapshot() =>
        Assert.Null(BluezClient.Parse(new Dictionary<string, Dictionary<string, Dictionary<string, VariantValue>>>()).Adapter);

    [Fact]
    public void ARowSaysWhatTheDeviceIsDoing()
    {
        var snapshot = BluezClient.Parse(Laptop());
        var headset = DeviceRow.From(snapshot.Devices[0]);
        Assert.Contains("80", headset.State);
        Assert.True(headset.Connected);
    }

    [Fact]
    public void OnlyABluetoothSwitchThatIsHardBlockedCounts()
    {
        var root = Directory.CreateTempSubdirectory("wlrix-bt-rfkill-");
        try
        {
            foreach (var (name, kind, hard) in new[] { ("rfkill0", "wlan", "1"), ("rfkill1", "bluetooth", "0") })
            {
                var dir = Directory.CreateDirectory(Path.Combine(root.FullName, name));
                File.WriteAllText(Path.Combine(dir.FullName, "type"), kind + "\n");
                File.WriteAllText(Path.Combine(dir.FullName, "hard"), hard + "\n");
            }
            Assert.False(Rfkill.HardBlocked(root.FullName));
            File.WriteAllText(Path.Combine(root.FullName, "rfkill1", "hard"), "1\n");
            Assert.True(Rfkill.HardBlocked(root.FullName));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
