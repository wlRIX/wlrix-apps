using Wlrix.Packages.Models;

namespace Wlrix.Packages.Backends;

/// <summary>
/// What the factory returns when no supported package manager is on this system.
///
/// The window still opens, the panes still work, and the user software half of the application
/// is untouched — the same posture the rest of wlRIX takes towards a missing component. An app
/// that refused to start because it did not recognize the distribution would be worse than one
/// that says so in its status line.
/// </summary>
public sealed class NullBackend : IPackageBackend
{
    public string Id => "none";

    public BackendCapabilities Capabilities => BackendCapabilities.None;

    public Task<IReadOnlyList<PackageInfo>> SearchAsync(string query,
        CancellationToken cancellationToken = default) => Empty<PackageInfo>();

    public Task<IReadOnlyList<PackageInfo>> ListInstalledAsync(
        CancellationToken cancellationToken = default) => Empty<PackageInfo>();

    public Task<IReadOnlyList<PackageInfo>> ListUpdatesAsync(
        CancellationToken cancellationToken = default) => Empty<PackageInfo>();

    public Task<PackageDetails?> DescribeAsync(string name,
        CancellationToken cancellationToken = default) => Task.FromResult<PackageDetails?>(null);

    public Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync(
        CancellationToken cancellationToken = default) => Empty<RepositoryInfo>();

    private static Task<IReadOnlyList<T>> Empty<T>() => Task.FromResult<IReadOnlyList<T>>([]);
}
