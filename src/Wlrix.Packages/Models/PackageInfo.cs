namespace Wlrix.Packages.Models;

/// <summary>One row's worth of package: what every listing has in common.</summary>
/// <param name="Name">The package name, as its package manager spells it.</param>
/// <param name="Version">
/// The version this listing is about — the available one when browsing repositories, the
/// installed one when browsing what is on the system.
/// </param>
/// <param name="InstalledVersion">What is installed, if anything.</param>
/// <param name="Summary">The one-line description.</param>
/// <param name="Repository">Where it comes from, for the Type column.</param>
/// <param name="InstalledSizeKilobytes">
/// What it occupies once installed, or zero when the package manager did not say. Zero means
/// unknown, not empty; no real package occupies nothing.
/// </param>
/// <param name="Status">What the Status column shows.</param>
public sealed record PackageInfo(
    string Name,
    string Version,
    string? InstalledVersion,
    string Summary,
    string Repository,
    long InstalledSizeKilobytes,
    PackageStatus Status)
{
    /// <summary>Whether this package can be installed or upgraded.</summary>
    public bool CanInstall => Status is PackageStatus.New or PackageStatus.UpgradeAvailable
        or PackageStatus.Downgrade;

    /// <summary>Whether this package is on the system, and so can be removed.</summary>
    public bool CanRemove => InstalledVersion is not null;
}
