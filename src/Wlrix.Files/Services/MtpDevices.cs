using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Wlrix.Files.Core.Mtp;
using Wlrix.Files.Core.Platform;
using ZLogger;

namespace Wlrix.Files.Services;

/// <summary>
/// Phones and players plugged in over USB in MTP mode, for the devices rail.
/// </summary>
/// <remarks>
/// A phone in "File Transfer" mode is not a disk, so udisks2 never hears of it. udev does: the
/// rules libmtp installs tag every MTP device with <c>ID_MTP_DEVICE=1</c> as it appears, and the
/// <c>uaccess</c> tag gives the person at the seat read and write on its node, which is what
/// lets libmtp open it without root.
///
/// <para>
/// Nothing here opens a device. A phone in the rail is only a row until someone clicks it; the
/// USB interface is claimed then, by <see cref="MtpFileSystemFactory"/>, because claiming it
/// shuts every other program out and doing that to a phone merely for being plugged in would
/// be rude to whatever else wanted it.
/// </para>
/// </remarks>
public sealed class MtpDevices(ILogger<MtpDevices> logger) : IStorageDeviceMonitor, IMtpDeviceDirectory, IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Phone> _phones = new(StringComparer.Ordinal);
    private Thread? _thread;
    private volatile bool _stopping;

    /// <summary>One plugged-in device, keyed by its kernel path so a removal can find it.</summary>
    private sealed record Phone(string SysPath, string Id, MtpAddress Address, StorageDevice Device);

    public IReadOnlyList<StorageDevice> Devices
    {
        get
        {
            lock (_lock)
                return [.. _phones.Values.Select(phone => phone.Device).OrderBy(device => device.Label, StringComparer.CurrentCulture)];
        }
    }

    public event Action? Changed;

    /// <remarks>
    /// Never throws. A system without libudev -- a container, a BSD -- simply has no phones in
    /// the rail.
    /// </remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var udev = LibUdev.New();
            if (udev == 0)
                return Task.CompletedTask;

            // The monitor is opened before the enumeration so nothing plugged in between the two
            // is missed; something seen by both is just added twice, which is harmless.
            var monitor = LibUdev.MonitorNewFromNetlink(udev, "udev");
            if (monitor != 0)
            {
                LibUdev.MonitorFilterAddMatchSubsystemDevtype(monitor, "usb", "usb_device");
                LibUdev.MonitorEnableReceiving(monitor);
            }

            Enumerate(udev);
            Changed?.Invoke();

            if (monitor != 0)
            {
                _thread = new Thread(() => Watch(udev, monitor)) { IsBackground = true, Name = "mtp hotplug" };
                _thread.Start();
            }
            else
            {
                LibUdev.Unref(udev);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            logger.ZLogInformation($"libudev unavailable ({ex.GetType().Name}); phones will not be listed");
        }
        return Task.CompletedTask;
    }

    private void Enumerate(nint udev)
    {
        var enumerate = LibUdev.EnumerateNew(udev);
        try
        {
            LibUdev.EnumerateAddMatchSubsystem(enumerate, "usb");
            LibUdev.EnumerateAddMatchProperty(enumerate, "ID_MTP_DEVICE", "1");
            LibUdev.EnumerateScanDevices(enumerate);
            for (var entry = LibUdev.EnumerateGetListEntry(enumerate); entry != 0; entry = LibUdev.ListEntryGetNext(entry))
            {
                if (LibUdev.Text(LibUdev.ListEntryGetName(entry)) is not { } sysPath)
                    continue;
                var device = LibUdev.DeviceNewFromSyspath(udev, sysPath);
                if (device == 0)
                    continue;
                try
                {
                    Add(device);
                }
                finally
                {
                    LibUdev.DeviceUnref(device);
                }
            }
        }
        finally
        {
            LibUdev.EnumerateUnref(enumerate);
        }
    }

    /// <summary>Follows plugs and unplugs until disposed.</summary>
    /// <remarks>
    /// A poll with a timeout rather than a blocking receive, so that disposing can end the
    /// thread without closing the descriptor out from under a call that is using it.
    /// </remarks>
    private unsafe void Watch(nint udev, nint monitor)
    {
        try
        {
            var poll = new LibUdev.PollFd { Fd = LibUdev.MonitorGetFd(monitor), Events = LibUdev.PollIn };
            while (!_stopping)
            {
                if (LibUdev.Poll(&poll, 1, 500) <= 0)
                    continue;

                var device = LibUdev.MonitorReceiveDevice(monitor);
                if (device == 0)
                    continue;
                try
                {
                    var changed = LibUdev.Text(LibUdev.DeviceGetAction(device)) switch
                    {
                        "add" => Add(device),
                        "remove" => Remove(device),
                        _ => false
                    };
                    if (changed)
                        Changed?.Invoke();
                }
                finally
                {
                    LibUdev.DeviceUnref(device);
                }
            }
        }
        finally
        {
            LibUdev.MonitorUnref(monitor);
            LibUdev.Unref(udev);
        }
    }

    private bool Add(nint device)
    {
        if (LibUdev.Property(device, "ID_MTP_DEVICE") != "1"
            || LibUdev.Property(device, "DEVTYPE") is not (null or "usb_device")
            || LibUdev.Text(LibUdev.DeviceGetSyspath(device)) is not { } sysPath
            || !int.TryParse(LibUdev.Property(device, "BUSNUM"), NumberStyles.None, CultureInfo.InvariantCulture, out var bus)
            || !int.TryParse(LibUdev.Property(device, "DEVNUM"), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return false;

        var id = MtpFileSystemFactory.DeviceId(LibUdev.Property(device, "ID_SERIAL_SHORT"), bus, number);
        var label = Decode(LibUdev.Property(device, "ID_MODEL_ENC"))
                    ?? LibUdev.Property(device, "ID_MODEL")?.Replace('_', ' ')
                    ?? id;
        var vendor = Decode(LibUdev.Property(device, "ID_VENDOR_ENC"))
                     ?? LibUdev.Property(device, "ID_VENDOR")?.Replace('_', ' ')
                     ?? string.Empty;

        var storage = new StorageDevice(
            Id: "mtp:" + id,
            Label: label,
            MountPoint: null,
            Device: vendor,
            FilesystemType: "mtp",
            SizeBytes: 0,
            IsRemovable: true,
            IsReadOnly: false)
        {
            Kind = StorageDeviceKind.Phone,
            RemoteLocation = MtpFileSystemFactory.RootOf(id)
        };

        lock (_lock)
            _phones[sysPath] = new Phone(sysPath, id, new MtpAddress(bus, number), storage);
        logger.ZLogInformation($"MTP device {label} ({id}) at bus {bus} device {number}");
        return true;
    }

    private bool Remove(nint device)
    {
        if (LibUdev.Text(LibUdev.DeviceGetSyspath(device)) is not { } sysPath)
            return false;
        lock (_lock)
            return _phones.Remove(sysPath);
    }

    public bool TryFind(string deviceId, out MtpAddress address)
    {
        lock (_lock)
        {
            foreach (var phone in _phones.Values)
            {
                if (phone.Id == deviceId)
                {
                    address = phone.Address;
                    return true;
                }
            }
        }
        address = default;
        return false;
    }

    /// <summary>Phones are never mounted: they are opened by clicking them.</summary>
    public Task<(string? MountPoint, string? Problem)> MountAsync(StorageDevice device) =>
        Task.FromResult<(string?, string?)>((null, null));

    /// <summary>Nothing to do here: releasing a phone is closing its filesystem, which the window does.</summary>
    public Task<string?> UnmountAsync(StorageDevice device) => Task.FromResult<string?>(null);

    /// <summary>Undoes udev's <c>\x20</c>-style escaping in the <c>*_ENC</c> properties.</summary>
    internal static string? Decode(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
            return null;

        var bytes = new List<byte>(encoded.Length);
        for (var i = 0; i < encoded.Length; i++)
        {
            if (encoded[i] == '\\' && i + 3 < encoded.Length && encoded[i + 1] == 'x'
                && byte.TryParse(encoded.AsSpan(i + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                bytes.Add(b);
                i += 3;
                continue;
            }
            bytes.AddRange(Encoding.UTF8.GetBytes(encoded[i].ToString()));
        }

        var text = Encoding.UTF8.GetString([.. bytes]).Trim();
        return text.Length > 0 ? text : null;
    }

    public void Dispose()
    {
        _stopping = true;
        _thread?.Join(TimeSpan.FromSeconds(1));
    }
}
