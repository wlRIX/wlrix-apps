using Wlrix.Common.Localization;

namespace Wlrix.Files.Localization;

/// <summary>The application's strings.</summary>
/// <remarks>
/// Hand-written rather than generated, matching the other localized apps. A missing key comes
/// back as the key itself, which leaves the blemish visible in the window instead of throwing.
///
/// <para>
/// The base name is the <em>project</em> name, not the assembly name. Those differ here --
/// the assembly is <c>wlrix-files</c> -- and getting it wrong makes every string in the
/// window render as its own key, silently.
/// </para>
///
/// <para>
/// The members below are the strings C# reaches for. Static labels in AXAML go through
/// <c>{loc:Tr}</c> against <see cref="Catalog"/> directly and are not repeated here.
/// </para>
/// </remarks>
public static class Strings
{
    public static StringCatalog Catalog { get; } =
        new("Wlrix.Files.Localization.Strings", typeof(Strings).Assembly);

    public static string Files => Catalog.Get("Files");
    public static string ErrorTitle => Catalog.Get("ErrorTitle");
    public static string AboutTitle => Catalog.Get("AboutTitle");
    public static string ConnectTitle => Catalog.Get("ConnectTitle");
    public static string ConnectAuthTitle => Catalog.Get("ConnectAuthTitle");
    public static string AboutText => Catalog.Get("AboutText");
    public static string StatusLoading => Catalog.Get("StatusLoading");
    public static string StatusEmpty => Catalog.Get("StatusEmpty");

    public static string FindTitle => Catalog.Get("FindTitle");

    public static string FindPrompt(string directory) => Catalog.Format("FindPrompt", directory);

    public static string StatusSearching(int found) => Catalog.Format("StatusSearching", found);

    public static string StatusFound(int count, string text) => Catalog.Format("StatusFound", count, text);

    public static string StatusFoundPartial(int count, string text, int unreadable) =>
        Catalog.Format("StatusFoundPartial", count, text, unreadable);

    public static string StatusFoundTruncated(int count, string text) =>
        Catalog.Format("StatusFoundTruncated", count, text);

    public static string StatusFoundNothing(string text) => Catalog.Format("StatusFoundNothing", text);

    public static string StatusItems(int count) => Catalog.Format("StatusItems", count);

    public static string StatusItemsSelected(int count, int selected) =>
        Catalog.Format("StatusItemsSelected", count, selected);

    public static string StatusFree(string free, string total) =>
        Catalog.Format("StatusFree", free, total);

    public static string NewFolderTitle => Catalog.Get("NewFolderTitle");
    public static string NewFolderPrompt => Catalog.Get("NewFolderPrompt");
    public static string NewFolderDefault => Catalog.Get("NewFolderDefault");
    public static string RenameTitle => Catalog.Get("RenameTitle");
    public static string ConfirmDeleteTitle => Catalog.Get("ConfirmDeleteTitle");
    public static string ConflictTitle => Catalog.Get("ConflictTitle");
    public static string OperationCancel => Catalog.Get("OperationCancel");

    public static string RenamePrompt(string name) => Catalog.Format("RenamePrompt", name);
    public static string ConfirmDeleteOne(string name) => Catalog.Format("ConfirmDeleteOne", name);
    public static string ConfirmDeleteMany(int count) => Catalog.Format("ConfirmDeleteMany", count);
    public static string ConflictSummary(string name) => Catalog.Format("ConflictSummary", name);
    public static string OperationScanning(int count) => Catalog.Format("OperationScanning", count);
    public static string OperationFailed(int count) => Catalog.Format("OperationFailed", count);

    /// <summary>The dismissing verb on a dialog. Not OperationCancel, which is "abort a job".</summary>
    public static string DialogCancel => Catalog.Get("DialogCancel");

    /// <summary>The title of both confirmations that stand between a double-click and a program.</summary>
    public static string ExecuteTitle => Catalog.Get("ExecuteTitle");

