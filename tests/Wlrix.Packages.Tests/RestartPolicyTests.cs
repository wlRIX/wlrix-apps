using Wlrix.Packages.Backends.Parsing;
using Xunit;

namespace Wlrix.Packages.Tests;

/// <summary>Which pacman upgrades call for a restart, and how the running system is read.</summary>
public class RestartPolicyTests
{
    private static readonly RunningSystem Cachyos = new("linux-cachyos", true, new HashSet<string> { "btrfs", "vfat" });

    private static bool Upgrading(string name, RunningSystem? system = null) => RestartPolicy.NeedsRestart(
        [new PackageOperation(PackageOperationKind.Upgrade, name)], system ?? Cachyos);

    [Theory]
    [InlineData("linux-cachyos")]
    [InlineData("amd-ucode")]
    [InlineData("linux-firmware")]
    [InlineData("linux-firmware-amdgpu")]
    [InlineData("linux-firmware-nvidia")]
    [InlineData("mesa")]
    [InlineData("systemd")]
    [InlineData("systemd-libs")]
    [InlineData("wayland")]
    [InlineData("nvidia-utils")]
    [InlineData("linux-cachyos-nvidia-open")]
    [InlineData("btrfs-progs")]
    public void Restarts(string name) => Assert.True(Upgrading(name));

    [Theory]
    [InlineData("linux")]
    [InlineData("linux-lts")]
    [InlineData("linux-cachyos-lts")]
    [InlineData("linux-cachyos-headers")]
    [InlineData("linux-api-headers")]
    [InlineData("linux-cachyos-lts-nvidia")]
    [InlineData("nvidia")]
    [InlineData("nvidia-lts")]
    [InlineData("xfsprogs")]
    [InlineData("e2fsprogs")]
    [InlineData("firefox")]
    [InlineData("lib32-mesa")]
    public void DoesNotRestart(string name) => Assert.False(Upgrading(name));

    [Fact]
    public void AnInstallOrRemovalDoesNotCount()
    {
        Assert.False(RestartPolicy.NeedsRestart([new PackageOperation(PackageOperationKind.Install, "mesa")], Cachyos));
        Assert.False(RestartPolicy.NeedsRestart([new PackageOperation(PackageOperationKind.Remove, "mesa")], Cachyos));
    }

    [Fact]
    public void MissingKernelModulesAlwaysMean_Restart() =>
        Assert.True(RestartPolicy.NeedsRestart([], Cachyos with { KernelModulesPresent = false }));

    [Fact]
    public void AnUnknownRunningKernelCountsEveryKernel() =>
        Assert.True(Upgrading("linux-lts", Cachyos with { KernelPackage = string.Empty }));

    [Fact]
    public void ReadsTheKernelPackage()
    {
        Assert.Equal("linux-cachyos", RestartPolicy.ReadKernelPackage(
            "Linux version 7.3.1-1-cachyos (linux-cachyos@cachyos) (clang version 23.1.4, LLD 23.1.4) #1 SMP"));
        Assert.Equal(string.Empty, RestartPolicy.ReadKernelPackage("Linux version 6.0 (gcc 13.1) #1"));
    }

    [Fact]
    public void ReadsTheMountedFilesystems() =>
        Assert.Equal(new HashSet<string> { "btrfs", "tmpfs" }, RestartPolicy.ReadMountedFilesystems(
            "/dev/sda1 / btrfs rw 0 0\ntmpfs /run tmpfs rw 0 0\n"));

    [Theory]
    [InlineData("upgrading mesa...", PackageOperationKind.Upgrade, "mesa")]
    [InlineData("reinstalling mesa...", PackageOperationKind.Upgrade, "mesa")]
    [InlineData("downgrading mesa...", PackageOperationKind.Upgrade, "mesa")]
    [InlineData("installing gtk-4.0...", PackageOperationKind.Install, "gtk-4.0")]
    [InlineData("removing cowsay...", PackageOperationKind.Remove, "cowsay")]
    [InlineData("( 1/70) upgrading linux-cachyos     [####] 100%", PackageOperationKind.Upgrade, "linux-cachyos")]
    public void ReadsPacmanOperations(string line, PackageOperationKind kind, string name) =>
        Assert.Equal(new PackageOperation(kind, name), PackageOperations.Read("pacman", line));

    [Fact]
    public void ReadsARecordedPacmanRun()
    {
        // pacman 7 under LC_ALL=C with no terminal, as the helper runs it: an install, an upgrade
        // and a removal of one package, in a throwaway root.
        var operations = Fixture.Read("pacman-transaction.txt").Split('\n')
            .Select(line => PackageOperations.Read("pacman", line))
            .OfType<PackageOperation>();

        Assert.Equal(
            [
                new PackageOperation(PackageOperationKind.Install, "wlrix-fixture"),
                new PackageOperation(PackageOperationKind.Upgrade, "wlrix-fixture"),
                new PackageOperation(PackageOperationKind.Remove, "wlrix-fixture"),
            ],
            operations);
    }

    [Theory]
    [InlineData("pacman", "resolving dependencies...")]
    [InlineData("pacman", ":: Running post-transaction hooks...")]
    [InlineData("pacman", "the installing of nothing")]
    [InlineData("apt", "upgrading mesa...")]
    public void IgnoresOtherLines(string backend, string line) =>
        Assert.Null(PackageOperations.Read(backend, line));
}
