using System.Text;
using System.Xml;
using Wlrix.Packages.AppStream;
using Xunit;

namespace Wlrix.Packages.Tests;

public class AppStreamParsingTests
{
    private static ParsedCatalog ReadXml(string xml, string language = "en-US") =>
        CatalogXml.Read(new MemoryStream(Encoding.UTF8.GetBytes(xml)), language);

    private static ParsedCatalog ArchFixture(string language = "en-US") =>
        ReadXml(Fixture.Read("appstream-arch.xml"), language);

    private static ParsedCatalog DebianFixture(string language = "en-US") =>
        Dep11Yaml.Read(new StringReader(Fixture.Read("appstream-debian.yml")), language);

    [Fact]
    public void Xml_ReadsARecordedComponent()
    {
        var catalog = ArchFixture();
        var gimp = catalog.Components.Single(component => component.Packages.Contains("gimp"));

        Assert.Equal("archlinux-arch-extra", catalog.Origin);
        Assert.Equal("org.gimp.GIMP", gimp.Id);
        Assert.True(gimp.IsApplication);
        Assert.Equal("gimp", gimp.StockIcon);
        Assert.Equal("Graphics", gimp.Categories[0]);
        Assert.Contains(new CachedIcon("gimp_gimp.jxl", 48, 48, 1), gimp.CachedIcons);
    }

    [Fact]
    public void Xml_IgnoresTheNameInsideDeveloper()
    {
        // <developer> has a <name> of its own, in dozens of languages, and comes after the
        // product's. Reading any descendant would call GIMP "The GIMP team".
        var gimp = ArchFixture().Components.Single(component => component.Id == "org.gimp.GIMP");

        Assert.Equal("GNU Image Manipulation Program", gimp.Name);
    }

    [Fact]
    public void Xml_PicksTheUsersLanguage()
    {
        var gimp = ArchFixture("ja-JP").Components.Single(component => component.Id == "org.gimp.GIMP");

        Assert.Equal("GIMP (GNU Image Manipulation Program)", gimp.Name);
        Assert.StartsWith("画像", gimp.Summary);
    }

    [Fact]
    public void Xml_TheRecordedCatalogFollowsTheIndexRules()
    {
        var index = AppStreamIndex.Build(ArchFixture().Components);

        Assert.Equal("GNU Image Manipulation Program", index.Find("gimp")?.Name);
        Assert.Equal("Code::Blocks IDE", index.Find("codeblocks")?.Name);
        Assert.Null(index.Find("hugin")?.Name);
        Assert.Equal("hugin", index.Find("hugin")?.StockIcon);
        Assert.Null(index.Find("gsfonts"));
    }

    [Fact]
    public void Xml_SkipsMergeComponentsAndThoseWithNoPackage()
    {
        var catalog = ReadXml("""
            <components origin="o">
              <component merge="append"><id>a</id><pkgname>a</pkgname></component>
              <component type="desktop-application"><id>b</id><name>B</name></component>
              <component type="desktop-application"><id>c</id><name>C</name><pkgname></pkgname></component>
              <component type="addon" priority="7"><id>d</id><name>D</name><pkgname>d1</pkgname><pkgname>d2</pkgname></component>
            </components>
            """);

        var only = Assert.Single(catalog.Components);
        Assert.Equal(["d1", "d2"], only.Packages);
        Assert.Equal(7, only.Priority);
    }

    [Theory]
    [InlineData("../../../etc/shadow")]
    [InlineData("/etc/shadow")]
    [InlineData("..")]
    public void Xml_RefusesIconNamesThatLeaveTheDirectory(string name)
    {
        var catalog = ReadXml($"""
            <components origin="o">
              <component><id>a</id><pkgname>a</pkgname>
                <icon type="cached" width="64" height="64">{name}</icon>
                <icon type="stock">{name}</icon>
              </component>
            </components>
            """);

        Assert.Empty(catalog.Components[0].CachedIcons);
        Assert.Null(catalog.Components[0].StockIcon);
    }

