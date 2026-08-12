namespace Wlrix.Packages.Models;

/// <summary>One configured software source.</summary>
/// <param name="Id">
/// What the package manager calls it — a zypper alias, a pacman section name, or the file a
/// deb line lives in. This is what a later add/remove/enable is addressed to.
/// </param>
/// <param name="Name">The human-readable name, where there is one; otherwise the id again.</param>
/// <param name="Url">Where it fetches from. A mirror list stands in for its first entry.</param>
/// <param name="IsEnabled">Whether the package manager is currently using it.</param>
public sealed record RepositoryInfo(string Id, string Name, string Url, bool IsEnabled);
