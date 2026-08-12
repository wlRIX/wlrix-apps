namespace Wlrix.SoftwareManager.ViewModels;

/// <summary>
/// Which of the three mode buttons is lit, and so what the Software Inventory pane is a list
/// of. These stand where IRIX had Default Installation / Customize Installation / Manage
/// Installed Software; the first two of those were two ways of installing from one distribution
/// medium, which is not the shape a repository-based package manager has.
/// </summary>
public enum ManagerMode
{
    /// <summary>Everything the repositories offer, filtered by the Available Software field.</summary>
    Install,

    /// <summary>What is installed on this system, for removal.</summary>
    Manage,

    /// <summary>Installed packages with a newer version available.</summary>
    Updates,
}
