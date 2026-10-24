using System.Xml.Linq;
using Wlrix.Settings.International.Core;
using Wlrix.Settings.International.Services;
using Xunit;

namespace Wlrix.Settings.International.Tests;

/// <summary>What the panel writes: the keyboard strings, locale.conf, locale.gen, and the country defaults.</summary>
public class SettingTests
{
    [Fact]
    public void LayoutsAndVariantsPairUpByPosition()
    {
        var keyboard = KeyboardSetting.Parse("us,jp", ",kana", "pc105", "grp:alt_shift_toggle,ctrl:nocaps");

        Assert.Equal([new LayoutChoice("us", ""), new LayoutChoice("jp", "kana")], keyboard.Layouts);
        Assert.Equal("grp:alt_shift_toggle", keyboard.SwitchKey);
        Assert.Equal(["ctrl:nocaps"], keyboard.OtherOptions);

        // ...and back again, unchanged.
        Assert.Equal("us,jp", keyboard.Layout);
        Assert.Equal(",kana", keyboard.Variant);
        Assert.Equal("grp:alt_shift_toggle,ctrl:nocaps", keyboard.Options);
    }

    [Fact]
    public void NoVariantsIsNotARowOfCommas()
    {
        var keyboard = KeyboardSetting.Parse("us,jp", "", "pc105", "grp:alt_shift_toggle");
        Assert.Equal("", keyboard.Variant);
    }

    [Fact]
    public void OneLayoutHasNoSwitchKeyButKeepsOtherOptions()
    {
        var keyboard = KeyboardSetting.Parse("jp", "", "jp106", "grp:alt_shift_toggle,ctrl:nocaps");
        Assert.Equal("ctrl:nocaps", keyboard.Options);
    }

    [Fact]
    public void NothingSetIsUsOnAGeneric105KeyBoard()
    {
        var keyboard = KeyboardSetting.Parse(null, null, null, null);
        Assert.Equal("us", keyboard.Layout);
        Assert.Equal("pc105", keyboard.Model);
        Assert.Equal("", keyboard.Options);
    }

    [Fact]
    public void TwoReadsOfTheSameKeyboardAreEqual()
    {
        Assert.Equal(KeyboardSetting.Parse("us,jp", "", "pc105", "grp:alt_shift_toggle"),
            KeyboardSetting.Parse("us, jp", ",", "pc105", "grp:alt_shift_toggle"));
        Assert.NotEqual(KeyboardSetting.Parse("us,jp", "", "pc105", ""), KeyboardSetting.Parse("jp,us", "", "pc105", ""));
    }

    [Fact]
    public void LocaleConfKeepsLinesItDoesNotOwn()
    {
        const string existing = "# mine\nLANG=en_US.UTF-8\nLC_COLLATE=C\nLC_TIME=en_GB.UTF-8\n";

        var written = LocaleConf.Write(existing, "ja_JP.UTF-8", formats: null);

        Assert.Equal("LANG=ja_JP.UTF-8\n# mine\nLC_COLLATE=C\n", written);
    }

    [Fact]
    public void FormatsAreWrittenOnlyWhenTheyDifferFromTheLanguage()
    {
        var withFormats = LocaleConf.Write("", "en_US.UTF-8", "de_DE.UTF-8");
        var values = LocaleConf.Parse(withFormats);
        Assert.Equal("en_US.UTF-8", values["LANG"]);
        Assert.All(LocaleConf.FormatVariables, variable => Assert.Equal("de_DE.UTF-8", values[variable]));
        Assert.Equal("de_DE.UTF-8", LocaleConf.FormatsOf(values));

        var same = LocaleConf.Write("", "en_US.UTF-8", "en_US.UTF-8");
        Assert.Equal("LANG=en_US.UTF-8\n", same);
        Assert.Null(LocaleConf.FormatsOf(LocaleConf.Parse(same)));
    }

    [Fact]
    public void LocaleConfReadsQuotesAndExports()
    {
        var values = LocaleConf.Parse("export LANG=\"ja_JP.UTF-8\"\nLC_TIME='C'\n");
        Assert.Equal("ja_JP.UTF-8", values["LANG"]);
        Assert.Equal("C", values["LC_TIME"]);
    }

