using System.IO.Enumeration;
using System.Text.RegularExpressions;

namespace Wlrix.Packages.Backends.Parsing;

/// <summary>The facts about the running system that decide whether a pacman upgrade matters.</summary>
/// <param name="KernelPackage">The running kernel's package, or empty if it could not be told.</param>
/// <param name="KernelModulesPresent">Whether the running kernel's modules are still on disk.</param>
/// <param name="MountedFilesystems">The filesystem types currently mounted.</param>
public sealed record RunningSystem(
    string KernelPackage,
    bool KernelModulesPresent,
    IReadOnlySet<string> MountedFilesystems);

/// <summary>
/// Decides whether a pacman transaction leaves the system needing a restart. pacman has no such
/// concept, so this is a list of packages that the running system keeps loaded.
///
/// The list follows CachyOS's <c>cachyos-reboot-required</c> hook. That hook cannot be relied on
/// instead: it exists only on CachyOS, and it finds who to tell from <c>users</c>, which has been
/// seen empty inside a running wlRIX session.
///
/// This differs from the hook on kernels. It only counts the kernel that is running, so upgrading
/// <c>linux</c> while booted into <c>linux-cachyos</c> asks for nothing, and it leaves out the
/// headers, which nothing loads.
/// </summary>
public static partial class RestartPolicy
{
    /// <summary>Everything else that the hook names, and that is loaded however the system is set up.</summary>
    private static readonly string[] SystemPackages =
    [
        "amd-ucode", "intel-ucode", "linux-firmware*", "cryptsetup", "mesa", "systemd*", "wayland",
        "egl-wayland", "xf86-video-*", "xorg-server*", "xorg-fonts*", "mkinitcpio*", "booster*",
        "dracut*", "winesync-dkms", "nvidia-dkms", "nvidia-*xx-dkms", "nvidia-*xx", "nvidia-*lts-dkms",
        "nvidia-utils", "lib32-nvidia-utils", "nvidia-open", "nvidia-open-dkms",
    ];

    private static readonly string[] KernelPackages =
        ["linux", "linux-hardened", "linux-lts", "linux-zen", "linux-cachyos*", "linux-cacule*"];

    private static readonly string[] KernelCompanionSuffixes = ["-headers", "-docs", "-api-headers"];

    /// <summary>The package in <c>/proc/version</c>: <c>Linux version 7.3.1 (linux-cachyos@cachyos)</c>.</summary>
    [GeneratedRegex(@"\((?<package>[^@()\s]+)@", RegexOptions.ExplicitCapture)]
    private static partial Regex KernelBuilder { get; }

    /// <summary>The kernel package named by the text of <c>/proc/version</c>, or empty.</summary>
    public static string ReadKernelPackage(string procVersion) =>
        KernelBuilder.Match(procVersion) is { Success: true } match ? match.Groups["package"].Value : string.Empty;

    /// <summary>The filesystem types in the text of <c>/proc/mounts</c>.</summary>
    public static IReadOnlySet<string> ReadMountedFilesystems(string procMounts) =>
        procMounts.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(' '))
            .Where(fields => fields.Length > 2)
            .Select(fields => fields[2])
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="operations"/> on <paramref name="system"/> call for a restart. Only
    /// an upgrade counts: nothing is running a newly installed package yet, and a removed running
    /// kernel shows up as its modules being gone.
    /// </summary>
    public static bool NeedsRestart(IEnumerable<PackageOperation> operations, RunningSystem system) =>
        !system.KernelModulesPresent
        || operations.Any(operation => operation.Kind == PackageOperationKind.Upgrade
                                       && UpgradeMatters(operation.Name, system));

    private static bool UpgradeMatters(string name, RunningSystem system)
    {
        if (Matches(KernelPackages, name) && !IsKernelCompanion(name) && !IsKernelModulePackage(name))
            return system.KernelPackage.Length == 0 || name == system.KernelPackage;

        return name switch
        {
            // Built for one kernel, so only that kernel's are in use.
            "nvidia" => system.KernelPackage == "linux",
            "nvidia-lts" => system.KernelPackage == "linux-lts",
            _ when IsKernelModulePackage(name) =>
                system.KernelPackage.Length > 0 && name.StartsWith(system.KernelPackage + "-nvidia", StringComparison.Ordinal),
            // Only worth a restart if the kernel is using that filesystem.
            "btrfs-progs" => system.MountedFilesystems.Contains("btrfs"),
            "xfsprogs" => system.MountedFilesystems.Contains("xfs"),
            "e2fsprogs" => system.MountedFilesystems.Contains("ext4"),
            _ => Matches(SystemPackages, name) || FileSystemName.MatchesSimpleExpression("nvidia*-lts", name),
        };
    }

    private static bool IsKernelCompanion(string name) =>
        KernelCompanionSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal));

    /// <summary><c>linux-cachyos-nvidia</c>, <c>linux-cachyos-nvidia-open</c> and so on.</summary>
    private static bool IsKernelModulePackage(string name) =>
        name.StartsWith("linux-", StringComparison.Ordinal)
        && !name.StartsWith("linux-firmware", StringComparison.Ordinal)
        && name.Contains("-nvidia", StringComparison.Ordinal);

    private static bool Matches(IEnumerable<string> patterns, string name) =>
        patterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: false));
}
