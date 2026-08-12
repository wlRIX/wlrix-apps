using ReactiveUI;

namespace Wlrix.SoftwareManager.ViewModels;

/// <summary>One row of the Software Inventory list.</summary>
/// <remarks>
/// The two check columns are mutually exclusive by construction rather than by validation:
/// marking a row for installation clears its removal mark and the other way round, so there is
/// no state in which a row is asking for both.
/// </remarks>
public sealed class PackageRowViewModel : ViewModelBase
{
    private bool _markedForInstall;
    private bool _markedForRemoval;

    public PackageRowViewModel(string name, string version, string statusText, long sizeKilobytes,
        string typeText, bool canInstall, bool canRemove)
    {
        Name = name;
        Version = version;
        StatusText = statusText;
        SizeKilobytes = sizeKilobytes;
        TypeText = typeText;
        CanInstall = canInstall;
        CanRemove = canRemove;
    }

    /// <summary>The package name, as the package manager spells it.</summary>
    public string Name { get; }

    /// <summary>The version this row is about — the available one, or the installed one.</summary>
    public string Version { get; }

    /// <summary>New / Installed / Same Version / Upgrade, already localized.</summary>
    public string StatusText { get; }

    /// <summary>Installed size in kilobytes, or zero when the package manager did not say.</summary>
    public long SizeKilobytes { get; }

    /// <summary>The repository or origin the package comes from.</summary>
    public string TypeText { get; }

    /// <summary>Whether the Install column's box is available on this row.</summary>
    public bool CanInstall { get; }

    /// <summary>Whether the Remove column's box is available on this row.</summary>
    public bool CanRemove { get; }

    /// <summary>What the Product column shows: the name, and the version after it.</summary>
    public string DisplayName => Version.Length > 0 ? $"{Name}, {Version}" : Name;

    public bool MarkedForInstall
    {
        get => _markedForInstall;
        set
        {
            this.RaiseAndSetIfChanged(ref _markedForInstall, value);
            if (value)
                MarkedForRemoval = false;
        }
    }

    public bool MarkedForRemoval
    {
        get => _markedForRemoval;
        set
        {
            this.RaiseAndSetIfChanged(ref _markedForRemoval, value);
            if (value)
                MarkedForInstall = false;
        }
    }

    /// <summary>Whether this row is part of the next transaction at all.</summary>
    public bool IsMarked => _markedForInstall || _markedForRemoval;
}
