namespace Wlrix.Toolchest.Applications;

/// <summary>
/// A launchable application, with its display name already resolved for a culture.
/// </summary>
public sealed record CatalogApp(string Name, string Exec, bool Terminal, string? Comment);

/// <summary>
/// A main category and its apps (sorted by localized name).
/// </summary>
public sealed record CatalogCategory(string Id, IReadOnlyList<CatalogApp> Apps);

/// <summary>
/// The categorized application catalog persisted to the per-culture cache file.
/// </summary>
public sealed record Catalog(IReadOnlyList<CatalogCategory> Categories)
{
    public static Catalog Empty { get; } = new([]);
}
