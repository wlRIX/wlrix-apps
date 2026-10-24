using System.Xml.Linq;

namespace Wlrix.Settings.International.Services;

/// <summary>A keyboard layout variant: <c>jp(kana)</c>'s <c>kana</c>.</summary>
public sealed record XkbVariant(string Name, string Description, IReadOnlyList<string> Countries);

/// <summary>A keyboard layout, its variants, and the countries and languages it is for.</summary>
public sealed record XkbLayout(
    string Name,
    string Description,
    IReadOnlyList<string> Countries,
    IReadOnlyList<string> Languages,
    IReadOnlyList<XkbVariant> Variants)
{
    /// <summary>Whether this layout, or one of its variants, is listed for <paramref name="country"/>.</summary>
    public bool IsFor(string country) =>
        Countries.Contains(country, StringComparer.OrdinalIgnoreCase)
        || Variants.Any(variant => variant.Countries.Contains(country, StringComparer.OrdinalIgnoreCase));
}

/// <summary>A keyboard model, or an xkb option such as a layout-switch key.</summary>
public sealed record XkbItem(string Name, string Description)
{
    public string Display => $"{Description} ({Name})";
}

/// <summary>
/// xkeyboard-config's registry, <c>evdev.xml</c>: the layouts, models and options every X and Wayland
/// desktop offers, with the countries each layout is for.
/// </summary>
/// <remarks>
/// The XML rather than the <c>.lst</c> file Keyboard Settings used to read: only the XML says which
/// countries a layout is for, which is what the Country button filters by. Descriptions are
/// translated through xkeyboard-config's own gettext catalog.
/// </remarks>
internal sealed class XkbRegistry
{
    public const string Path = "/usr/share/X11/xkb/rules/evdev.xml";

    /// <summary>The option group whose members are layout-switch keys.</summary>
    public const string SwitchGroup = "grp";

    public IReadOnlyList<XkbLayout> Layouts { get; private init; } = [];
    public IReadOnlyList<XkbItem> Models { get; private init; } = [];

    /// <summary>The <c>grp:</c> options: which key, or which chord, switches between layouts.</summary>
    public IReadOnlyList<XkbItem> SwitchKeys { get; private init; } = [];

    /// <summary>A registry with nothing in it, for when xkeyboard-config is not installed.</summary>
    public static XkbRegistry Empty { get; } = new();

    public static XkbRegistry Load()
    {
        try
        {
            return Parse(XDocument.Load(Path), MoCatalog.Load("xkeyboard-config"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return Empty;
        }
    }

    public static XkbRegistry Parse(XDocument document, MoCatalog translations)
    {
        var root = document.Root ?? throw new System.Xml.XmlException("empty registry");

        string Describe(XElement item) => translations.Translate(item.Element("description")?.Value.Trim() ?? string.Empty);

        IReadOnlyList<string> Codes(XElement item, string list, string code) =>
        [
            .. item.Element(list)?.Elements(code).Select(element => element.Value.Trim()) ?? [],
        ];

        var layouts = root.Element("layoutList")?.Elements("layout")
            .Select(layout =>
            {
                var item = layout.Element("configItem")!;
                var variants = layout.Element("variantList")?.Elements("variant")
                    .Select(variant => variant.Element("configItem")!)
                    .Select(variantItem => new XkbVariant(
                        variantItem.Element("name")!.Value.Trim(),
                        Describe(variantItem),
                        Codes(variantItem, "countryList", "iso3166Id")))
                    .ToList() ?? [];
                return new XkbLayout(
                    item.Element("name")!.Value.Trim(),
                    Describe(item),
                    Codes(item, "countryList", "iso3166Id"),
                    Codes(item, "languageList", "iso639Id"),
                    variants);
            })
            .OrderBy(layout => layout.Description, StringComparer.CurrentCultureIgnoreCase)
            .ToList() ?? [];

        var models = root.Element("modelList")?.Elements("model")
            .Select(model => model.Element("configItem")!)
            .Select(item => new XkbItem(item.Element("name")!.Value.Trim(), Describe(item)))
            .OrderBy(model => model.Description, StringComparer.CurrentCultureIgnoreCase)
            .ToList() ?? [];

        var switchKeys = root.Element("optionList")?.Elements("group")
            .Where(group => group.Element("configItem")?.Element("name")?.Value.Trim() == SwitchGroup)
            .SelectMany(group => group.Elements("option"))
            .Select(option => option.Element("configItem")!)
            .Select(item => new XkbItem(item.Element("name")!.Value.Trim(), Describe(item)))
            .ToList() ?? [];

        return new XkbRegistry { Layouts = layouts, Models = models, SwitchKeys = switchKeys };
    }

    public XkbLayout? Layout(string name) => Layouts.FirstOrDefault(layout => layout.Name == name);
}
