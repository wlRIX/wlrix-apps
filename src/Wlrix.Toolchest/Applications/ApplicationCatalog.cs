using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wlrix.Common;
using Wlrix.Toolchest.Desktop;
using ZLogger;

namespace Wlrix.Toolchest.Applications;

/// <summary>
/// Provides the categorized application <see cref="Catalog"/>, cached per UI culture under
/// <c>&lt;AppData&gt;/toolchest/catalog.&lt;culture&gt;.json</c>. The cache is written on first
/// build; <see cref="RebuildAsync"/> forces a rescan (the future "programs changed" hook).
/// </summary>
public interface IApplicationCatalog
{
    /// <summary>
    /// Returns the cached catalog if present, otherwise builds and caches it.
    /// </summary>
    Task<Catalog> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rescans the <c>.desktop</c> files and overwrites the cache.
    /// </summary>
    Task<Catalog> RebuildAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc/>
public sealed class ApplicationCatalog(ILogger<ApplicationCatalog> logger) : IApplicationCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <inheritdoc/>
    public async Task<Catalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = CachePath();
        if (File.Exists(path))
        {
            try
            {
                await using var stream = File.OpenRead(path);
                var cached = await JsonSerializer.DeserializeAsync<Catalog>(stream, JsonOptions, cancellationToken);
                if (cached is not null)
                {
                    logger.ZLogInformation($"Loaded application catalog cache: {path}");
                    return cached;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                logger.ZLogWarning(ex, $"Failed to read catalog cache {path}; rebuilding.");
            }
        }

        return await RebuildAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<Catalog> RebuildAsync(CancellationToken cancellationToken = default)
    {
        var catalog = await Task.Run(Build, cancellationToken);
        await WriteCacheAsync(catalog, cancellationToken);
        return catalog;
    }

    private Catalog Build()
    {
        var culture = CultureInfo.CurrentUICulture;
        var entries = new DesktopEntryScanner().Scan();

        var groups = new Dictionary<string, List<CatalogApp>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var name = DesktopEntryParser.ResolveLocalized(entry.Name, culture);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var comment = DesktopEntryParser.ResolveLocalized(entry.Comment, culture);
            var app = new CatalogApp(name, entry.Exec!, entry.Terminal, string.IsNullOrEmpty(comment) ? null : comment);

            var categoryId = MainCategory.Classify(entry.Categories);
            if (!groups.TryGetValue(categoryId, out var list))
                groups[categoryId] = list = [];
            list.Add(app);
        }

        var byName = StringComparer.Create(culture, ignoreCase: true);
        var categories = MainCategory.Order
            .Where(groups.ContainsKey)
            .Select(id => new CatalogCategory(id, groups[id].OrderBy(a => a.Name, byName).ToList()))
            .ToList();

        logger.ZLogInformation(
            $"Build application catalog: {entries.Count} entries across {categories.Count} categories.");
        return new Catalog(categories);
    }

    private async Task WriteCacheAsync(Catalog catalog, CancellationToken cancellationToken)
    {
        var path = CachePath();
        try
        {
            ApplicationPaths.EnsureDirectory(Path.GetDirectoryName(path)!);
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, catalog, JsonOptions, cancellationToken);
            logger.ZLogInformation($"Wrote application catalog cache: {path}");
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            logger.ZLogWarning(ex, $"Failed to write catalog cache {path}.");
        }
    }

    private static string CachePath()
    {
        var culture = CultureInfo.CurrentUICulture.Name;
        if (string.IsNullOrEmpty(culture))
            culture = "invariant";

        return Path.Combine(ApplicationPaths.AppData, "toolchest", $"catalog.{culture}.json");
    }
}
