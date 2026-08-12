using Wlrix.Packages.Models;

namespace Wlrix.Packages;

/// <summary>
/// One system package manager, seen through the operations the Software Manager needs.
///
/// Everything declared here is a <em>read</em>, and runs unprivileged in this process. The
/// writes are deliberately absent: a backend describes a transaction, and
/// <c>Wlrix.Packages.Privileged</c> is what performs it, through a helper behind pkexec. Keeping
/// the two apart means the code that can change the system is one small file rather than a
/// method on each of three backends.
/// </summary>
public interface IPackageBackend
{
    /// <summary>Which package manager this is: <c>pacman</c>, <c>apt</c>, <c>zypper</c>.</summary>
    string Id { get; }

    /// <summary>What it can be asked to do.</summary>
    BackendCapabilities Capabilities { get; }

    /// <summary>
    /// Packages matching <paramref name="query"/>, whether installed or not. An empty query is
    /// answered with an empty list rather than every package in every repository.
    /// </summary>
    Task<IReadOnlyList<PackageInfo>> SearchAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>Everything installed on this system.</summary>
    Task<IReadOnlyList<PackageInfo>> ListInstalledAsync(CancellationToken cancellationToken = default);

    /// <summary>Installed packages with a newer version available.</summary>
    Task<IReadOnlyList<PackageInfo>> ListUpdatesAsync(CancellationToken cancellationToken = default);

    /// <summary>Everything known about one package, or <c>null</c> if there is no such package.</summary>
    Task<PackageDetails?> DescribeAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>The configured software sources.</summary>
    Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync(CancellationToken cancellationToken = default);
}
