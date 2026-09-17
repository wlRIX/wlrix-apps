namespace Wlrix.Files.Core.Platform;

/// <summary>What a block device looks like before any judgement has been applied.</summary>
/// <param name="Id">A stable key. The udisks object path, or the device node.</param>
/// <param name="Device">The device node, e.g. <c>/dev/sdc1</c>.</param>
/// <param name="Label">The filesystem label, often empty.</param>
/// <param name="FilesystemType">As the probe names it: <c>xfs</c>, <c>btrfs</c>, <c>vfat</c>.</param>
/// <param name="Usage">udisks' <c>IdUsage</c>: <c>filesystem</c>, <c>swap</c>, <c>crypto</c>, or empty.</param>
/// <param name="SizeBytes">The partition's size, which is not the same as the space in it.</param>
/// <param name="MountPoints">Everywhere it is mounted. A btrfs device has one per subvolume.</param>
/// <param name="IsRemovable">Whether the drive behind it can be taken out.</param>
/// <param name="IsReadOnly">Whether the device is read-only.</param>
/// <param name="Ignore">udisks' <c>HintIgnore</c>: the system asking for this not to be shown.</param>
/// <param name="DriveName">The drive's model, for a device with nothing better to be called.</param>
public readonly record struct BlockDeviceFacts(
    string Id,
    string Device,
    string Label,
    string FilesystemType,
    string Usage,
    long SizeBytes,
    IReadOnlyList<string> MountPoints,
    bool IsRemovable,
    bool IsReadOnly,
    bool Ignore,
    string DriveName = "");

/// <summary>A disk worth showing in the sidebar.</summary>
/// <param name="Id">The stable key, for matching a device across a refresh.</param>
/// <param name="Label">What the row says.</param>
/// <param name="MountPoint">Where it is, or null when it is not mounted.</param>
/// <param name="Device">The device node, shown as the subtitle of an unmounted device.</param>
/// <param name="FilesystemType">As the probe names it.</param>
/// <param name="SizeBytes">The partition's size.</param>
/// <param name="IsRemovable">Whether it can be taken out, which decides whether Unmount is offered.</param>
/// <param name="IsReadOnly">Whether it is mounted read-only.</param>
public sealed record StorageDevice(
    string Id,
    string Label,
    string? MountPoint,
    string Device,
    string FilesystemType,
    long SizeBytes,
    bool IsRemovable,
    bool IsReadOnly)
{
    public bool IsMounted => MountPoint is not null;

    /// <summary>Where clicking it goes, or null while it is not mounted.</summary>
    public Location? Location => MountPoint is null ? null : Core.Location.FromLocalPath(MountPoint);
}

/// <summary>
/// Which disks belong in the sidebar, and what to call them.
/// </summary>
/// <remarks>
/// Every rule about what a person should see lives here rather than in the udisks client, so
/// that the rules can be argued with in a test instead of against a running daemon holding a
/// particular set of hardware. The client's job is to report facts; this decides what they
/// mean.
/// </remarks>
public static class StorageDevices
{
    /// <summary>
    /// Mount points that are the operating system's own plumbing.
    /// </summary>
    /// <remarks>
    /// <c>/boot</c> is a real filesystem on a real partition and belongs in nobody's sidebar:
    /// it exists for the bootloader, and the one thing a person can do by opening it is break
    /// the machine. Explicit rather than inferred, because there is nothing in the device's
    /// own properties that distinguishes it from a data partition — udisks marks the root
    /// filesystem and every internal disk on this machine <c>HintSystem</c> alike.
    /// </remarks>
    public static readonly IReadOnlySet<string> PlumbingMountPoints = new HashSet<string>(StringComparer.Ordinal)
    {
        "/boot",
        "/boot/efi",
        "/efi"
    };

    /// <summary>
    /// Turns what the system reports into a row, or nothing if it should not be shown.
    /// </summary>
    public static StorageDevice? Describe(BlockDeviceFacts facts)
    {
        // The system asking to be left out. Swap, zram and the loopback devices behind a
        // snap or an appimage all set it.
        if (facts.Ignore)
            return null;

        // Only things a person can open. Swap and an unlocked crypto container's backing
        // device are block devices with no files in them.
        if (!string.Equals(facts.Usage, "filesystem", StringComparison.Ordinal))
            return null;

        var mountPoint = PrimaryMountPoint(facts.MountPoints);
        if (mountPoint is not null && PlumbingMountPoints.Contains(mountPoint))
            return null;

        // An unmounted internal partition is usually a recovery image or another operating
        // system, and offering to mount it is offering to do something nobody asked for.
        // Unmounted *removable* media is the whole reason this list exists.
        if (mountPoint is null && !facts.IsRemovable)
            return null;

        return new StorageDevice(
            facts.Id,
            Label(facts, mountPoint),
            mountPoint,
            facts.Device,
            facts.FilesystemType,
            facts.SizeBytes,
            facts.IsRemovable,
            facts.IsReadOnly);
    }

