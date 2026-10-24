using System.Text.Json;

namespace Wlrix.Settings.International.Services;

/// <summary>A country, by its ISO 3166 code, with its name in the UI language.</summary>
public sealed record Country(string Code, string Name);

/// <summary>
/// The countries the Country dialog lists.
/// </summary>
/// <remarks>
/// IRIX listed the countries SGI supported. Here the list is every ISO 3166 country, because the
/// point of choosing one is the same -- narrowing the other three lists to what fits it -- and
/// the data that does the narrowing (glibc's locales, xkeyboard-config's layouts, tzdata's zones)
/// covers the world.
/// <para>
/// Names come from iso-codes, the list every Linux desktop uses, translated through its own gettext
/// catalog. The common name is preferred where there is one ("South Korea" rather than "Korea,
/// Republic of"). Without iso-codes, tzdata's <c>iso3166.tab</c> is the fallback: English only, but
/// always installed.
/// </para>
/// </remarks>
internal static class Countries
{
    public const string IsoCodesPath = "/usr/share/iso-codes/json/iso_3166-1.json";
    public const string TzdataPath = "/usr/share/zoneinfo/iso3166.tab";

    /// <summary>Every country, sorted by name in the UI language.</summary>
    public static IReadOnlyList<Country> Load()
    {
        var translations = MoCatalog.Load("iso_3166-1");
        IReadOnlyList<Country> countries;
        try
        {
            countries = File.Exists(IsoCodesPath)
                ? ParseIsoCodes(File.ReadAllText(IsoCodesPath), translations)
                : ParseTzdata(File.ReadAllText(TzdataPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            countries = [];
        }
        return Sorted(countries);
    }

    /// <summary>iso-codes' <c>iso_3166-1.json</c>.</summary>
    public static IReadOnlyList<Country> ParseIsoCodes(string json, MoCatalog translations)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<Country>();
        foreach (var entry in document.RootElement.GetProperty("3166-1").EnumerateArray())
        {
            var code = entry.GetProperty("alpha_2").GetString();
            var name = entry.TryGetProperty("common_name", out var common)
                ? common.GetString()
                : entry.GetProperty("name").GetString();
            if (code is { Length: 2 } && name is { Length: > 0 })
                result.Add(new Country(code, translations.Translate(name)));
        }
        return result;
    }

    /// <summary>tzdata's <c>iso3166.tab</c>: a code, a tab, and the English name.</summary>
    public static IReadOnlyList<Country> ParseTzdata(string text) =>
    [
        .. text.Split('\n')
            .Where(line => line.Length > 3 && !line.StartsWith('#'))
            .Select(line => line.Split('\t'))
            .Where(fields => fields.Length >= 2 && fields[0].Length == 2)
            .Select(fields => new Country(fields[0], fields[1].Trim())),
    ];

    private static IReadOnlyList<Country> Sorted(IEnumerable<Country> countries) =>
        [.. countries.OrderBy(country => country.Name, StringComparer.CurrentCultureIgnoreCase)];
}