    [Fact]
    public void Xml_AcceptsAnEmptyCatalog()
    {
        // core.xml.gz in the real package holds exactly this.
        Assert.Empty(ReadXml("<components version=\"1.0\" origin=\"o\">\n</components>").Components);
        Assert.Empty(ReadXml("<components origin=\"o\"/>").Components);
    }

    [Fact]
    public void Xml_RefusesADocumentType()
    {
        Assert.Throws<XmlException>(() => ReadXml(
            "<!DOCTYPE components [<!ENTITY x \"y\">]><components origin=\"o\"/>"));
    }

    [Fact]
    public void Xml_RefusesSomethingElseAltogether()
    {
        Assert.Throws<InvalidDataException>(() => ReadXml("<html/>"));
    }

    [Fact]
    public void Yaml_ReadsAComponent()
    {
        var catalog = DebianFixture();
        var gimp = catalog.Components[0];

        Assert.Equal("debian-trixie-main", catalog.Origin);
        Assert.Equal("org.gimp.GIMP", gimp.Id);
        Assert.Equal(["gimp"], gimp.Packages);
        Assert.Equal("GNU Image Manipulation Program", gimp.Name);
        Assert.Equal("High-end image creation and manipulation", gimp.Summary);
        Assert.Equal(["Graphics", "2DGraphics"], gimp.Categories);
        Assert.Equal("gimp", gimp.StockIcon);
        Assert.Equal(5, gimp.Priority);
        Assert.True(gimp.IsApplication);
    }

    [Fact]
    public void Yaml_ReadsCachedIconsWhateverOrderTheirKeysAreIn()
    {
        var gimp = DebianFixture().Components[0];

        Assert.Equal(
            [new CachedIcon("gimp_gimp.png", 48, 48, 1), new CachedIcon("gimp_gimp.png", 64, 64, 2)],
            gimp.CachedIcons);
    }

    [Theory]
    [InlineData("ja-JP", "GIMP: 画像")]
    [InlineData("pt-BR", "Programa de Manipulação: o 'GIMP'")]
    [InlineData("sr-RS", "GIMP latinicom")]
    [InlineData("de-DE", "GNU Image Manipulation Program")]
    public void Yaml_UnderstandsQuotingAndLocaleKeys(string language, string expected)
    {
        Assert.Equal(expected, DebianFixture(language).Components[0].Name);
    }

    [Fact]
    public void Yaml_DoesNotReadFieldsOutOfADescription()
    {
        Assert.Equal(["gimp"], DebianFixture().Components[0].Packages);
    }

    [Fact]
    public void Yaml_ReadsTheOtherIconFormAndRefusesAPathForAStockName()
    {
        var plugin = DebianFixture().Components[1];

        Assert.Equal("Example \"Plugin\"", plugin.Name);
        Assert.Null(plugin.StockIcon);
        Assert.Equal([new CachedIcon("example_plugin.png", 0, 0, 1)], plugin.CachedIcons);
    }

    [Fact]
    public void Yaml_SkipsMergeComponentsAndThoseWithNoPackage()
    {
        Assert.Equal(2, DebianFixture().Components.Count);
    }

    [Fact]
    public void Yaml_RefusesAFileWithNoHeader()
    {
        Assert.Throws<InvalidDataException>(() =>
            Dep11Yaml.Read(new StringReader("---\nID: a\nPackage: a\n"), "en-US"));
    }

    [Theory]
    [InlineData(null, "ja-JP", 1)]
    [InlineData("C", "ja-JP", 1)]
    [InlineData("ja", "ja-JP", 2)]
    [InlineData("ja-JP", "ja-JP", 3)]
    [InlineData("ja_JP", "ja-JP", 3)]
    [InlineData("de", "ja-JP", 0)]
    [InlineData("zh-Hant-TW", "zh-CN", 0)]
    [InlineData("zh-Hans-CN", "zh-CN", 3)]
    [InlineData("en-GB", "en-US", 0)]
    [InlineData("ja", "", 0)]
    public void LanguageRank_FollowsTheTag(string? tag, string language, int expected)
    {
        Assert.Equal(expected, CatalogRules.LanguageRank(tag, language));
    }
}
