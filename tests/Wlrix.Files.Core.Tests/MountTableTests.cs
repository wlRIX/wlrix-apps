using Wlrix.Files.Core.Platform;
using Xunit;

namespace Wlrix.Files.Core.Tests;

public class MountTableTests
{
    private static MountTable Desktop() => MountTable.Parse(Fixture.Lines("mountinfo-desktop.txt"));

    [Fact]
    public void TheMostSpecificMountPointWinsRatherThanTheFirstMatch()
    {
        // Every path is under "/", so a naive scan would answer "/" for everything.
        var table = Desktop();
        Assert.Equal("/home", table.Find("/home/vic/notes.txt")!.MountPoint);
        Assert.Equal("/boot", table.Find("/boot/vmlinuz")!.MountPoint);
        Assert.Equal("/", table.Find("/usr/bin/env")!.MountPoint);
    }

    [Fact]
    public void APrefixThatIsNotAPathComponentDoesNotMatch()
    {
        // "/boots" starts with "/boot" as a string but is not on that mount.
        Assert.Equal("/", Desktop().Find("/boots/x")!.MountPoint);
    }

    [Fact]
    public void TwoBtrfsSubvolumesOfOneDeviceCountAsTheSameFilesystem()
    {
        // This is the case /proc/mounts cannot answer, and getting it wrong means
        // copying a whole home directory where a rename would have done.
        var table = Desktop();
        Assert.True(table.IsSameFilesystem(Location.Parse("/usr/share/x"), Location.Parse("/home/vic/x")));
    }

    [Fact]
    public void ABindMountSharesTheFilesystemItWasBoundFrom()
    {
        var table = Desktop();
        Assert.True(table.IsSameFilesystem(Location.Parse("/mnt/scratch/a"), Location.Parse("/mnt/work/b")));
    }

    [Fact]
    public void SeparateDevicesAreNotTheSameFilesystem()
    {
        var table = Desktop();
        Assert.False(table.IsSameFilesystem(Location.Parse("/home/vic/x"), Location.Parse("/boot/y")));
        Assert.False(table.IsSameFilesystem(Location.Parse("/home/vic/x"), Location.Parse("/mnt/scratch/y")));
    }

    [Fact]
    public void ARemoteLocationIsNeverOnTheSameFilesystemAsALocalOne()
    {
        var table = Desktop();
        Assert.False(table.IsSameFilesystem(Location.Parse("/home/vic/x"), Location.Parse("smb://host/share/y")));
    }

    [Fact]
    public void OctalEscapesInMountPointsAreDecoded()
    {
        // The kernel escapes space, tab, newline and backslash. Left encoded, a path
        // under this mount would never match it.
        Assert.NotNull(Desktop().Find("/mnt/tag space/file"));
        Assert.Equal("/mnt/tag space", Desktop().Find("/mnt/tag space/file")!.MountPoint);
    }

    [Fact]
    public void TheAllowlistKeepsPseudoFilesystemsOutOfTheDevicesList()
    {
        var real = Desktop().RealFilesystems.Select(m => m.MountPoint).ToList();
        Assert.Contains("/", real);
        Assert.Contains("/home", real);
        Assert.Contains("/boot", real);
        Assert.Contains("/mnt/nas", real);
        // proc, sysfs, devtmpfs, tmpfs, binfmt_misc and the portal's fuse mount are
        // not places a user keeps files.
        Assert.DoesNotContain("/proc", real);
        Assert.DoesNotContain("/sys", real);
        Assert.DoesNotContain("/run", real);
        Assert.DoesNotContain("/run/user/1000/doc", real);
    }

    [Fact]
    public void AReadOnlyMountIsReportedAsSuch()
    {
        var nas = Desktop().Find("/mnt/nas/share")!;
        Assert.True(nas.IsReadOnly);
        Assert.Equal("cifs", nas.FilesystemType);
        Assert.False(Desktop().Find("/home/vic")!.IsReadOnly);
    }

    [Fact]
    public void MalformedLinesAreSkippedRatherThanThrowing()
    {
        // mountinfo grows fields over kernel versions; an unparseable line must not
        // take out the whole devices rail.
        var table = MountTable.Parse(["garbage", "", "27 1 0:24 /@ / rw - btrfs /dev/x rw"]);
        Assert.Single(table.Mounts);
    }

    [Fact]
    public void TheRealMountTableOfThisMachineParses()
    {
        // A smoke test against the live kernel: the fixture cannot catch a format
        // change, and this will.
        var table = MountTable.Read();
        Assert.NotEmpty(table.Mounts);
        Assert.NotNull(table.Find("/"));
        Assert.True(table.IsSameFilesystem(Location.Parse("/"), Location.Parse("/")));
    }

    [Fact]
    public void LocalStorageIsADifferentQuestionFromHoldingUserFiles()
    {
        // Confusing the two is a real mistake and was one: the devices rail wants network
        // mounts listed, and the thumbnailer must not read them. Using one set for both
        // refused tmpfs -- the fastest storage on the machine -- and permitted NFS, which is
        // exactly the case the thumbnail gate exists to prevent.
        var table = MountTable.Parse(
        [
            "1 0 8:1 / / rw shared:1 - ext4 /dev/sda1 rw",
            "2 1 0:20 / /tmp rw shared:2 - tmpfs tmpfs rw",
            "3 1 0:30 / /mnt/nas rw shared:3 - nfs4 server:/export rw",
            "4 1 0:40 / /mnt/win rw shared:4 - cifs //server/share rw",
            "5 1 0:50 / /proc rw shared:5 - proc proc rw"
        ]);

        Assert.True(table.Find("/home/vic/a.jpg")!.IsLocalStorage);
        Assert.True(table.Find("/tmp/a.jpg")!.IsLocalStorage);
        Assert.False(table.Find("/mnt/nas/a.jpg")!.IsLocalStorage);
        Assert.False(table.Find("/mnt/win/a.jpg")!.IsLocalStorage);
        Assert.False(table.Find("/proc/cpuinfo")!.IsLocalStorage);

        // ...while the rail still lists the shares, because they are places with files on.
        Assert.True(table.Find("/mnt/nas/a.jpg")!.IsRealFilesystem);
        Assert.False(table.Find("/tmp/a.jpg")!.IsRealFilesystem);
    }
}
