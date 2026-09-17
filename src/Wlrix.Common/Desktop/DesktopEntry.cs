namespace Wlrix.Common.Desktop;

/// <summary>
/// One <c>[Desktop Action <em>id</em>]</c> group: an extra verb an application offers
/// beyond simply launching, such as "New Window" or "New Private Window".
/// </summary>
/// <param name="Id">The action id, as it appeared in <c>Actions=</c>.</param>
/// <param name="Name">Localized display names, keyed as in <see cref="DesktopEntry.Name"/>.</param>
/// <param name="Exec">The command to run.</param>
/// <param name="Icon">An icon name or absolute path, if the action names its own.</param>
public sealed record DesktopAction(
    string Id,
    IReadOnlyDictionary<string, string> Name,
    string? Exec,
    string? Icon);

/// <summary>
/// A parsed freedesktop <c>.desktop</c> entry. Localized fields keep every
/// <c>key[locale]</c> variant (with <see cref="string.Empty"/> as the key for the
/// unlocalized value) so the display locale can be resolved later via
/// <see cref="DesktopEntryParser.ResolveLocalized"/>.
/// </summary>
public sealed class DesktopEntry
{
    /// <summary>The desktop-file id (path under an <c>applications</c> dir, separators → '-').</summary>
    public required string Id { get; init; }

    public string? Type { get; init; }

    public required IReadOnlyDictionary<string, string> Name { get; init; }

    public IReadOnlyDictionary<string, string> Comment { get; init; } =
        new Dictionary<string, string>();

    public string? Exec { get; init; }

    public string? TryExec { get; init; }

    public IReadOnlyList<string> Categories { get; init; } = [];

    public bool Terminal { get; init; }

    public bool NoDisplay { get; init; }

    public bool Hidden { get; init; }

    public IReadOnlyList<string> OnlyShowIn { get; init; } = [];

    public IReadOnlyList<string> NotShowIn { get; init; } = [];

    /// <summary>An icon name to look up in the icon theme, or an absolute path.</summary>
    /// <remarks>
    /// The menu never needed this because it draws its own glyphs; a file manager
    /// showing "Open With" does.
    /// </remarks>
    public string? Icon { get; init; }

    /// <summary>The MIME types this application declares it can open.</summary>
    /// <remarks>
    /// The reverse index built from these is what answers "what can open this file",
    /// which is the whole of the Open With menu.
    /// </remarks>
    public IReadOnlyList<string> MimeType { get; init; } = [];

    /// <summary>Search keywords, beyond the name and comment.</summary>
    public IReadOnlyDictionary<string, string> Keywords { get; init; } =
        new Dictionary<string, string>();

    /// <summary>The working directory to launch in, if the entry asks for one.</summary>
    public string? Path { get; init; }

    /// <summary>
    /// The window class this application's windows will report.
    /// </summary>
    /// <remarks>
    /// How <c>wlrix-desktop</c> matches a running window back to the launcher that
    /// started it, which is why every wlRIX entry sets it to its Wayland app id.
    /// </remarks>
    public string? StartupWmClass { get; init; }

    /// <summary>The extra verbs from <c>Actions=</c>, in the order declared.</summary>
    public IReadOnlyList<DesktopAction> Actions { get; init; } = [];

    /// <summary>The file this was parsed from, when it came from disk.</summary>
    /// <remarks>Needed for the <c>%k</c> field code, and to re-read the file at launch.</remarks>
    public string? FilePath { get; init; }
}
