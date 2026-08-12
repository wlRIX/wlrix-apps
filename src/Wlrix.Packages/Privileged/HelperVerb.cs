namespace Wlrix.Packages.Privileged;

/// <summary>
/// The closed set of things the privileged helper will do. Both the application and the helper
/// compile against this, so there is exactly one list and no way for the two to disagree about
/// what is allowed.
///
/// There is deliberately no verb that takes a command to run. Everything here names an
/// operation; the arguments are package names, file paths and repository aliases, each of which
/// <see cref="HelperRequest"/> validates before the helper builds anything from it.
/// </summary>
public enum HelperVerb
{
    /// <summary>Install named packages from the configured repositories.</summary>
    Install,

    /// <summary>Install package files from disk.</summary>
    InstallFile,

    /// <summary>Remove named packages.</summary>
    Remove,

    /// <summary>Re-download the package lists.</summary>
    Refresh,

    /// <summary>Upgrade everything installed.</summary>
    Upgrade,

    /// <summary>Add a repository: alias, then URL.</summary>
    RepoAdd,

    /// <summary>Remove a repository by alias.</summary>
    RepoRemove,

    /// <summary>Enable a repository by alias.</summary>
    RepoEnable,

    /// <summary>Disable a repository by alias.</summary>
    RepoDisable,
}
