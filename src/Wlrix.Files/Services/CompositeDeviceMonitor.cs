using Wlrix.Files.Core.Platform;

namespace Wlrix.Files.Services;

/// <summary>
/// The rail's one list of devices, from the two places devices come from.
/// </summary>
/// <remarks>
/// Disks are udisks2's and phones are udev's, and neither knows about the other. The windows
/// only want one list, so this merges them and sends each request back to the source that owns
/// the device it is about.
/// </remarks>
public sealed class CompositeDeviceMonitor : IStorageDeviceMonitor
{
    private readonly UDisks2Devices _disks;
    private readonly MtpDevices _phones;

    public CompositeDeviceMonitor(UDisks2Devices disks, MtpDevices phones)
    {
        _disks = disks;
        _phones = phones;
        disks.Changed += () => Changed?.Invoke();
        phones.Changed += () => Changed?.Invoke();
    }

    /// <summary>Disks first, then phones, which is the order they were always shown in.</summary>
    public IReadOnlyList<StorageDevice> Devices => [.. _disks.Devices, .. _phones.Devices];

    public event Action? Changed;

    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(_disks.StartAsync(cancellationToken), _phones.StartAsync(cancellationToken));

    public Task<(string? MountPoint, string? Problem)> MountAsync(StorageDevice device) =>
        Owner(device).MountAsync(device);

    public Task<string?> UnmountAsync(StorageDevice device) =>
        Owner(device).UnmountAsync(device);

    private IStorageDeviceMonitor Owner(StorageDevice device) =>
        device.Kind == StorageDeviceKind.Phone ? _phones : _disks;
}
