using Wlrix.Settings.Network.Services;
using Xunit;

namespace Wlrix.Settings.Network.Tests;

/// <summary>
/// The edits, against a real NetworkManager. Run only when <c>WLRIX_NM_INTEGRATION</c> names a
/// saved system-wide WPA connection made for the purpose -- they change it -- and otherwise pass
/// without doing anything, so the suite still runs on a build machine with no NetworkManager.
/// </summary>
/// <remarks>
/// What they prove is the one thing a unit test cannot: that NetworkManager keeps the password
/// through an <c>Update</c> of something else, given the secrets merged back in first.
/// </remarks>
public class NetworkManagerIntegrationTests
{
    private static string? Target => Environment.GetEnvironmentVariable("WLRIX_NM_INTEGRATION");

    private static async Task<(NetworkManagerClient Client, KnownNetwork Network)?> OpenAsync()
    {
        if (Target is not { Length: > 0 } name)
            return null;
        var client = await NetworkManagerClient.ConnectAsync();
        var known = await client.ReadKnownAsync();
        var network = known.SingleOrDefault(n => n.Name == name)
            ?? throw new Xunit.Sdk.XunitException($"no saved connection called {name}");
        return (client, network);
    }

    private static string Nmcli(string fields, string name)
    {
        var info = new System.Diagnostics.ProcessStartInfo("nmcli", ["-s", "-g", fields, "connection", "show", name])
        {
            RedirectStandardOutput = true,
        };
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }

    [Fact]
    public async Task ChangingThePriorityKeepsThePassword()
    {
        if (await OpenAsync() is not var (client, network))
            return;
        using (client)
        {
            var before = Nmcli("802-11-wireless-security.psk", network.Name);
            Assert.NotEmpty(before);
            await client.EditAsync(network, settings => ConnectionSettings.WithPriority(settings, 7));
            Assert.Equal("7", Nmcli("connection.autoconnect-priority", network.Name));
            Assert.Equal(before, Nmcli("802-11-wireless-security.psk", network.Name));
            await client.EditAsync(network, settings => ConnectionSettings.WithPriority(settings, 0));
        }
    }

    [Fact]
    public async Task AutoConnectTurnsOffAndOnAgain()
    {
        if (await OpenAsync() is not var (client, network))
            return;
        using (client)
        {
            await client.EditAsync(network, settings => ConnectionSettings.WithAutoConnect(settings, true));
            Assert.Equal("yes", Nmcli("connection.autoconnect", network.Name));
            await client.EditAsync(network, settings => ConnectionSettings.WithAutoConnect(settings, false));
            Assert.Equal("no", Nmcli("connection.autoconnect", network.Name));
        }
    }

    [Fact]
    public async Task ANewPasswordReplacesTheOld()
    {
        if (await OpenAsync() is not var (client, network))
            return;
        using (client)
        {
            var before = Nmcli("802-11-wireless-security.psk", network.Name);
            await client.SetPasswordAsync(network, "replaced123");
            Assert.Equal("replaced123", Nmcli("802-11-wireless-security.psk", network.Name));
            await client.SetPasswordAsync(network, before);
        }
    }
}
