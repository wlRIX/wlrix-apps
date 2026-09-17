using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;
using Wlrix.Files.Core.Platform;
using Wlrix.Files.DBus;
using ZLogger;

namespace Wlrix.Files.Services;

/// <summary>The disks the sidebar shows, and mounting one that is not mounted.</summary>
public interface IStorageDeviceMonitor
{
    /// <summary>What is attached now.</summary>
    IReadOnlyList<StorageDevice> Devices { get; }

    /// <summary>Raised when a disk appears, disappears, or is mounted or unmounted.</summary>
    event Action? Changed;

    /// <summary>Reads the list and starts watching. Never throws.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Mounts a device, answering where it landed, or null with a reason.</summary>
    Task<(string? MountPoint, string? Problem)> MountAsync(StorageDevice device);

    /// <summary>Unmounts a device, answering null or a reason it could not be.</summary>
    Task<string?> UnmountAsync(StorageDevice device);
}

/// <summary>
/// The devices rail, over udisks2 on the system bus.
/// </summary>
/// <remarks>
/// udisks2 rather than <c>/proc/self/mountinfo</c>, because mountinfo can only describe what is
/// already mounted: it cannot see a stick that has been plugged in and not yet mounted, cannot
/// say whether a disk is removable, and offers no way to mount anything. What it can do is
/// still worth having when the daemon is absent, and
/// <see cref="StorageDevices.FromMounts"/> is that fallback.
///
/// <para>
/// Mounting needs no <c>pkexec</c> and no helper. udisks2's own polkit actions
/// (<c>org.freedesktop.udisks2.filesystem-mount</c> and the <c>-system</c> variant) allow an
/// active local session, so an ordinary user mounting their own stick is already authorized and
/// nothing prompts. The call is made interactively, which costs nothing when it is going to be
/// allowed anyway and is what lets polkit answer rather than refuse outright when it is not.
/// </para>
///
/// <para>
/// Everything about <em>which</em> devices matter and what they are called is in
/// <see cref="StorageDevices"/>, in Core, where it can be argued with in a test. This part only
/// reports facts.
/// </para>
/// </remarks>
public sealed class UDisks2Devices(ILogger<UDisks2Devices> logger) : IStorageDeviceMonitor, IDisposable
{
    private const string BusName = "org.freedesktop.UDisks2";
    private const string RootPath = "/org/freedesktop/UDisks2";
    private const string BlockInterface = "org.freedesktop.UDisks2.Block";
    private const string FilesystemInterface = "org.freedesktop.UDisks2.Filesystem";
    private const string DriveInterface = "org.freedesktop.UDisks2.Drive";

    private readonly List<IDisposable> _watches = [];
    private DBusConnection? _connection;
    private ObjectManager? _objects;

    /// <inheritdoc/>
    public IReadOnlyList<StorageDevice> Devices { get; private set; } = [];

    /// <inheritdoc/>
    public event Action? Changed;

    /// <inheritdoc/>
    /// <remarks>
    /// A machine with no udisks2 — a container, or a session where the daemon is not running —
    /// is not an error. The rail falls back to what the mount table can say, which is the disks
    /// that are already mounted and nothing about removable media.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var address = DBusAddress.System
                ?? throw new InvalidOperationException("no system bus address");
            _connection = new DBusConnection(new DBusConnectionOptions(address) { AutoConnect = false });
            await _connection.ConnectAsync().ConfigureAwait(false);

            _objects = new ObjectManager(_connection, BusName, RootPath);
            _watches.Add(await _objects.WatchInterfacesAddedAsync(OnObjectsChanged).ConfigureAwait(false));
            _watches.Add(await _objects.WatchInterfacesRemovedAsync(OnObjectsChanged).ConfigureAwait(false));

