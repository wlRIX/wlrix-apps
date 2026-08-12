namespace Wlrix.Packages.Models;

/// <summary>
/// What the Software Inventory's Status column says about a package. The names are the IRIX
/// ones, because the distinctions are the same: whether the system has it, and whether what the
/// repositories offer is newer than what is installed.
/// </summary>
public enum PackageStatus
{
    /// <summary>Available and not installed.</summary>
    New,

    /// <summary>Installed, with nothing to compare against (this came from an installed-only listing).</summary>
    Installed,

    /// <summary>Installed, and the available version is the same one.</summary>
    SameVersion,

    /// <summary>Installed, and the repositories have a newer version.</summary>
    UpgradeAvailable,

    /// <summary>Installed, and what the repositories offer is older — a rollback, not an upgrade.</summary>
    Downgrade,
}
