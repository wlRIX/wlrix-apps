namespace Wlrix.Packages.AppStream;

/// <summary>Package name to product, for the rows of a listing.</summary>
public sealed class AppStreamIndex
{
    private readonly Dictionary<string, AppStreamProduct> _products;

    private AppStreamIndex(Dictionary<string, AppStreamProduct> products) => _products = products;

    /// <summary>The index of a system with no catalogs: every package has no product.</summary>
    public static AppStreamIndex Empty { get; } = new([]);

    /// <summary>How many packages have a product.</summary>
    public int Count => _products.Count;

    /// <summary>What is known about a package, or null when no catalog names it.</summary>
    public AppStreamProduct? Find(string package) => _products.GetValueOrDefault(package);

    /// <summary>Builds the index. Pure, so the rules below can be tested on their own.</summary>
    internal static AppStreamIndex Build(IEnumerable<AppStreamComponent> components)
    {
        var byPackage = new Dictionary<string, Dictionary<string, AppStreamComponent>>(StringComparer.Ordinal);

        foreach (var component in components)
        {
            foreach (var package in component.Packages)
            {
                if (!byPackage.TryGetValue(package, out var byId))
                    byPackage[package] = byId = new Dictionary<string, AppStreamComponent>(StringComparer.Ordinal);

                // Several catalogs can describe one component, as Debian's backports do. Higher
                // priority wins, and the first read wins a tie.
                if (!byId.TryGetValue(component.Id, out var existing) || component.Priority > existing.Priority)
                    byId[component.Id] = component;
            }
        }

        var products = new Dictionary<string, AppStreamProduct>(StringComparer.Ordinal);
        foreach (var (package, byId) in byPackage)
        {
            if (ProductOf(package, byId.Values.ToList()) is { } product)
                products[package] = product;
        }

        return new AppStreamIndex(products);
    }

    /// <summary>
    /// One application names its package, whatever else the package carries. Several leave the
    /// package's own name in place, and the one named after the package, or else the first by id,
    /// lends its icon and categories. With no application, one component of any other type names
    /// its package, and several, such as the fonts of a font collection, say nothing about it.
    /// </summary>
    private static AppStreamProduct? ProductOf(string package, List<AppStreamComponent> components)
    {
        var applications = components.Where(component => component.IsApplication).ToList();

        if (applications.Count == 1)
            return Named(applications[0]);

        if (applications.Count > 1)
        {
            var lender = applications
                .OrderByDescending(application => IsNamedAfter(application.Id, package))
                .ThenBy(application => application.Id, StringComparer.Ordinal)
                .First();

            return Named(lender) with { Name = null, Summary = null };
        }

        return components.Count == 1 ? Named(components[0]) : null;
    }

    private static AppStreamProduct Named(AppStreamComponent component) => new(component.Name, component.Summary,
        component.Categories, component.StockIcon, component.IconFiles);

    private static bool IsNamedAfter(string id, string package)
    {
        var name = id.EndsWith(".desktop", StringComparison.Ordinal) ? id[..^".desktop".Length] : id;
        return name[(name.LastIndexOf('.') + 1)..].Equals(package, StringComparison.OrdinalIgnoreCase);
    }
}
