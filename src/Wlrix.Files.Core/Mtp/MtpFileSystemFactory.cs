using System.Text;

namespace Wlrix.Files.Core.Mtp;

/// <summary>Where a device is plugged in, as libusb and udev number it.</summary>
public readonly record struct MtpAddress(int Bus, int DeviceNumber);

/// <summary>Which devices are connected now, by the id their locations carry.</summary>
/// <remarks>
/// The factory's view of whatever is watching for hardware. A location only names a device; the
/// bus address it is reachable at changes every time it is plugged in, so it is looked up at the
/// moment of opening rather than written into the location.
/// </remarks>
public interface IMtpDeviceDirectory
{
    bool TryFind(string deviceId, out MtpAddress address);
}

/// <summary>Opens <c>mtp://</c> locations.</summary>
/// <remarks>
/// Separate from <c>RemoteFileSystemFactory</c> because none of what that does applies: there
/// is no server to connect to, no credentials to ask for, and no pool of sessions to keep.
/// </remarks>
public sealed class MtpFileSystemFactory(IMtpDeviceDirectory directory) : IFileSystemFactory
{
    public const string MtpScheme = "mtp";

    public string Scheme => MtpScheme;

    public Task<IFileSystem> CreateAsync(Location location, CancellationToken cancellationToken)
    {
        if (location.Host is not { } id || !directory.TryFind(id, out var address))
            throw new FileOperationException(FileErrorKind.NotFound, location, "the device is not connected");
        return Open();

        async Task<IFileSystem> Open() =>
            await MtpFileSystem.OpenAsync(
                location, () => LibMtpDevice.Open(address.Bus, address.DeviceNumber), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The id a device's locations carry.</summary>
    /// <remarks>
    /// The serial number when there is one, so a phone keeps its address across being unplugged
    /// and so bookmarks and the session file still point at it. It becomes a URI host, and the
    /// rules for those are strict, so it is reduced to lowercase letters, digits and hyphens. A
    /// device with no serial gets its bus position instead, which is stable only while it stays
    /// in the same port and is the best there is.
    /// </remarks>
    public static string DeviceId(string? serial, int bus, int deviceNumber)
    {
        var id = new StringBuilder();
        foreach (var c in (serial ?? string.Empty).ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
                id.Append(c);
            else if (id.Length > 0 && id[^1] != '-')
                id.Append('-');
        }
        while (id.Length > 0 && id[^1] == '-')
            id.Length--;

        return id.Length > 0 ? id.ToString() : $"usb-{bus}-{deviceNumber}";
    }

    /// <summary>The root of a device's locations.</summary>
    public static Location RootOf(string deviceId) => Location.Parse($"{MtpScheme}://{deviceId}/");
}
