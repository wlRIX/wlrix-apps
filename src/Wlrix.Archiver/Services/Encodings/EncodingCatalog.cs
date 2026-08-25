using System.Text;

namespace Wlrix.Archiver.Services.Encodings;

/// <summary>One filename encoding the user can pick from the View menu.</summary>
/// <param name="Id">A stable key for settings and resource lookup; <c>auto</c> for detection.</param>
/// <param name="CodePage">The .NET code page, or <c>0</c> for automatic.</param>
/// <param name="DisplayName">What the menu shows. Not localized: these are proper names.</param>
public sealed record FilenameEncoding(string Id, int CodePage, string DisplayName)
{
    /// <summary>Whether this is the "work it out" entry rather than a fixed encoding.</summary>
    public bool IsAutomatic => CodePage == 0;

    /// <summary>The encoding itself, or <c>null</c> for <see cref="IsAutomatic"/>.</summary>
    public Encoding? Resolve() => IsAutomatic ? null : Encoding.GetEncoding(CodePage);
}

/// <summary>The encodings offered for archive filenames, and the provider they need.</summary>
public static class EncodingCatalog
{
    private static bool _registered;

    /// <summary>
    /// Makes the legacy code pages available. Safe to call more than once.
    /// </summary>
    /// <remarks>
    /// .NET exposes only UTF-8, UTF-16, UTF-32, ASCII and Latin-1 by default. Without this call
    /// <c>Encoding.GetEncoding(932)</c> throws and every Shift-JIS archive is unreadable. The
    /// provider itself is in the shared framework on net10.0 — no package reference needed, and
    /// adding one is an NU1510 build error — but it is still opt-in at runtime, and nothing else
    /// in the process opts in for us.
    /// </remarks>
    public static void Register()
    {
        if (_registered)
            return;

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _registered = true;
    }

    /// <summary>Work it out from the bytes. The default.</summary>
    public static FilenameEncoding Automatic { get; } = new("auto", 0, "Automatic");

    /// <summary>
    /// The candidates, in the order the menu lists them and the order detection tries them.
    /// </summary>
    /// <remarks>
    /// This is not every code page .NET knows, deliberately: a menu of two hundred encodings is
    /// not a menu. These are the ones that actually turn up in archives — the CJK code pages
    /// that predate UTF-8 in zip, DOS CP437 from the era zip was defined in, and CP1252 for the
    /// Western European files that get mistaken for it.
    ///
    /// <b>The order is load-bearing.</b> Detection breaks ties by taking the first, so the more
    /// restrictive code pages come first: CP932 rejects the most byte sequences and GBK the
    /// fewest — it accepts very nearly the whole lead-trail space — so a page accepting a name
    /// at all is stronger evidence the earlier it appears here. EUC-JP sits after the DBCS pages
    /// for that reason and one more: it is a Unix encoding and zip is not, so an EUC-JP zip is
    /// rare enough that it should not win a tie against a Chinese one.
    /// </remarks>
    public static IReadOnlyList<FilenameEncoding> All { get; } =
    [
        Automatic,
        new("utf-8", 65001, "UTF-8"),
        new("cp932", 932, "Japanese (Shift-JIS)"),
        new("cp949", 949, "Korean (EUC-KR)"),
        new("cp936", 936, "Simplified Chinese (GBK)"),
        new("cp950", 950, "Traditional Chinese (Big5)"),
        new("euc-jp", 51932, "Japanese (EUC-JP)"),
        new("cp866", 866, "Cyrillic (DOS)"),
        new("cp1251", 1251, "Cyrillic (Windows)"),
        new("cp437", 437, "Western European (DOS)"),
        new("cp1252", 1252, "Western European (Windows)"),
    ];

    /// <summary>The entry with <paramref name="id"/>, or <see cref="Automatic"/>.</summary>
    public static FilenameEncoding ById(string? id) =>
        All.FirstOrDefault(entry => entry.Id == id) ?? Automatic;
}
