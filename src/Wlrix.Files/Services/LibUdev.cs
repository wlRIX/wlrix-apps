using System.Runtime.InteropServices;

namespace Wlrix.Files.Services;

// The slice of libudev that finding phones needs: enumerate what is plugged in, then watch the
// kernel's uevents for what is plugged in and taken out. udev rather than polling libmtp's own
// detection, which probes every USB device on the bus each time it is asked and is too slow to
// run on a timer; libmtp's udev rules have already done that work once per device and left the
// answer in ID_MTP_DEVICE.

internal static unsafe partial class LibUdev
{
    private const string Lib = "libudev.so.1";

    [LibraryImport(Lib, EntryPoint = "udev_new")]
    public static partial nint New();

    [LibraryImport(Lib, EntryPoint = "udev_unref")]
    public static partial nint Unref(nint udev);

    [LibraryImport(Lib, EntryPoint = "udev_monitor_new_from_netlink", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint MonitorNewFromNetlink(nint udev, string name);

    [LibraryImport(Lib, EntryPoint = "udev_monitor_filter_add_match_subsystem_devtype", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int MonitorFilterAddMatchSubsystemDevtype(nint monitor, string subsystem, string devtype);

    [LibraryImport(Lib, EntryPoint = "udev_monitor_enable_receiving")]
    public static partial int MonitorEnableReceiving(nint monitor);

    [LibraryImport(Lib, EntryPoint = "udev_monitor_get_fd")]
    public static partial int MonitorGetFd(nint monitor);

    [LibraryImport(Lib, EntryPoint = "udev_monitor_receive_device")]
    public static partial nint MonitorReceiveDevice(nint monitor);

    [LibraryImport(Lib, EntryPoint = "udev_monitor_unref")]
    public static partial nint MonitorUnref(nint monitor);

    [LibraryImport(Lib, EntryPoint = "udev_enumerate_new")]
    public static partial nint EnumerateNew(nint udev);

    [LibraryImport(Lib, EntryPoint = "udev_enumerate_add_match_subsystem", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int EnumerateAddMatchSubsystem(nint enumerate, string subsystem);

    [LibraryImport(Lib, EntryPoint = "udev_enumerate_add_match_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int EnumerateAddMatchProperty(nint enumerate, string property, string value);

    [LibraryImport(Lib, EntryPoint = "udev_enumerate_scan_devices")]
    public static partial int EnumerateScanDevices(nint enumerate);

    [LibraryImport(Lib, EntryPoint = "udev_enumerate_get_list_entry")]
    public static partial nint EnumerateGetListEntry(nint enumerate);

    [LibraryImport(Lib, EntryPoint = "udev_enumerate_unref")]
    public static partial nint EnumerateUnref(nint enumerate);

    [LibraryImport(Lib, EntryPoint = "udev_list_entry_get_next")]
    public static partial nint ListEntryGetNext(nint entry);

    [LibraryImport(Lib, EntryPoint = "udev_list_entry_get_name")]
    public static partial nint ListEntryGetName(nint entry);

    [LibraryImport(Lib, EntryPoint = "udev_device_new_from_syspath", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint DeviceNewFromSyspath(nint udev, string syspath);

    [LibraryImport(Lib, EntryPoint = "udev_device_get_action")]
    public static partial nint DeviceGetAction(nint device);

    [LibraryImport(Lib, EntryPoint = "udev_device_get_syspath")]
    public static partial nint DeviceGetSyspath(nint device);

    [LibraryImport(Lib, EntryPoint = "udev_device_get_property_value", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint DeviceGetPropertyValue(nint device, string key);

    [LibraryImport(Lib, EntryPoint = "udev_device_unref")]
    public static partial nint DeviceUnref(nint device);

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    public const short PollIn = 0x001;

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    public static partial int Poll(PollFd* fds, nuint count, int timeoutMs);

    public static string? Text(nint utf8) => utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);

    public static string? Property(nint device, string key) => Text(DeviceGetPropertyValue(device, key));
}