    /// <summary>
    /// The one mount point that stands for the device.
    /// </summary>
    /// <remarks>
    /// The shortest, which on this machine is the difference between one row and seven: a
    /// btrfs root is mounted once per subvolume — <c>/</c>, <c>/home</c>, <c>/var/log</c> and
    /// the rest — and they are all the same disk. The shortest is the one the others hang
    /// below, and it is the one somebody means by "that disk".
    /// </remarks>
    public static string? PrimaryMountPoint(IReadOnlyList<string> mountPoints)
    {
        string? best = null;
        foreach (var candidate in mountPoints)
        {
            if (candidate.Length == 0)
                continue;
            if (best is null || candidate.Length < best.Length
                || (candidate.Length == best.Length && string.CompareOrdinal(candidate, best) < 0))
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// What to call a device, in descending order of how much it tells you.
    /// </summary>
    /// <remarks>
    /// The filesystem label first, when there is one — it is the only name anybody chose on
    /// purpose. Then the mount point's own last component, which on a machine that mounts its
    /// disks at <c>/mnt/Games</c> is exactly the name their owner thinks of them by, and which
    /// beats the drive model for the same reason a bookmark beats a URL. The root gets a name
    /// of its own because its last component is empty. Then the drive's model, then its size —
    /// the last two being what is left for an unlabeled stick, which is also what other file
    /// managers fall back to.
    /// </remarks>
    public static string Label(BlockDeviceFacts facts, string? mountPoint)
    {
        if (!string.IsNullOrWhiteSpace(facts.Label))
            return facts.Label;

        if (mountPoint == "/")
            return RootLabel;

        if (mountPoint is not null)
        {
            var name = mountPoint.TrimEnd('/');
            var slash = name.LastIndexOf('/');
            if (slash >= 0 && slash < name.Length - 1)
                return name[(slash + 1)..];
        }

        if (!string.IsNullOrWhiteSpace(facts.DriveName))
            return facts.DriveName;

        return facts.SizeBytes > 0 ? FormatSize(facts.SizeBytes) : facts.Device;
    }

    /// <summary>What the filesystem root is called when it has no label of its own.</summary>
    /// <remarks>
    /// Not localized, and that is a decision rather than an oversight: this is a name in a list
    /// beside <c>Dev</c> and <c>Games</c>, which are directory names and are not translated
    /// either. A translated word among untranslated ones reads as the odd one out.
    /// </remarks>
    public const string RootLabel = "System";

    /// <summary>A size in the units a disk is sold in.</summary>
    /// <remarks>
    /// Powers of ten, unlike the file sizes in the listing, because that is what is written on
    /// the drive: a disk sold as 1 TB is what <c>1000203820544</c> bytes is, and rendering it
    /// as "931.5 GiB" invites the question of where the rest went.
    /// </remarks>
    public static string FormatSize(long bytes)
    {
        string[] units = ["kB", "MB", "GB", "TB", "PB"];
        if (bytes < 1000)
            return $"{bytes} B";

        double value = bytes;
        var unit = -1;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return value >= 100
            ? $"{Math.Round(value)} {units[unit]}"
            : $"{value:0.#} {units[unit]}";
    }

    /// <summary>
    /// The disks as <c>/proc/self/mountinfo</c> alone can describe them.
    /// </summary>
    /// <remarks>
    /// The fallback for a machine with no udisks2 — a container, CI, or a session where the
    /// daemon is not running. It can only see what is already mounted and knows nothing about
    /// removable media, so the list is poorer, but a sidebar listing the disks that exist beats
    /// a sidebar with no devices section at all.
    /// </remarks>
    public static IReadOnlyList<StorageDevice> FromMounts(MountTable mounts)
    {
        var byDevice = new Dictionary<string, List<Mount>>(StringComparer.Ordinal);
        foreach (var mount in mounts.Mounts)
        {
            if (!mount.IsRealFilesystem)
                continue;
            if (!byDevice.TryGetValue(mount.Source, out var list))
                byDevice[mount.Source] = list = [];
            list.Add(mount);
        }

        var devices = new List<StorageDevice>();
        foreach (var (source, group) in byDevice)
        {
            var facts = new BlockDeviceFacts(
                Id: source,
                Device: source,
                Label: string.Empty,
                FilesystemType: group[0].FilesystemType,
                Usage: "filesystem",
                SizeBytes: 0,
                MountPoints: [.. group.Select(mount => mount.MountPoint)],
                IsRemovable: false,
                IsReadOnly: group[0].IsReadOnly,
                Ignore: false);

            if (Describe(facts) is { } device)
                devices.Add(device);
        }

        devices.Sort(static (a, b) => string.CompareOrdinal(a.MountPoint, b.MountPoint));
        return devices;
    }
}
