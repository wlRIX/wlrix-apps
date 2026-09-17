using ReactiveUI;
using Wlrix.Files.Core;
using Wlrix.Files.Core.Platform;
using Wlrix.Files.Localization;

namespace Wlrix.Files.ViewModels;

/// <summary>One disk in the sidebar.</summary>
/// <remarks>
/// The free space is read here rather than in the monitor because it is a different question
/// with a different answer rate: which disks exist changes when hardware is plugged in, and how
/// full they are changes constantly. Reading it on each refresh of the rail is often enough for
/// a number nobody watches move.
/// </remarks>
public sealed class DeviceViewModel : ReactiveObject
{
    private string _detail = string.Empty;

    public DeviceViewModel(StorageDevice device)
    {
        Device = device;
        _detail = Describe(device);
    }

    public StorageDevice Device { get; }

    public string Label => Device.Label;

    public bool IsMounted => Device.IsMounted;

    /// <summary>Where it is, or null while it is not mounted.</summary>
    public Location? Location => Device.Location;

    /// <summary>The second line: how full it is, or what it is when it is not mounted.</summary>
    public string Detail
    {
        get => _detail;
        private set => this.RaiseAndSetIfChanged(ref _detail, value);
    }

    /// <summary>The label, for a menu that shows these without a template.</summary>
    public override string ToString() => Label;

    /// <summary>
    /// Reads how full the disk is, off the UI thread.
    /// </summary>
    /// <remarks>
    /// <c>statvfs</c> on a local block device is fast, but "local" is the caller's assumption
    /// rather than a guarantee — and a stale mount can make it block for as long as the kernel
    /// takes to give up. Off the thread pool, and a failure leaves the size line as it was.
    /// </remarks>
    public async Task RefreshUsageAsync()
    {
        if (Device.MountPoint is not { } mountPoint)
            return;

        var text = await Task.Run(() =>
        {
            try
            {
                var drive = new DriveInfo(mountPoint);
                return drive.TotalSize > 0
                    ? Strings.DeviceFree(
                        StorageDevices.FormatSize(drive.AvailableFreeSpace),
                        StorageDevices.FormatSize(drive.TotalSize))
                    : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }).ConfigureAwait(true);

        if (text is not null)
            Detail = text;
    }

    /// <summary>What the second line says before the disk has been measured.</summary>
    private static string Describe(StorageDevice device) =>
        device.IsMounted
            ? device.MountPoint!
            : device.SizeBytes > 0
                ? $"{device.Device} · {StorageDevices.FormatSize(device.SizeBytes)}"
                : device.Device;
}
