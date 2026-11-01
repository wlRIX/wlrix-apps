namespace Wlrix.Packages.AppStream;

/// <summary>An icon in a catalog's icon cache, by file name rather than by location.</summary>
/// <param name="FileName">The file name, already checked to be a single path segment.</param>
/// <param name="Width">Its width, or zero when the catalog did not say.</param>
/// <param name="Height">Its height, or zero when the catalog did not say.</param>
/// <param name="Scale">Its HiDPI scale.</param>
internal sealed record CachedIcon(string FileName, int Width, int Height, int Scale);

/// <summary>One software component as a catalog describes it.</summary>
/// <param name="Id">The component's id, unique across catalogs.</param>
/// <param name="Kind">The type, such as <c>desktop-application</c>; empty when not declared.</param>
/// <param name="Packages">The packages that provide it.</param>
/// <param name="Name">The name in the user's language, or null.</param>
/// <param name="Summary">The one-line summary in the user's language, or null.</param>
/// <param name="Categories">XDG categories, in the catalog's order.</param>
/// <param name="StockIcon">An icon-theme name, already checked to be a single path segment.</param>
/// <param name="CachedIcons">What the icon cache holds for it.</param>
/// <param name="Priority">Decides between catalogs that describe the same component.</param>
internal sealed record AppStreamComponent(
    string Id,
    string Kind,
    IReadOnlyList<string> Packages,
    string? Name,
    string? Summary,
    IReadOnlyList<string> Categories,
    string? StockIcon,
    IReadOnlyList<CachedIcon> CachedIcons,
    int Priority)
{
    /// <summary>The cached icons as files, in order of preference. Set once the catalog's location is known.</summary>
    public IReadOnlyList<string> IconFiles { get; init; } = [];

    /// <summary>Whether this is a graphical application rather than an add-on, a font and so on.</summary>
    public bool IsApplication => Kind == "desktop-application";
}

/// <summary>What one catalog file holds.</summary>
/// <param name="Origin">The catalog's origin, which names its directory in the icon cache.</param>
/// <param name="Components">Its components, in file order.</param>
internal sealed record ParsedCatalog(string Origin, IReadOnlyList<AppStreamComponent> Components);

/// <summary>
/// What the Software Inventory shows for a package that has AppStream metadata.
/// </summary>
/// <param name="Name">
/// The product's name, or null when the package holds several applications and no one of them
/// names it: <c>libreoffice-fresh</c> is not "LibreOffice Writer".
/// </param>
/// <param name="Summary">The one-line summary, null in the same case as the name.</param>
/// <param name="Categories">XDG categories, in the catalog's order.</param>
/// <param name="StockIcon">An icon-theme name, or null.</param>
/// <param name="IconFiles">Files in the icon cache, in order of preference. They may not exist.</param>
public sealed record AppStreamProduct(
    string? Name,
    string? Summary,
    IReadOnlyList<string> Categories,
    string? StockIcon,
    IReadOnlyList<string> IconFiles)
{
    /// <summary>
    /// The first of the catalog's categories that is an XDG main category, for the Category
    /// column, or null. The rest are additional categories like <c>RasterGraphics</c>, and there
    /// are too many to translate. Audio and Video collapse into AudioVideo, which the desktop
    /// specification requires alongside either.
    /// </summary>
    public string? MainCategory
    {
        get
        {
            foreach (var category in Categories)
            {
                switch (category)
                {
                    case "AudioVideo" or "Audio" or "Video":
                        return "AudioVideo";
                    case "Development" or "Education" or "Game" or "Graphics" or "Network" or "Office"
                        or "Science" or "Settings" or "System" or "Utility":
                        return category;
                }
            }

            return null;
        }
    }
}

/// <summary>The rules every catalog format shares.</summary>
internal static class CatalogRules
{
    /// <summary>
    /// Whether a name from a catalog is safe to join onto a directory, or to hand to the icon
    /// theme, which takes an absolute path as it stands.
    /// </summary>
    public static bool IsPathSegment(string? name) =>
        !string.IsNullOrEmpty(name) && name is not ("." or "..") && name.IndexOfAny(['/', '\0']) < 0;

    /// <summary>
    /// How well a catalog's language tag suits the user's language: 3 for the same language and
    /// region, 2 for the same language with a region on at most one side, 1 for the untranslated
    /// text, 0 for no use. Tags arrive as <c>pt-BR</c> and <c>zh-Hans-CN</c> in XML and as
    /// <c>pt_BR</c> and <c>sr@latin</c> in YAML. Two different regions do not match, so a
    /// mainland Chinese system does not get a Taiwanese name.
    /// </summary>
    public static int LanguageRank(string? tag, string language)
    {
        if (string.IsNullOrEmpty(tag) || tag == "C")
            return 1;

        var (tagLanguage, tagRegion) = SplitTag(tag);
        var (wanted, wantedRegion) = SplitTag(language);

        if (!tagLanguage.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            return 0;

        if (tagRegion is null || wantedRegion is null)
            return 2;

        return tagRegion.Equals(wantedRegion, StringComparison.OrdinalIgnoreCase) ? 3 : 0;
    }

    private static (string Language, string? Region) SplitTag(string tag)
    {
        var parts = tag.Split('@', '.')[0].Split('-', '_');
        return (parts[0], parts.Length > 1 && parts[^1].Length == 2 ? parts[^1] : null);
    }
}

/// <summary>Keeps whichever translation of a field best suits the user's language.</summary>
internal struct BestTranslation(string language)
{
    private int _rank;

    public string? Value { get; private set; }

    public void Offer(string? tag, string text)
    {
        var rank = CatalogRules.LanguageRank(tag, language);
        if (rank <= _rank || text.Length == 0)
            return;

        _rank = rank;
        Value = text;
    }
}
