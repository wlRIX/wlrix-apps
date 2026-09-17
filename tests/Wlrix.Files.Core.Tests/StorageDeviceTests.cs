using Wlrix.Files.Core.Platform;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// Which disks belong in the sidebar, and what they are called.
/// </summary>
/// <remarks>
/// Every case here is taken from the machine this was written on, which has five disks worth
/// showing, a btrfs root mounted seven times, a <c>/boot</c> that belongs in nobody's sidebar
/// and an unmounted USB stick. Arguing with the rules in a test beats arguing with them against
/// a running daemon holding one particular set of hardware.
/// </remarks>
public class StorageDeviceTests
{
    private static BlockDeviceFacts Facts(
        string device = "/dev/sda1",
        string label = "",
        string usage = "filesystem",
        string type = "ext4",
        long size = 1_000_000_000_000,
        IReadOnlyList<string>? mountPoints = null,
        bool removable = false,
        bool ignore = false,
        string driveName = "") =>
        new(device, device, label, type, usage, size, mountPoints ?? [], removable, false, ignore, driveName);

    // --- what is shown -------------------------------------------------------

    [Fact]
    public void AMountedDataDiskIsShown()
    {
        var device = StorageDevices.Describe(Facts(mountPoints: ["/mnt/Games"]));

        Assert.NotNull(device);
        Assert.Equal("/mnt/Games", device!.MountPoint);
        Assert.True(device.IsMounted);
        Assert.Equal("/mnt/Games", device.Location!.Path);
    }

    [Fact]
    public void ADeviceTheSystemAsksToHideIsHidden()
    {
        // zram and the loopbacks behind a snap or an appimage all set HintIgnore.
        Assert.Null(StorageDevices.Describe(Facts(mountPoints: ["/mnt/x"], ignore: true)));
    }

    [Theory]
    [InlineData("swap")]
    [InlineData("crypto")]
    [InlineData("")]
    public void OnlySomethingWithFilesInItIsShown(string usage) =>
        Assert.Null(StorageDevices.Describe(Facts(usage: usage, mountPoints: ["/mnt/x"])));

    [Theory]
    [InlineData("/boot")]
    [InlineData("/boot/efi")]
    [InlineData("/efi")]
    public void TheBootPartitionIsNotSomewhereToGoBrowsing(string mountPoint)
    {
        // A real filesystem on a real partition, and the one thing a person can do by opening
        // it is break the machine. Nothing in the device's own properties says so — udisks
        // marks it HintSystem exactly as it marks the root filesystem and every internal disk.
        Assert.Null(StorageDevices.Describe(Facts(type: "vfat", mountPoints: [mountPoint])));
    }

    [Fact]
    public void AnUnmountedRemovableDiskIsShownBecauseThatIsThePoint()
    {
        var device = StorageDevices.Describe(Facts(device: "/dev/sdc1", removable: true, size: 32_000_000_000));

        Assert.NotNull(device);
        Assert.False(device!.IsMounted);
        Assert.Null(device.Location);
    }

    [Fact]
    public void AnUnmountedInternalPartitionIsLeftAlone()
    {
        // Usually a recovery image or another operating system. Offering to mount it is
        // offering to do something nobody asked for.
        Assert.Null(StorageDevices.Describe(Facts(removable: false)));
    }

    // --- one row per disk ----------------------------------------------------

    [Fact]
    public void ABtrfsRootMountedOncePerSubvolumeIsStillOneDisk()
    {
        // The case that decides between one row and seven on this machine.
        var device = StorageDevices.Describe(Facts(
            type: "btrfs",
            mountPoints: ["/", "/home", "/root", "/srv", "/var/cache", "/var/log", "/var/tmp"]));

        Assert.NotNull(device);
        Assert.Equal("/", device!.MountPoint);
    }

    [Theory]
    [InlineData(new[] { "/mnt/a", "/mnt/a/b" }, "/mnt/a")]
    [InlineData(new[] { "/var/log", "/" }, "/")]
    [InlineData(new string[0], null)]
    public void TheShortestMountPointStandsForTheDevice(string[] mounts, string? expected) =>
        Assert.Equal(expected, StorageDevices.PrimaryMountPoint(mounts));

