using Microsoft.Extensions.Logging;
using ZLogger;

namespace Wlrix.SoftwareManager.Services;

/// <summary>One mounted filesystem the Disk Space pane can show. Sizes are in kilobytes.</summary>
/// <param name="MountPoint">Where it is mounted, and what the pane's selector shows.</param>
/// <param name="TotalKilobytes">Capacity.</param>
/// <param name="UsedKilobytes">In use.</param>
public sealed record DiskUsage(string MountPoint, long TotalKilobytes, long UsedKilobytes)
{
    public long FreeKilobytes => TotalKilobytes - UsedKilobytes;
}

/// <summary>Where the Disk Space pane's numbers come from.</summary>
public interface IDiskSpaceProbe
{
    /// <summary>
    /// The filesystems worth offering, root first. Pseudo-filesystems (tmpfs, proc, and the
    /// rest) are left out: they hold no packages, and a dozen of them would bury the one mount
    /// point the user came to look at.
    /// </summary>
    IReadOnlyList<DiskUsage> Probe();
}

/// <inheritdoc />
public sealed class DiskSpaceProbe(ILogger<DiskSpaceProbe> logger) : IDiskSpaceProbe
{
    // The filesystem types a package can actually land on. Everything else on a running Linux
    // system is a kernel or runtime interface that happens to be mounted.
    private static readonly HashSet<string> RealFilesystems = new(StringComparer.Ordinal)
    {
        "ext2", "ext3", "ext4", "btrfs", "xfs", "zfs", "f2fs", "jfs", "reiserfs",
        "vfat", "exfat", "ntfs", "ntfs3", "overlay", "nfs", "nfs4",
    };

    public IReadOnlyList<DiskUsage> Probe()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(drive => drive.IsReady
                                && drive.TotalSize > 0
                                && RealFilesystems.Contains(drive.DriveFormat))
                .Select(drive => new DiskUsage(
                    drive.Name,
                    ToKilobytes(drive.TotalSize),
                    ToKilobytes(drive.TotalSize - drive.TotalFreeSpace)))
                // Root first, then the rest alphabetically: "/" is the answer to "where will
                // this package go" on nearly every system, so it should not need looking for.
                .OrderBy(usage => usage.MountPoint == "/" ? 0 : 1)
                .ThenBy(usage => usage.MountPoint, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.ZLogWarning(ex, $"Could not read the mounted filesystems.");
            return [];
        }
    }

    private static long ToKilobytes(long bytes) => bytes / 1024;
}
