using Wlrix.Common.Localization;

namespace Wlrix.Archiver.Localization;

/// <summary>The application's strings.</summary>
/// <remarks>
/// Hand-written rather than generated, matching the other localized apps: the resx designer
/// output is a large file nobody reads and it has to be regenerated on every string added.
/// A missing key comes back as the key itself, which leaves the blemish visible in the window
/// instead of throwing.
///
/// The menu uses <c>{loc:Tr}</c> against <see cref="Catalog"/> directly; the members here are
/// for the strings that reach a message dialog from C#.
/// </remarks>
public static class Strings
{
    public static StringCatalog Catalog { get; } =
        new("Wlrix.Archiver.Localization.Strings", typeof(Strings).Assembly);

    public static string Archiver => Catalog.Get("Archiver");
    public static string OpenTitle => Catalog.Get("OpenTitle");
    public static string ExtractTitle => Catalog.Get("ExtractTitle");
    public static string FilterArchives => Catalog.Get("FilterArchives");
    public static string FilterAllFiles => Catalog.Get("FilterAllFiles");
    public static string AboutTitle => Catalog.Get("AboutTitle");
    public static string ConfirmRemoveTitle => Catalog.Get("ConfirmRemoveTitle");
    public static string ErrorTitle => Catalog.Get("ErrorTitle");
    public static string EmptyNoArchive => Catalog.Get("EmptyNoArchive");
    public static string EmptyArchive => Catalog.Get("EmptyArchive");
    public static string NewNotImplemented => Catalog.Get("NewNotImplemented");

    public static string About(string version, bool hasSevenZip) => Catalog.Format(
        hasSevenZip ? "AboutMessage" : "AboutMessageWithoutSevenZip", version);

    public static string ConfirmRemove(string what) => Catalog.Format("ConfirmRemove", what);
    public static string Unsupported(string name) => Catalog.Format("ErrorUnsupported", name);
    public static string ReadOnly(string format) => Catalog.Format("ErrorReadOnly", format);
    public static string OpenFailed(string name) => Catalog.Format("ErrorOpenFailed", name);
    public static string ExtractFailed(string name) => Catalog.Format("ErrorExtractFailed", name);
    public static string AddFailed(string name) => Catalog.Format("ErrorAddFailed", name);
    public static string RemoveFailed(string name) => Catalog.Format("ErrorRemoveFailed", name);
    public static string ExtractedTo(string what, string where) =>
        Catalog.Format("ExtractedTo", what, where);

    public static string ExtractedCountTo(int count, string where) =>
        Catalog.Format("ExtractedCountTo", count, where);

    public static string Cancel => Catalog.Get("Cancel");
    public static string StatusCanceled => Catalog.Get("StatusCanceled");
    public static string StatusExtracting => Catalog.Get("StatusExtracting");

    public static string Decompressing(string name) => Catalog.Format("StatusDecompressing", name);
    public static string Reading(string name) => Catalog.Format("StatusReading", name);
    public static string ReadingCount(string name, int count) =>
        Catalog.Format("StatusReadingCount", name, count);
    public static string ExtractingCount(int count) =>
        Catalog.Format("StatusExtractingCount", count);
    public static string Saving(string name) => Catalog.Format("StatusSaving", name);

    /// <summary>"Automatic (Japanese (Shift-JIS))" — what the View menu shows once it has guessed.</summary>
    public static string AutomaticDetected(string encoding) =>
        Catalog.Format("EncodingAutomaticDetected", encoding);
}