    [Fact]
    public void TwoMountPointsOfEqualLengthPickTheSameOneEveryTime()
    {
        // Otherwise the row's name changes between refreshes for no reason a person can see.
        Assert.Equal("/mnt/a", StorageDevices.PrimaryMountPoint(["/mnt/b", "/mnt/a"]));
        Assert.Equal("/mnt/a", StorageDevices.PrimaryMountPoint(["/mnt/a", "/mnt/b"]));
    }

    // --- naming --------------------------------------------------------------

    [Fact]
    public void AFilesystemLabelWinsBecauseSomebodyChoseIt()
    {
        var device = StorageDevices.Describe(Facts(label: "Backups", mountPoints: ["/mnt/b"]));
        Assert.Equal("Backups", device!.Label);
    }

    [Fact]
    public void WithNoLabelTheMountPointsOwnNameIsUsed()
    {
        // On a machine that mounts its disks at /mnt/Games, that is exactly the name their
        // owner thinks of them by.
        Assert.Equal("Games", StorageDevices.Describe(Facts(mountPoints: ["/mnt/Games"]))!.Label);
        Assert.Equal("Dev", StorageDevices.Describe(Facts(mountPoints: ["/mnt/Dev"]))!.Label);
    }

    [Fact]
    public void TheRootGetsANameOfItsOwnBecauseItHasNoLastComponent()
    {
        Assert.Equal("System", StorageDevices.Describe(Facts(mountPoints: ["/"]))!.Label);
    }

    [Fact]
    public void AnUnlabeledStickFallsBackToTheDriveAndThenToItsSize()
    {
        Assert.Equal(
            "USB DISK 3.0",
            StorageDevices.Describe(Facts(removable: true, driveName: "USB DISK 3.0"))!.Label);

        Assert.Equal(
            "32 GB",
            StorageDevices.Describe(Facts(removable: true, size: 32_000_000_000))!.Label);
    }

    [Theory]
    [InlineData(1_000_203_820_544L, "1 TB")]
    [InlineData(32_000_000_000L, "32 GB")]
    [InlineData(8_001_562_156_544L, "8 TB")]
    [InlineData(1_500_000_000L, "1.5 GB")]
    [InlineData(512L, "512 B")]
    public void ADiskIsMeasuredInTheUnitsItWasSoldIn(long bytes, string expected)
    {
        // Powers of ten, unlike the file sizes in the listing. A disk sold as 1 TB is what
        // 1000203820544 bytes is, and calling it "931.5 GiB" invites the question of where the
        // rest went.
        Assert.Equal(expected, StorageDevices.FormatSize(bytes));
    }

    // --- the fallback --------------------------------------------------------

    [Fact]
    public void WithNoUdisksTheMountTableStillNamesTheDisks()
    {
        // A container, CI, or a session where the daemon is not running. A poorer list beats
        // no devices section at all.
        var mounts = MountTable.Parse([
            "23 1 259:5 / / rw,relatime shared:1 - btrfs /dev/nvme3n1p2 rw,subvol=/@",
            "24 23 259:5 /@home /home rw,relatime shared:2 - btrfs /dev/nvme3n1p2 rw,subvol=/@home",
            "25 1 259:1 / /mnt/Dev rw,relatime shared:3 - xfs /dev/nvme0n1p1 rw",
            "26 1 259:3 / /boot rw,relatime shared:4 - vfat /dev/nvme3n1p1 rw",
            "27 1 0:22 / /proc rw,relatime shared:5 - proc proc rw"
        ]);

        var devices = StorageDevices.FromMounts(mounts);

        // The root's two subvolumes are one disk, /boot is not there, and proc never was.
        Assert.Equal(["/", "/mnt/Dev"], devices.Select(device => device.MountPoint!).ToArray());
        Assert.Equal(["System", "Dev"], devices.Select(device => device.Label).ToArray());
    }
}
