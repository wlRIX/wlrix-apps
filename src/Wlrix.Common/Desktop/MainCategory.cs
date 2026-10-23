namespace Wlrix.Common.Desktop;

/// <summary>
/// Maps a <c>.desktop</c> entry's <c>Categories</c> to the freedesktop registered *main*
/// categories we surface, in a fixed display order. Vendor/extra tags (Qt, KDE, GTK, X-*, ...)
/// are ignored; anything without a main category lands in <see cref="Other"/>.
/// </summary>
public static class MainCategory
{
    public const string Other = "Other";

    /// <summary>
    /// The categories we display, in display order (<see cref="Other"/> last).
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
    /// Every main category among <paramref name="categories"/>, each once and in
    /// <see cref="Order"/>; just <see cref="Other"/> when there is none.
    /// </summary>
    /// <remarks>
    /// All of them rather than the first, because an application can belong in more than one
    /// place: Steam declares both <c>Network</c> and <c>Game</c>, and is looked for under both.
    /// </remarks>
    public static IReadOnlyList<string> ClassifyAll(IReadOnlyList<string> categories)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var category in categories)
        {
            var mapped = Aliases.GetValueOrDefault(category, category);
            if (mapped != Other && Known.Contains(mapped))
                found.Add(mapped);
        }

        return found.Count == 0 ? [Other] : Order.Where(found.Contains).ToList();
    }
}
