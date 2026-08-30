namespace Wlrix.Packages.Models;

/// <summary>
/// What a given package manager can be asked to do. The UI disables controls from these rather
/// than each view knowing which backends support what — the difference between "pacman has no
/// command for adding a repository" and "the Add button is grayed out" belongs in one place.
/// </summary>
[Flags]
public enum BackendCapabilities
{
    None = 0,

    /// <summary>Install and remove packages from the configured repositories.</summary>
    InstallFromRepository = 1 << 0,

    /// <summary>Install a package file from disk.</summary>
    InstallFromFile = 1 << 1,

    /// <summary>Refresh the package lists, and upgrade what is installed.</summary>
    Upgrade = 1 << 2,

    /// <summary>Report the configured repositories.</summary>
    ListRepositories = 1 << 3,

    /// <summary>
    /// Add, remove, enable and disable repositories. Distinct from
    /// <see cref="ListRepositories"/>: pacman can be read but has no command for the writes, and
    /// editing its configuration file by hand is not something to do on a user's behalf.
    /// </summary>
    ModifyRepositories = 1 << 4,
}
