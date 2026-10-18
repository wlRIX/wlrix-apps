using Tmds.DBus.Protocol;
using Wlrix.Settings.Network.DBus.Secrets;

namespace Wlrix.Settings.Network.Services;

/// <summary>Whether a network can be made this user's own, with its password in their keyring.</summary>
/// <remarks>
/// The same test <c>wlrix-network</c> makes when it creates a connection, and <c>Wlrix.Files</c>
/// before it: something answers <c>org.freedesktop.secrets</c> and the default collection is
/// unlocked. Without that, a password moved out of NetworkManager's system profile would have
/// nowhere to go, so "only me" is not offered. Nothing here reads a secret.
/// </remarks>
internal static class Keyring
{
    public static async Task<bool> AvailableAsync()
    {
        var address = DBusAddress.Session;
        if (address is null)
            return false;
        using var connection = new DBusConnection(new DBusConnectionOptions(address) { AutoConnect = false });
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            var service = new Service(connection, "org.freedesktop.secrets", "/org/freedesktop/secrets");
            var collection = await service.ReadAliasAsync("default").ConfigureAwait(false);
            if (collection.ToString() == "/")
                return false;
            var locked = await new Collection(connection, "org.freedesktop.secrets", collection)
                .GetLockedAsync().ConfigureAwait(false);
            return !locked;
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException)
        {
            return false;
        }
    }
}