    /// <summary>The first confirmation: what the file is, and whether to run it at all.</summary>
    public static string ExecuteQuestion(string name, string description) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture,
            Catalog.Get("ExecuteQuestion"), name, description);

    public static string ExecuteButton => Catalog.Get("ExecuteButton");

    public static string ExecuteTrustTitle => Catalog.Get("ExecuteTrustTitle");

    /// <summary>
    /// The second confirmation, shown only when the file is not executable yet, because
    /// agreeing to it is also agreeing to change the file.
    /// </summary>
    public static string ExecuteTrustMessage(string name) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture,
            Catalog.Get("ExecuteTrustMessage"), name);

    public static string ExecuteContinueButton => Catalog.Get("ExecuteContinueButton");

    /// <summary>Shown when the execute bit could not be set, so nothing was run.</summary>
    public static string ExecuteGrantFailed(string name, string problem) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture,
            Catalog.Get("ExecuteGrantFailed"), name, problem);

    /// <summary>Shown when a double-click has nothing to run.</summary>
    public static string NoHandler(string name, string mimeType) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture,
            Catalog.Get("NoHandler"), name, mimeType);

    /// <summary>The "and remember this" item at the foot of the Open With menu.</summary>
    public static string OpenWithAlways(string mimeType) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture,
            Catalog.Get("OpenWithAlways"), mimeType);

    /// <summary>The status line for a running operation.</summary>
    public static string Operation(global::Wlrix.Files.Core.Operations.OperationKind kind) => kind switch
    {
        global::Wlrix.Files.Core.Operations.OperationKind.Copy => Catalog.Get("OperationCopying"),
        global::Wlrix.Files.Core.Operations.OperationKind.Move => Catalog.Get("OperationMoving"),
        global::Wlrix.Files.Core.Operations.OperationKind.Trash => Catalog.Get("OperationTrashing"),
        global::Wlrix.Files.Core.Operations.OperationKind.Delete => Catalog.Get("OperationDeleting"),
        _ => Catalog.Get("StatusLoading")
    };

    /// <summary>The Kind column's text for an entry.</summary>
    public static string Kind(global::Wlrix.Files.Core.FileKind kind) => kind switch
    {
        global::Wlrix.Files.Core.FileKind.Directory => Catalog.Get("KindFolder"),
        global::Wlrix.Files.Core.FileKind.File => Catalog.Get("KindFile"),
        global::Wlrix.Files.Core.FileKind.Symlink => Catalog.Get("KindLink"),
        _ => Catalog.Get("KindSpecial")
    };

    /// <summary>The sidebar label for an XDG user-directory key.</summary>
    /// <remarks>
    /// Built at runtime from the key, so a naive grep for unused resources will not see
    /// these. They are used.
    /// </remarks>
    public static string Place(string xdgKey) => xdgKey switch
    {
        "XDG_DESKTOP_DIR" => Catalog.Get("PlaceDesktop"),
        "XDG_DOCUMENTS_DIR" => Catalog.Get("PlaceDocuments"),
        "XDG_DOWNLOAD_DIR" => Catalog.Get("PlaceDownloads"),
        "XDG_MUSIC_DIR" => Catalog.Get("PlaceMusic"),
        "XDG_PICTURES_DIR" => Catalog.Get("PlacePictures"),
        "XDG_VIDEOS_DIR" => Catalog.Get("PlaceVideos"),
        _ => xdgKey
    };

    // --- the properties window ---------------------------------------------

    public static string PropertiesTitle(string name) => Catalog.Format("PropertiesTitle", name);

    public static string PropertiesTitleMany(int count) => Catalog.Format("PropertiesTitleMany", count);

    public static string PropItemCount(int count) => Catalog.Format("PropItemCount", count);

    public static string PropKindFolder => Catalog.Get("PropKindFolder");

    public static string PropKindEmpty => Catalog.Get("PropKindEmpty");

    public static string SettingFailed(string why) => Catalog.Format("SettingFailed", why);

    // --- the devices rail ---------------------------------------------------

    public static string DeviceFree(string free, string total) => Catalog.Format("DeviceFree", free, total);

    public static string DeviceMountFailed(string label, string why) =>
        Catalog.Format("DeviceMountFailed", label, why);

    public static string DeviceUnmountFailed(string label, string why) =>
        Catalog.Format("DeviceUnmountFailed", label, why);

    public static string DeviceUnmounted(string label) => Catalog.Format("DeviceUnmounted", label);

    public static string PropCalculating => Catalog.Get("PropCalculating");

    public static string PropCounting(string size, long items) => Catalog.Format("PropCounting", size, items);

    public static string PropSizeExact(string size, long bytes) => Catalog.Format("PropSizeExact", size, bytes);

    public static string PropContents(long files, long directories) =>
        Catalog.Format("PropContents", FileCount(files), FolderCount(directories));

    public static string PropContentsPartial(long files, long directories, int unreadable) =>
        Catalog.Format("PropContentsPartial", FileCount(files), FolderCount(directories), unreadable);

    /// <summary>
    /// The two halves of the contents line, counted separately.
    /// </summary>
    /// <remarks>
    /// Separate keys for one rather than a single "{0} files", because the two counts are
    /// independent and a folder holding three files and one subfolder would otherwise read
    /// "3 files, 1 folders". Languages that do not inflect give the singular key the same
    /// value as the plural one, which costs nothing and keeps the composition identical
    /// everywhere.
    /// </remarks>
    private static string FileCount(long files) =>
        files == 1 ? Catalog.Get("PropFileCountOne") : Catalog.Format("PropFileCount", files);

    private static string FolderCount(long directories) =>
        directories == 1 ? Catalog.Get("PropFolderCountOne") : Catalog.Format("PropFolderCount", directories);

    public static string PropFreeOf(string free, string total) => Catalog.Format("PropFreeOf", free, total);
}
