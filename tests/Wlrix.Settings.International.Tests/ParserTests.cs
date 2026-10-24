using System.Text;
using System.Xml.Linq;
using Wlrix.Settings.International.Core;
using Wlrix.Settings.International.Services;
using Xunit;

namespace Wlrix.Settings.International.Tests;

/// <summary>The readers of the system's own lists, against small cuts of the real files.</summary>
public class ParserTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>A GNU .mo file holding <paramref name="messages"/>, little-endian.</summary>
    internal static byte[] Mo(params (string Id, string Text)[] messages)
    {
        var all = messages.Prepend(("", "Content-Type: text/plain; charset=UTF-8\n")).ToArray();
        var headerSize = 28;
        var tables = all.Length * 8 * 2;
        var strings = new MemoryStream();
        var originals = new List<(int, int)>();
        var translations = new List<(int, int)>();
        foreach (var (id, _) in all)
        {
            var bytes = Encoding.UTF8.GetBytes(id);
            originals.Add((bytes.Length, headerSize + tables + (int)strings.Length));
            strings.Write(bytes);
            strings.WriteByte(0);
        }
        foreach (var (_, text) in all)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            translations.Add((bytes.Length, headerSize + tables + (int)strings.Length));
            strings.Write(bytes);
            strings.WriteByte(0);
        }

        var output = new MemoryStream();
        var writer = new BinaryWriter(output);
        writer.Write(0x950412deu);
        writer.Write(0);
        writer.Write(all.Length);
        writer.Write(headerSize);
        writer.Write(headerSize + all.Length * 8);
        writer.Write(0);
        writer.Write(0);
        foreach (var (length, offset) in originals.Concat(translations))
        {
            writer.Write(length);
            writer.Write(offset);
        }
        writer.Write(strings.ToArray());
        return output.ToArray();
    }

    [Fact]
    public void AMoCatalogTranslatesAndLeavesTheRestInEnglish()
    {
        var catalog = MoCatalog.Parse(Mo(("Japanese", "日本語"), ("layout\u0004German", "ドイツ語")));
        Assert.Equal("日本語", catalog.Translate("Japanese"));
        // A message with a context is found by its id alone.
        Assert.Equal("ドイツ語", catalog.Translate("German"));
        Assert.Equal("English (US)", catalog.Translate("English (US)"));
        // The header is not a message.
        Assert.Equal(2, catalog.Count);
    }

    [Fact]
    public void ANonCatalogIsRefused() =>
        Assert.Throws<InvalidDataException>(() => MoCatalog.Parse(new byte[64]));

    [Fact]
    public void TimeZonesComeWithTheirCountryAndPlace()
    {
        var zones = TimeZones.Parse(Fixture("zone.tab"));

        var tokyo = Assert.Single(zones, zone => zone.Id == "Asia/Tokyo");
        Assert.Equal("JP", tokyo.Country);
        // +353916+1394441: seconds included.
        Assert.Equal(35.6544, tokyo.Latitude, 3);
        Assert.Equal(139.7447, tokyo.Longitude, 3);

        var newYork = Assert.Single(zones, zone => zone.Id == "America/New_York");
        Assert.Equal(40.7142, newYork.Latitude, 3);
        Assert.Equal(-74.0064, newYork.Longitude, 3);
        Assert.Equal("America/New York (Eastern (most areas))", newYork.Display);

        // The country's most populous zone is first, which is what makes it the default.
        Assert.Equal("Europe/Berlin", zones.First(zone => zone.Country == "DE").Id);
    }

    [Theory]
    [InlineData("+4723+00832", 47.3833, 8.5333)]
    [InlineData("-3352+15113", -33.8667, 151.2167)]
    [InlineData("+404251-0740023", 40.7142, -74.0064)]
    public void CoordinatesAreIso6709(string text, double latitude, double longitude)
    {
        Assert.True(TimeZones.TryParseCoordinates(text, out var lat, out var lon));
        Assert.Equal(latitude, lat, 3);
        Assert.Equal(longitude, lon, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("4723+00832")]
    [InlineData("+47+008")]
    [InlineData("+4723+0083x")]
    public void NonsenseCoordinatesAreRefused(string text) =>
        Assert.False(TimeZones.TryParseCoordinates(text, out _, out _));

    [Fact]
    public void AZoneLandsOnTheMapByLongitudeAndLatitude()
    {
        var greenwich = new TimeZoneEntry("Europe/London", "GB", 0, 0, "");
        Assert.Equal((500.0, 250.0), greenwich.MapPoint(1000, 500));
        var corner = new TimeZoneEntry("x", "XX", 90, -180, "");
        Assert.Equal((0.0, 0.0), corner.MapPoint(1000, 500));
    }

    [Fact]
    public void CountriesFromIsoCodesPreferTheCommonName()
    {
        const string json = """
            {"3166-1": [
              {"alpha_2": "JP", "name": "Japan"},
              {"alpha_2": "KR", "name": "Korea, Republic of", "common_name": "South Korea"}
            ]}
            """;
        var countries = Countries.ParseIsoCodes(json, MoCatalog.Parse(Mo(("Japan", "日本"))));
        Assert.Equal([new Country("JP", "日本"), new Country("KR", "South Korea")], countries);
    }

    [Fact]
    public void TzdatasCountryListIsTheFallback()
    {
        var countries = Countries.ParseTzdata("# comment\nJP\tJapan\nUS\tUnited States\n");
        Assert.Equal([new Country("JP", "Japan"), new Country("US", "United States")], countries);
    }

    [Fact]
    public void OnlyUtf8LocalesAreListedAndGeneratedOnesAreMarked()
    {
        var supported = LocaleGen.ParseSupported(
            "de_DE.UTF-8 UTF-8\nde_DE ISO-8859-1\nja_JP.EUC-JP EUC-JP\nja_JP.UTF-8 UTF-8\nsr_RS@latin UTF-8\n");
        // What `locale -a` prints: the codeset normalized, and none for a name that had none.
        var locales = Locales.Build(supported, ["C", "C.utf8", "POSIX", "ja_JP.utf8", "sr_RS@latin", "de_DE"]);

        Assert.Equal(["de_DE.UTF-8", "ja_JP.UTF-8", "sr_RS@latin"], locales.Select(locale => locale.Name).Order());
        Assert.True(locales.Single(locale => locale.Name == "ja_JP.UTF-8").IsGenerated);
        Assert.True(locales.Single(locale => locale.Name == "sr_RS@latin").IsGenerated);
        // The ISO-8859-1 de_DE is generated, the UTF-8 one is not.
        Assert.False(locales.Single(locale => locale.Name == "de_DE.UTF-8").IsGenerated);
    }

    [Theory]
    [InlineData("ja_JP.UTF-8", "ja", "JP", "UTF-8", "")]
    [InlineData("sr_RS@latin", "sr", "RS", "", "latin")]
    [InlineData("ca_ES.UTF-8@valencia", "ca", "ES", "UTF-8", "valencia")]
    [InlineData("eo", "eo", "", "", "")]
    public void ALocaleNameComesApart(string name, string language, string territory, string codeset, string modifier) =>
        Assert.Equal((language, territory, codeset, modifier), Locales.Split(name));

    [Theory]
    [InlineData("ja_JP.UTF-8", "ja-JP")]
    [InlineData("sr_RS@latin", "sr-Latn-RS")]
    [InlineData("uz_UZ@cyrillic", "uz-Cyrl-UZ")]
    [InlineData("ca_ES@valencia", "ca-ES")]
    public void ALocaleBecomesACulture(string name, string culture) =>
        Assert.Equal(culture, Locales.CultureName(name));

    [Fact]
    public void TheKeyboardRegistryHasCountriesVariantsModelsAndSwitchKeys()
    {
        var registry = XkbRegistry.Parse(XDocument.Parse(Fixture("evdev.xml")), MoCatalog.Empty);

        var jp = Assert.Single(registry.Layouts, layout => layout.Name == "jp");
        Assert.Equal(["JP"], jp.Countries);
        Assert.True(jp.IsFor("JP"));
        Assert.False(jp.IsFor("US"));
        Assert.Contains(jp.Variants, variant => variant.Name == "kana");

        Assert.Contains(registry.Models, model => model.Name == "jp106");
        Assert.NotEmpty(registry.SwitchKeys);
        // Only the `grp` group: `ctrl:` options are not switch keys.
        Assert.All(registry.SwitchKeys, key => Assert.StartsWith("grp:", key.Name));
    }

    [Fact]
    public void KeyboardDescriptionsAreTranslated()
    {
        var registry = XkbRegistry.Parse(XDocument.Parse(Fixture("evdev.xml")),
            MoCatalog.Parse(Mo(("Japanese", "日本語"))));
        Assert.Equal("日本語", registry.Layout("jp")!.Description);
    }
}
