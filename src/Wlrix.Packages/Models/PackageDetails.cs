namespace Wlrix.Packages.Models;

/// <summary>
/// Everything a package manager will say about one package, for the details view. Fields a
/// given package manager does not report come back empty rather than as a missing key: the
/// caller renders a blank line, which is honest, and does not have to know which of the three
/// backends answered.
/// </summary>
/// <param name="Name">The package name.</param>
/// <param name="Version">The version these details describe.</param>
/// <param name="Description">The long description, which may run to several lines.</param>
/// <param name="License">The license identifier, where one is declared.</param>
/// <param name="Url">The upstream project's address.</param>
/// <param name="Repository">Which repository it came from.</param>
/// <param name="InstalledSizeKilobytes">Installed size, or zero when unknown.</param>
/// <param name="DownloadSizeKilobytes">Download size, or zero when unknown or already present.</param>
/// <param name="Dependencies">What it needs.</param>
public sealed record PackageDetails(
    string Name,
    string Version,
    string Description,
    string License,
    string Url,
    string Repository,
    long InstalledSizeKilobytes,
    long DownloadSizeKilobytes,
    IReadOnlyList<string> Dependencies);