    [Fact]
    public void FormatsThatDisagreeAreNotOneChoice() =>
        Assert.Null(LocaleConf.FormatsOf(new Dictionary<string, string>
        {
            ["LANG"] = "en_US.UTF-8", ["LC_TIME"] = "de_DE.UTF-8", ["LC_NUMERIC"] = "fr_FR.UTF-8",
            ["LC_MONETARY"] = "de_DE.UTF-8", ["LC_PAPER"] = "de_DE.UTF-8", ["LC_MEASUREMENT"] = "de_DE.UTF-8",
        }));

    private static readonly SupportedLocale German = new("de_DE.UTF-8", "UTF-8");

    [Fact]
    public void EnablingUncommentsTheStockLine()
    {
        const string gen = "# Configuration file for locale-gen\n#de_DE.UTF-8 UTF-8  \n#de_DE ISO-8859-1  \n";
        Assert.Equal("# Configuration file for locale-gen\nde_DE.UTF-8 UTF-8\n#de_DE ISO-8859-1  \n",
            LocaleGen.Enable(gen, German));
    }

    [Fact]
    public void EnablingAppendsWhenThereIsNoLine() =>
        Assert.Equal("en_US.UTF-8 UTF-8\nde_DE.UTF-8 UTF-8\n", LocaleGen.Enable("en_US.UTF-8 UTF-8\n", German));

    [Fact]
    public void AlreadyEnabledAnywhereIsNoChange()
    {
        // As on the machine this was written on: the stock line still commented, an enabled copy
        // appended at the bottom by hand.
        const string gen = "#de_DE.UTF-8 UTF-8  \nen_US.UTF-8 UTF-8\nde_DE.UTF-8 UTF-8\n";
        Assert.Equal(gen, LocaleGen.Enable(gen, German));
        Assert.True(LocaleGen.IsEnabled(gen, German));
    }

    [Fact]
    public void TheHeaderProseIsNeverALocale()
    {
        // Two words after the #, exactly like a locale line would be, but not this locale.
        const string gen = "# de_DE.UTF-8 is German\n";
        Assert.False(LocaleGen.IsEnabled(gen, German));
        Assert.EndsWith("de_DE.UTF-8 UTF-8\n", LocaleGen.Enable(gen, German));
    }

    [Theory]
    [InlineData("ja_JP.UTF-8", true)]
    [InlineData("sr_RS@latin", true)]
    [InlineData("../etc/passwd", false)]
    [InlineData("de_DE UTF-8", false)]
    [InlineData("x;rm -rf", false)]
    [InlineData("", false)]
    public void OnlyLocaleShapedNamesGetThrough(string name, bool ok) =>
        Assert.Equal(ok, LocaleGen.IsWellFormed(name));

    private static readonly IReadOnlyList<LocaleEntry> SomeLocales =
    [
        new("en_US.UTF-8", "UTF-8", true, "English (United States)"),
        new("es_US.UTF-8", "UTF-8", false, "Spanish (United States)"),
        new("ja_JP.UTF-8", "UTF-8", true, "Japanese (Japan)"),
        new("de_DE.UTF-8", "UTF-8", false, "German (Germany)"),
        new("dsb_DE", "UTF-8", false, "Lower Sorbian (Germany)"),
        new("nds_DE", "UTF-8", false, "Low German (Germany)"),
    ];

    private static XkbRegistry Registry() =>
        XkbRegistry.Parse(XDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "evdev.xml"))),
            MoCatalog.Empty);

    private static IReadOnlyList<TimeZoneEntry> Zones() =>
        TimeZones.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "zone.tab")));

    [Theory]
    [InlineData("JP", "ja_JP.UTF-8", "jp", "Asia/Tokyo")]
    [InlineData("US", "en_US.UTF-8", "us", "America/New_York")]
    [InlineData("DE", "de_DE.UTF-8", "de", "Europe/Berlin")]
    public void ACountrySuggestsItsLanguageKeyboardAndZone(string country, string locale, string layout, string zone)
    {
        var suggestion = CountryDefaults.Suggest(country, SomeLocales, Registry().Layouts, Zones());
        Assert.Equal(locale, suggestion.Locale?.Name);
        Assert.Equal(layout, suggestion.Layout?.Name);
        Assert.Equal(zone, suggestion.TimeZone?.Id);
    }
}
