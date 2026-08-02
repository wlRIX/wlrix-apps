namespace Wlrix.Toolchest.Desktop;

/// <summary>
/// A parsed freedesktop <c>.desktop</c> entry (the <c>[Desktop Entry]</c> group). Localized
/// fields keep every <c>key[locale]</c> variant (with <see cref="string.Empty"/> as the key for
/// the unlocalized value) so the display locale can be resolved later via
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
}
