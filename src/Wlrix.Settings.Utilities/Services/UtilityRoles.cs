// SPDX-License-Identifier: GPL-3.0-or-later
using Wlrix.Common.Desktop;

namespace Wlrix.Settings.Utilities.Services;

/// <summary>How a role's choice is stored.</summary>
internal enum RoleKind
{
    /// <summary>As the default for a set of MIME types, in <c>mimeapps.list</c>.</summary>
    Mime,

    /// <summary>As the first line of <c>xdg-terminals.list</c>. No type opens in a terminal.</summary>
    Terminal,
}

/// <summary>
/// One of the panel's menus: a kind of application, and the types choosing one sets.
/// </summary>
/// <remarks>
/// A role is a name for a family of types, which is what KDE's Default Applications page is
/// too. Picking an image viewer makes it the default for every image type it says it opens,
/// not only the one the menu was built from: a viewer chosen for PNG and still not opening
/// JPEGs would be a surprise nobody wanted.
/// </remarks>
/// <param name="Key">The localization key of its label, and its identity.</param>
/// <param name="Group">The localization key of the heading it is under.</param>
/// <param name="PrimaryType">
/// The type that decides what the menu offers and what it shows as current.
/// </param>
/// <param name="CoreTypes">
/// Types always set, whether or not the chosen application claims them. A browser chosen
/// with Other… still has to receive the links.
/// </param>
/// <param name="InFamily">Which of the chosen application's own types the choice also covers.</param>
/// <param name="Kind">Where the choice is stored.</param>
internal sealed record UtilityRole(
    string Key,
    string Group,
    string PrimaryType,
    IReadOnlyList<string> CoreTypes,
    Func<string, bool> InFamily,
    RoleKind Kind = RoleKind.Mime)
{
    /// <summary>The types choosing <paramref name="entry"/> for this role sets.</summary>
    public IReadOnlyList<string> TypesFor(DesktopEntry entry) =>
        [.. CoreTypes.Concat(entry.MimeType.Where(InFamily)).Distinct(StringComparer.Ordinal)];

    /// <summary>Whether an entry belongs on this role's menu without being picked with Other….</summary>
    public bool Offers(DesktopEntry entry) => Kind switch
    {
        RoleKind.Terminal => entry.Categories.Contains("TerminalEmulator", StringComparer.Ordinal),
        _ => entry.MimeType.Contains(PrimaryType, StringComparer.Ordinal),
    };
}

/// <summary>The panel's menus, in the order it shows them.</summary>
/// <remarks>
/// IRIX's list, less the HTML editor, plus what a desktop now has to decide that IRIX did
/// not: a file manager, a terminal, an archiver, and players for music and video. Grouped as
/// KDE groups them.
/// </remarks>
internal static class UtilityRoles
{
    public const string Internet = "GroupInternet";
    public const string Documents = "GroupDocuments";
    public const string Multimedia = "GroupMultimedia";
    public const string Utilities = "GroupUtilities";

    /// <summary>The archive formats an archive manager is the default for.</summary>
    /// <remarks>
    /// A list rather than a prefix, because archives have none: they are scattered across
    /// <c>application/</c> among the word processors and the executables.
    /// </remarks>
    public static IReadOnlySet<string> ArchiveTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "application/zip",
        "application/x-tar",
        "application/x-compressed-tar",
        "application/x-bzip-compressed-tar",
        "application/x-bzip2-compressed-tar",
        "application/x-xz-compressed-tar",
        "application/x-zstd-compressed-tar",
        "application/x-lzma-compressed-tar",
        "application/x-7z-compressed",
        "application/vnd.rar",
        "application/x-rar",
        "application/gzip",
        "application/x-bzip",
        "application/x-bzip2",
        "application/x-xz",
        "application/zstd",
        "application/x-lzma",
        "application/x-cpio",
        "application/x-archive",
        "application/x-lha",
        "application/x-arj",
    };

    private static readonly IReadOnlySet<string> WebTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "x-scheme-handler/http", "x-scheme-handler/https", "text/html", "application/xhtml+xml",
    };

    public static IReadOnlyList<UtilityRole> All { get; } =
    [
        new("RoleBrowser", Internet, "x-scheme-handler/https",
            ["x-scheme-handler/http", "x-scheme-handler/https"], WebTypes.Contains),
        new("RoleMailer", Internet, "x-scheme-handler/mailto",
            ["x-scheme-handler/mailto"], static _ => false),

        new("RoleTextEditor", Documents, "text/plain", ["text/plain"], static _ => false),
        new("RoleDocumentViewer", Documents, "application/pdf",
            ["application/pdf"], static type => type is "application/postscript" or "application/x-dvi"),

        new("RoleImageViewer", Multimedia, "image/png", ["image/png"],
            static type => type.StartsWith("image/", StringComparison.Ordinal)),
        new("RoleMusicPlayer", Multimedia, "audio/mpeg", ["audio/mpeg"],
            static type => type.StartsWith("audio/", StringComparison.Ordinal)),
        new("RoleVideoPlayer", Multimedia, "video/mp4", ["video/mp4"],
            static type => type.StartsWith("video/", StringComparison.Ordinal)),

        new("RoleFileManager", Utilities, "inode/directory", ["inode/directory"], static _ => false),
        new("RoleTerminal", Utilities, string.Empty, [], static _ => false, RoleKind.Terminal),
        new("RoleArchiver", Utilities, "application/zip", ["application/zip"], ArchiveTypes.Contains),
    ];
}
