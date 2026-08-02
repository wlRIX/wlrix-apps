namespace Wlrix.Toolchest.Applications;

/// <summary>
/// Maps a <c>.desktop</c> entry's <c>Categories</c> to one of the freedesktop registered *main*
/// categories we surface, in a fixed menu order. Vendor/extra tags (Qt, KDE, GTK, X-*, ...) are
/// ignored; anything without a main category lands in <see cref="Other"/>. The ids double as the
/// suffix of the localized display key (<c>Category_&lt;id&gt;</c>) in the string resources.
/// </summary>
public static class MainCategory
{
    public const string Other = "Other";

    /// <summary>
    /// The categories we display, in menu order (<see cref="Other"/> last).
    /// </summary>
    public static IReadOnlyList<string> Order { get; } =
    [
        "AudioVideo", "Development", "Education", "Game", "Graphics",
        "Network", "Office", "Science", "Settings", "System", "Utility", Other
    ];

    // Additional categories that imply a main one when the main one isn't listed explicitly.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["Audio"] = "AudioVideo",
        ["Video"] = "AudioVideo"
    };

    private static readonly HashSet<string> Known = new(Order, StringComparer.Ordinal);

    /// <summary>
    /// Returns the first main category among <paramref name="categories"/>, else <see cref="Other"/>.
    /// </summary>
    public static string Classify(IReadOnlyList<string> categories)
    {
        foreach (var category in categories)
        {
            var mapped = Aliases.GetValueOrDefault(category, category);
            if (mapped != Other && Known.Contains(mapped))
                return mapped;
        }

        return Other;
    }
}
