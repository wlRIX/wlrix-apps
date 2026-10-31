using Wlrix.Common.Localization;

namespace Wlrix.SoftwareManager.Localization;

/// <summary>
/// Strongly-typed access to the app's localized UI strings (backed by <c>Strings.resx</c> and
/// its culture satellites).
///
/// Most of the window's text is static and comes straight out of the AXAML through
/// <c>{loc:Tr}</c>, which reads the same <see cref="Catalog"/>. The properties here are for the
/// strings a view model builds or chooses between.
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.SoftwareManager.Localization.Strings", typeof(Strings).Assembly);

    public static string SoftwareManager => Catalog.Get("SoftwareManager");
    public static string AboutTitle => Catalog.Get("AboutTitle");
    public static string Ok => Catalog.Get("Ok");
    public static string SourceHint => Catalog.Get("SourceHint");
    public static string InventoryEmpty => Catalog.Get("InventoryEmpty");
    public static string StatusNoBackend => Catalog.Get("StatusNoBackend");
    public static string StatusSearching => Catalog.Get("StatusSearching");
    public static string StatusReadingInstalled => Catalog.Get("StatusReadingInstalled");
    public static string StatusCheckingUpdates => Catalog.Get("StatusCheckingUpdates");
    public static string StatusUpToDate => Catalog.Get("StatusUpToDate");
    public static string TransactionDone => Catalog.Get("TransactionDone");
    public static string TransactionFailed => Catalog.Get("TransactionFailed");
    public static string TransactionNotAuthorized => Catalog.Get("TransactionNotAuthorized");
    public static string TransactionStopped => Catalog.Get("TransactionStopped");
    public static string TransactionStopping => Catalog.Get("TransactionStopping");
    public static string RestartPending => Catalog.Get("RestartPending");
    public static string UpdatesMayBeStale => Catalog.Get("UpdatesMayBeStale");
    public static string UpgradesEverything => Catalog.Get("UpgradesEverything");
    public static string FailureTitle => Catalog.Get("FailureTitle");
    public static string FailureStaleLists => Catalog.Get("FailureStaleLists");
    public static string ConflictsTitle => Catalog.Get("ConflictsTitle");
    public static string TypeLocalFile => Catalog.Get("TypeLocalFile");
    public static string RepositoryEnabled => Catalog.Get("RepositoryEnabled");
    public static string RepositoryDisabled => Catalog.Get("RepositoryDisabled");

    /// <summary>Nothing in the managed AppImage directory yet.</summary>
    public static string UserSoftwareEmpty(string directory) =>
        Catalog.Format("UserSoftwareEmpty", directory);

    /// <summary>An AppImage arrived.</summary>
    public static string UserSoftwareInstalled(string name) =>
        Catalog.Format("UserSoftwareInstalled", name);

    /// <summary>An AppImage went away.</summary>
    public static string UserSoftwareRemoved(string name) =>
        Catalog.Format("UserSoftwareRemoved", name);

    /// <summary>This package manager can list its repositories but not change them.</summary>
    public static string RepositoriesReadOnly(string backend) =>
        Catalog.Format("RepositoriesReadOnly", backend);

    /// <summary>The Available Software field named a path that is not there.</summary>
    public static string FileNotFound(string path) => Catalog.Format("FileNotFound", path);

    /// <summary>A package file is listed and waiting to be marked.</summary>
    public static string FileReady(string name) => Catalog.Format("FileReady", name);

    /// <summary>A command faulted where nothing should have thrown.</summary>
    public static string UnexpectedError(string message) => Catalog.Format("UnexpectedError", message);

    /// <summary>The validation gate turned a transaction down before it was ever run.</summary>
    public static string TransactionRefused(string reason) => Catalog.Format("TransactionRefused", reason);

    /// <summary>logind would not restart the machine.</summary>
    public static string RestartFailed(string reason) => Catalog.Format("RestartFailed", reason);

    /// <summary>How many packages Start will act on, listed or not.</summary>
    public static string MarkedCount(int count) => Catalog.Format("MarkedCount", count);

    /// <summary>The product information text, with the version.</summary>
    public static string About(string version) => Catalog.Format("About", version);

    /// <summary>A size in kilobytes, in the user's number format.</summary>
    public static string Kilobytes(long kilobytes) => Catalog.Format("Kilobytes", kilobytes);

    /// <summary>The idle status line: which distribution, and which package manager runs it.</summary>
    public static string StatusIdle(string distribution, string backend) =>
        Catalog.Format("StatusIdle", distribution, backend);

    /// <summary>How many packages a listing came back with.</summary>
    public static string StatusFound(int count) => Catalog.Format("StatusFound", count);

    /// <summary>A search that matched nothing.</summary>
    public static string StatusNothingFound(string query) => Catalog.Format("StatusNothingFound", query);

    /// <summary>Install mode with an empty Available Software field.</summary>
    public static string StatusNeedsQuery(string backend) => Catalog.Format("StatusNeedsQuery", backend);

    /// <summary>The package manager failed, and the log has the detail.</summary>
    public static string StatusQueryFailed(string backend) => Catalog.Format("StatusQueryFailed", backend);

    /// <summary>The localized name of a <see cref="Packages.Models.PackageStatus"/>.</summary>
    public static string PackageStatus(Packages.Models.PackageStatus status) =>
        Catalog.Get($"Status{status}");
}
