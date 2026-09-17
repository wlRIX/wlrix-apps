using Wlrix.Common.Localization;
using Wlrix.Files.Core.Portal;

namespace Wlrix.FilePicker.Localization;

/// <summary>
/// Strongly-typed access to the dialog's localized strings, backed by <c>Strings.resx</c> and
/// its culture satellites.
/// </summary>
/// <remarks>
/// The static labels come through <c>{loc:Tr}</c>, which reads the same <see cref="Catalog"/>.
/// What is here is what code has to build — and what the application supplies is deliberately
/// not here: the title and the accept label are the requesting application's own words, already
/// in its language, and translating them would be putting ours over theirs.
/// </remarks>
public static class Strings
{
    /// <summary>The resources behind these, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.FilePicker.Localization.Strings", typeof(Strings).Assembly);

    /// <summary>The window title, for an application that did not give one.</summary>
    public static string DefaultTitle(FileChooserMode mode, bool directory) => mode switch
    {
        FileChooserMode.Save => Catalog.Get("TitleSave"),
        FileChooserMode.SaveFiles => Catalog.Get("TitleSaveFiles"),
        _ => Catalog.Get(directory ? "TitleOpenFolder" : "TitleOpen"),
    };

    /// <summary>The accept button's label, for an application that did not give one.</summary>
    public static string DefaultAccept(FileChooserMode mode) =>
        Catalog.Get(mode == FileChooserMode.Open ? "AcceptOpen" : "AcceptSave");

    /// <summary>How many rows are showing.</summary>
    public static string Items(int count) => Catalog.Format("StatusItems", count);

    /// <summary>The overwrite question, naming the file it is about.</summary>
    public static string ConfirmOverwrite(string name) => Catalog.Format("ConfirmOverwrite", name);

    /// <summary>A place in the rail, by its <c>user-dirs.dirs</c> key.</summary>
    /// <remarks>
    /// The same names the file manager's rail uses, so the two rails read alike. A key with no
    /// translation falls back to its directory's own name rather than to the key, which is a
    /// path component and would at least be recognizable.
    /// </remarks>
    public static string PlaceName(string xdgKey) => xdgKey switch
    {
        "XDG_DESKTOP_DIR" => Catalog.Get("PlaceDesktop"),
        "XDG_DOCUMENTS_DIR" => Catalog.Get("PlaceDocuments"),
        "XDG_DOWNLOAD_DIR" => Catalog.Get("PlaceDownloads"),
        "XDG_MUSIC_DIR" => Catalog.Get("PlaceMusic"),
        "XDG_PICTURES_DIR" => Catalog.Get("PlacePictures"),
        "XDG_VIDEOS_DIR" => Catalog.Get("PlaceVideos"),
        _ => xdgKey,
    };
}
