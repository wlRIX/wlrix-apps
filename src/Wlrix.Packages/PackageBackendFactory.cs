using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wlrix.Common;
using Wlrix.Packages.Backends;
using ZLogger;

namespace Wlrix.Packages;

/// <summary>Which package manager this system runs, and the backend that speaks to it.</summary>
/// <param name="Backend">The backend, or a <see cref="NullBackend"/> if none was found.</param>
/// <param name="Distribution">What <c>/etc/os-release</c> says this system is.</param>
public sealed record PackageSystem(IPackageBackend Backend, DistributionInfo Distribution)
{
    /// <summary>Whether a real package manager was found.</summary>
    public bool IsSupported => Backend is not NullBackend;
}

/// <summary>Chooses the backend for the system the application is running on.</summary>
public interface IPackageBackendFactory
{
    /// <summary>The backend for this system. Resolved once and cached.</summary>
    PackageSystem Resolve();
}

/// <summary>
/// Picks a backend from <c>/etc/os-release</c>, confirmed by finding the program on the
/// <c>PATH</c>.
///
/// Both halves are needed, and neither on its own would do. os-release alone misses that a
/// container image may not have the tool installed; the <c>PATH</c> alone would pick the wrong
/// one on a system that has more than one — <c>apt</c> exists on plenty of Arch machines as a
/// package named after something else entirely, and Ubuntu ships a <c>zypper</c> in its
/// archive. The declared distribution decides; the <c>PATH</c> confirms.
/// </summary>
public sealed class PackageBackendFactory(IServiceProvider services,
    ILogger<PackageBackendFactory> logger) : IPackageBackendFactory
{
    // In order of preference, and the ID_LIKE families each answers for. The program name is
    // what has to be on the PATH for the backend to be usable at all.
    private static readonly (string Family, string Program, Type Backend)[] Candidates =
    [
        ("arch", "pacman", typeof(PacmanBackend)),
        ("debian", "apt-get", typeof(AptBackend)),
        ("suse", "zypper", typeof(ZypperBackend)),
        ("opensuse", "zypper", typeof(ZypperBackend)),
    ];

    private PackageSystem? _resolved;

    public PackageSystem Resolve()
    {
        if (_resolved is not null)
            return _resolved;

        var distribution = SystemDetection.Detect();

        foreach (var (family, program, type) in Candidates)
        {
            if (!distribution.Matches(family) || !Executables.Exists(program))
                continue;

            var backend = (IPackageBackend)ActivatorUtilities.CreateInstance(services, type);
            logger.ZLogInformation($"Using the {backend.Id} backend on {distribution.Name}.");
            return _resolved = new PackageSystem(backend, distribution);
        }

        // Nothing matched by declaration. Rather than give up, take the first package manager
        // that is actually installed: a distribution this does not know by name but which runs
        // one of these three is far more likely than a system with the binary and no use for it.
        foreach (var (_, program, type) in Candidates)
        {
            if (!Executables.Exists(program))
                continue;

            var backend = (IPackageBackend)ActivatorUtilities.CreateInstance(services, type);
            logger.ZLogInformation(
                $"{distribution.Name} is not a distribution this recognizes; using the {backend.Id} backend, which is installed.");
            return _resolved = new PackageSystem(backend, distribution);
        }

        logger.ZLogWarning($"No supported package manager was found on {distribution.Name}.");
        return _resolved = new PackageSystem(new NullBackend(), distribution);
    }
}
