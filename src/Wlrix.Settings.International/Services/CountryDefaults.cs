namespace Wlrix.Settings.International.Services;

/// <summary>What choosing a country suggests for the other three buttons. Any of them may be null.</summary>
public sealed record CountrySuggestion(LocaleEntry? Locale, XkbLayout? Layout, TimeZoneEntry? TimeZone);

/// <summary>
/// The defaults the Country button proposes, as IRIX's did: choosing Japan set the language to
/// Japanese, the keyboard to Japanese and the time zone to Japan in one go.
/// </summary>
internal static class CountryDefaults
{
    /// <summary>
    /// A country's main language, where its code is not the same as the country's. Most are
    /// (<c>de_DE</c>, <c>fr_FR</c>); for the rest, glibc lists several locales in the country and
    /// alphabetical order would pick wrongly (<c>es_US</c> before <c>en_US</c>). Only the commonly
    /// chosen ones are here; any other country falls back to its first locale.
    /// </summary>
    private static readonly Dictionary<string, string> MainLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = "en", ["GB"] = "en", ["AU"] = "en", ["CA"] = "en", ["IE"] = "en", ["NZ"] = "en",
        ["ZA"] = "en", ["IN"] = "en", ["SG"] = "en", ["PH"] = "en",
        ["JP"] = "ja", ["CN"] = "zh", ["TW"] = "zh", ["HK"] = "zh", ["KR"] = "ko",
        ["AT"] = "de", ["CH"] = "de", ["BE"] = "nl", ["BR"] = "pt", ["MX"] = "es", ["AR"] = "es",
        ["CL"] = "es", ["CO"] = "es", ["CZ"] = "cs", ["DK"] = "da", ["SE"] = "sv", ["GR"] = "el",
        ["IL"] = "he", ["UA"] = "uk", ["VN"] = "vi", ["EE"] = "et", ["SI"] = "sl", ["RS"] = "sr",
        ["IR"] = "fa", ["PK"] = "ur", ["BD"] = "bn", ["NO"] = "nb", ["EG"] = "ar", ["SA"] = "ar",
        ["AE"] = "ar", ["MA"] = "ar", ["DZ"] = "ar",
    };

    /// <summary>
    /// A country's usual keyboard, where it is not the layout named after the country. Almost all
    /// are (<c>jp</c>, <c>de</c>, <c>fr</c>).
    /// </summary>
    private static readonly Dictionary<string, string> MainLayout = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AU"] = "us", ["NZ"] = "us", ["CA"] = "us",
        ["MX"] = "latam", ["AR"] = "latam", ["CL"] = "latam", ["CO"] = "latam",
    };

    public static CountrySuggestion Suggest(
        string country,
        IEnumerable<LocaleEntry> locales,
        IEnumerable<XkbLayout> layouts,
        IEnumerable<TimeZoneEntry> zones)
    {
        var inCountry = locales.Where(locale => string.Equals(locale.Territory, country, StringComparison.OrdinalIgnoreCase)
                                                 && Locales.Split(locale.Name).Modifier.Length == 0)
            .OrderBy(locale => locale.Name, StringComparer.Ordinal)
            .ToList();
        var language = MainLanguage.TryGetValue(country, out var main) ? main : country.ToLowerInvariant();
        var locale = inCountry.FirstOrDefault(entry => entry.Language == language) ?? inCountry.FirstOrDefault();

        var forCountry = layouts.Where(layout => layout.Countries.Contains(country, StringComparer.OrdinalIgnoreCase)).ToList();
        var layoutName = MainLayout.TryGetValue(country, out var usual) ? usual : country.ToLowerInvariant();
        var layout = forCountry.FirstOrDefault(entry => entry.Name == layoutName)
                     ?? layouts.FirstOrDefault(entry => entry.Name == layoutName)
                     ?? forCountry.FirstOrDefault();

        // zone.tab lists a country's most populous zone first.
        var zone = zones.FirstOrDefault(entry => string.Equals(entry.Country, country, StringComparison.OrdinalIgnoreCase));

        return new CountrySuggestion(locale, layout, zone);
    }
}
