using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ReactiveUI;
using Wlrix.Packages.AppStream;
using Wlrix.SoftwareManager.Services;

namespace Wlrix.SoftwareManager.ViewModels;

/// <summary>One row of the Software Inventory list.</summary>
/// <remarks>
/// The two check columns are mutually exclusive by construction rather than by validation:
/// marking a row for installation clears its removal mark and the other way round, so there is
/// no state in which a row is asking for both.
///
/// <para>
/// A product from AppStream only changes what the row looks like. <see cref="Name"/> stays the
/// package name, because that is what the marks and the transaction go by.
/// </para>
/// </remarks>
public sealed class PackageRowViewModel : ViewModelBase
{
    private bool _markedForInstall;
    private bool _markedForRemoval;
    private readonly AppStreamProduct? _product;
    private readonly IProductIcons? _icons;
    private Bitmap? _icon;
    private bool _iconRequested;

    public PackageRowViewModel(string name, string version, string statusText, long sizeKilobytes,
        string typeText, bool canInstall, bool canRemove, bool isFile = false, bool isLocked = false,
        AppStreamProduct? product = null, string categoryText = "", IProductIcons? icons = null)
    {
        _product = product;
        _icons = icons;
        CategoryText = categoryText;
        IsFile = isFile;
        IsLocked = isLocked;
        _markedForInstall = isLocked;
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

    /// <summary>Whether <see cref="Name"/> is the path of a package file rather than a package.</summary>
    public bool IsFile { get; }

    /// <summary>
    /// Whether this row goes with all the others and cannot be unticked: pacman's updates, which
    /// are all or nothing. A locked row shows as marked but is not one of the inventory's marks.
    /// </summary>
    public bool IsLocked { get; }

    /// <summary>Whether the user may change this row's boxes.</summary>
    public bool CanChangeMark => !IsLocked;

    /// <summary>What the Product column shows: the name, and the version after it.</summary>
    public string DisplayName => WithVersion(_product?.Name ?? Name);

    /// <summary>
    /// What the field above the list shows: <see cref="DisplayName"/>, with the package name when
    /// the product goes by another.
    /// </summary>
    public string FullName => _product?.Name is { } product && product != Name
        ? WithVersion($"{product} ({Name})")
        : DisplayName;

    /// <summary>The product's summary and the package name, or null to show no tooltip.</summary>
    public string? ToolTipText => _product?.Name is null ? null : $"{_product.Summary}\n{Name}".TrimStart();

    /// <summary>What the Category column shows, already localized. Empty when there is none.</summary>
    public string CategoryText { get; }

    /// <summary>
    /// The product's icon, or null until it arrives, and for good if it has none. Asked for on
    /// first read, which is when the row first scrolls into view, so a listing of two thousand
    /// packages only pays for the ones on screen.
    /// </summary>
    public Bitmap? Icon
    {
        get
        {
            if (!_iconRequested && _product is not null && _icons is not null)
            {
                _iconRequested = true;
                _ = LoadIconAsync(_icons, _product);
            }

            return _icon;
        }
        private set => this.RaiseAndSetIfChanged(ref _icon, value);
    }

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

    private string WithVersion(string name) => Version.Length > 0 ? $"{name}, {Version}" : name;

    private async Task LoadIconAsync(IProductIcons icons, AppStreamProduct product)
    {
        var bitmap = await icons.Get(product).ConfigureAwait(false);
        if (bitmap is not null)
            Dispatcher.UIThread.Post(() => Icon = bitmap);
    }
}