            await RefreshAsync().ConfigureAwait(false);
        }
        // DBusExceptionBase, not the error-reply leaf: a system bus that cannot be reached
        // throws DBusConnectFailedException, which is a connection failure rather than a reply
        // and would otherwise escape a method whose entire point is degrading gracefully.
        catch (Exception ex) when (ex is DBusExceptionBase or SocketException or IOException
                                       or InvalidOperationException or ObjectDisposedException)
        {
            logger.ZLogInformation($"udisks2 unavailable ({ex.GetType().Name}); listing mounted disks only");
            Devices = StorageDevices.FromMounts(MountTable.Read());
            Changed?.Invoke();
        }
    }

    /// <summary>Something was plugged in or taken away.</summary>
    /// <remarks>
    /// The signal carries the new object's properties, but a refresh is read wholesale anyway:
    /// a device appearing usually means several objects appearing together — a drive, a block
    /// device and a partition — and answering each one separately would rebuild the rail three
    /// times for one stick.
    /// </remarks>
    private void OnObjectsChanged<T>(T signal) => _ = RefreshAsync();

    /// <summary>A device was mounted or unmounted, possibly by something else entirely.</summary>
    private void OnFilesystemChanged(IChangedFilesystemProperties changed) => _ = RefreshAsync();

    /// <inheritdoc/>
    public async Task<(string? MountPoint, string? Problem)> MountAsync(StorageDevice device)
    {
        if (_connection is null)
            return (null, null);

        try
        {
            var filesystem = new Filesystem(_connection, BusName, new ObjectPath(device.Id));
            var mountPoint = await filesystem.MountAsync([]).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
            return (mountPoint, null);
        }
        catch (DBusErrorReplyException ex)
        {
            logger.ZLogWarning($"could not mount {device.Device}: {ex.ErrorName}");
            return (null, Describe(ex));
        }
        catch (Exception ex) when (ex is DBusExceptionBase or SocketException or ObjectDisposedException)
        {
            // Not a refusal but the daemon going away mid-call. Nothing to quote back to the
            // user, and crashing the window over a disk that did not mount would be worse.
            logger.ZLogWarning($"could not reach udisks2 to mount {device.Device}: {ex.GetType().Name}");
            return (null, null);
        }
    }

    /// <inheritdoc/>
    public async Task<string?> UnmountAsync(StorageDevice device)
    {
        if (_connection is null)
            return null;

        try
        {
            var filesystem = new Filesystem(_connection, BusName, new ObjectPath(device.Id));
            await filesystem.UnmountAsync([]).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
            return null;
        }
        catch (DBusErrorReplyException ex)
        {
            logger.ZLogWarning($"could not unmount {device.Device}: {ex.ErrorName}");
            return Describe(ex);
        }
        catch (Exception ex) when (ex is DBusExceptionBase or SocketException or ObjectDisposedException)
        {
            logger.ZLogWarning($"could not reach udisks2 to unmount {device.Device}: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// What went wrong, in words rather than in a bus error name.
    /// </summary>
    /// <remarks>
    /// udisks puts the useful part in the message — "target is busy", and often which process
    /// is holding it — so the message is what to show. The error name is for the log.
    /// </remarks>
    private static string Describe(DBusErrorReplyException ex) =>
        string.IsNullOrWhiteSpace(ex.ErrorMessage) ? ex.ErrorName : ex.ErrorMessage;

    /// <summary>
    /// Re-reads every object in one call, rather than asking each one for its properties.
    /// </summary>
    /// <remarks>
    /// <c>GetManagedObjects</c> answers the whole tree with every interface and every property
    /// already filled in, so a rail of a dozen disks costs one round trip rather than fifty.
    /// The per-object property watches are rebuilt from scratch each time, which is the simple
    /// arrangement that cannot leak a subscription to a device that has been unplugged.
    /// </remarks>
    private async Task RefreshAsync()
    {
        if (_objects is null || _connection is null)
            return;

        try
        {
            var managed = await _objects.GetManagedObjectsAsync().ConfigureAwait(false);
            var devices = new List<StorageDevice>();

            foreach (var (path, interfaces) in managed)
            {
                if (!interfaces.TryGetValue(BlockInterface, out var block))
                    continue;

                interfaces.TryGetValue(FilesystemInterface, out var filesystem);
                var drive = DriveOf(managed, block);

                if (StorageDevices.Describe(FactsFrom(path, block, filesystem, drive)) is { } device)
                    devices.Add(device);
            }

            devices.Sort(static (a, b) => Order(a).CompareTo(Order(b)) is var byKind and not 0
                ? byKind
                : string.CompareOrdinal(a.MountPoint ?? a.Label, b.MountPoint ?? b.Label));

            Devices = devices;
            await WatchFilesystemsAsync(managed).ConfigureAwait(false);
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is DBusExceptionBase or SocketException or IOException or ObjectDisposedException)
        {
            logger.ZLogWarning($"could not read the disks: {ex.Message}");
        }
    }

    /// <summary>Fixed disks first, removable ones under them.</summary>
    /// <remarks>
    /// The fixed ones are the same every day and are found by position; a stick is the thing
    /// that just appeared and is found by being new. Interleaving them alphabetically would
    /// move the familiar rows around every time something is plugged in.
    /// </remarks>
    private static int Order(StorageDevice device) => device.IsRemovable ? 1 : 0;

    /// <summary>
    /// Notices a mount or unmount performed by something other than this window.
    /// </summary>
    /// <remarks>
    /// A terminal running <c>umount</c>, or another file manager. Without this the rail would
    /// keep offering to open a directory that is no longer mounted, and only notice when
    /// something unrelated forced a refresh.
    /// </remarks>
    private async Task WatchFilesystemsAsync(
        Dictionary<ObjectPath, Dictionary<string, Dictionary<string, VariantValue>>> managed)
    {
        foreach (var watch in _watches.Skip(2))
            watch.Dispose();
        _watches.RemoveRange(2, _watches.Count - 2);

        foreach (var (path, interfaces) in managed)
        {
            if (!interfaces.ContainsKey(FilesystemInterface))
                continue;

            var filesystem = new Filesystem(_connection!, BusName, path);
            _watches.Add(await filesystem
                .WatchPropertiesChangedAsync(OnFilesystemChanged)
                .ConfigureAwait(false));
        }
    }

    private static Dictionary<string, VariantValue>? DriveOf(
        Dictionary<ObjectPath, Dictionary<string, Dictionary<string, VariantValue>>> managed,
        Dictionary<string, VariantValue> block)
    {
        if (!block.TryGetValue("Drive", out var drive))
            return null;
        return managed.TryGetValue(drive.GetObjectPath(), out var interfaces)
               && interfaces.TryGetValue(DriveInterface, out var properties)
            ? properties
            : null;
    }

    private static BlockDeviceFacts FactsFrom(
        ObjectPath path,
        Dictionary<string, VariantValue> block,
        Dictionary<string, VariantValue>? filesystem,
        Dictionary<string, VariantValue>? drive) =>
        new(
            Id: path.ToString(),
            Device: NulTerminated(block, "Device"),
            Label: Text(block, "IdLabel"),
            FilesystemType: Text(block, "IdType"),
            Usage: Text(block, "IdUsage"),
            SizeBytes: (long)Number(block, "Size"),
            MountPoints: MountPoints(filesystem),
            IsRemovable: drive is not null && Flag(drive, "Removable"),
            IsReadOnly: Flag(block, "ReadOnly"),
            Ignore: Flag(block, "HintIgnore"),
            DriveName: DriveName(drive));

    private static string DriveName(Dictionary<string, VariantValue>? drive)
    {
        if (drive is null)
            return string.Empty;
        var vendor = Text(drive, "Vendor");
        var model = Text(drive, "Model");
        return string.IsNullOrWhiteSpace(vendor) ? model : $"{vendor} {model}".Trim();
    }

    /// <summary>
    /// The mount points, which arrive as an array of NUL-terminated byte arrays.
    /// </summary>
    /// <remarks>
    /// Bytes rather than strings because a mount point is a path and a path is not text: it can
    /// hold any byte but NUL and the separator, and udisks refuses to pretend otherwise. Decoded
    /// as UTF-8 here because everything above this is <c>string</c>, and a path that is not
    /// valid UTF-8 is a problem the whole application has, not one this method can fix.
    /// </remarks>
    private static IReadOnlyList<string> MountPoints(Dictionary<string, VariantValue>? filesystem)
    {
        if (filesystem is null || !filesystem.TryGetValue("MountPoints", out var value))
            return [];

        var points = new List<string>();
        for (var i = 0; i < value.Count; i++)
        {
            var bytes = value.GetItem(i).GetArray<byte>();
            var end = Array.IndexOf(bytes, (byte)0);
            points.Add(System.Text.Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end));
        }

        return points;
    }

    private static string NulTerminated(Dictionary<string, VariantValue> properties, string name)
    {
        if (!properties.TryGetValue(name, out var value))
            return string.Empty;
        var bytes = value.GetArray<byte>();
        var end = Array.IndexOf(bytes, (byte)0);
        return System.Text.Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end);
    }

    private static string Text(Dictionary<string, VariantValue> properties, string name) =>
        properties.TryGetValue(name, out var value) ? value.GetString() : string.Empty;

    private static ulong Number(Dictionary<string, VariantValue> properties, string name) =>
        properties.TryGetValue(name, out var value) ? value.GetUInt64() : 0;

    private static bool Flag(Dictionary<string, VariantValue> properties, string name) =>
        properties.TryGetValue(name, out var value) && value.GetBool();

    public void Dispose()
    {
        foreach (var watch in _watches)
            watch.Dispose();
        _watches.Clear();
        _connection?.Dispose();
    }
}
