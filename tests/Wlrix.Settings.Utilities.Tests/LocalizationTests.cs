using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Wlrix.Settings.Utilities.Tests;

/// <summary>
/// That every string the window and the code ask for exists, in English and in Japanese. A
/// <c>{loc:Tr Foo}</c> whose key is missing renders as the literal text <c>Foo</c>, with no
/// error anywhere.
/// </summary>
public class LocalizationTests
{
    private static string Data(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Localization", name);

    private static IReadOnlySet<string> Catalog(string file) =>
        XDocument.Load(Data(file))
            .Root!
            .Elements("data")
            .Select(element => element.Attribute("name")!.Value)
            .ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<string> MarkupKeys() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Localization"), "*.axaml")
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"\{loc:Tr\s+([A-Za-z0-9_]+)\s*\}"))
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToList();

    [Fact]
    public void TheMarkupIsScannedAtAll() =>
        Assert.True(MarkupKeys().Count > 15, "the markup was not copied, or has lost its strings");

    [Theory]
    [InlineData("Strings.resx")]
    [InlineData("Strings.ja.resx")]
    public void EveryKeyTheMarkupAsksForIsInTheCatalog(string file)
    {
        var catalog = Catalog(file);
        var missing = MarkupKeys().Where(key => !catalog.Contains(key)).ToList();
        Assert.True(missing.Count == 0, $"not in {file}: " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData("Strings.resx")]
    [InlineData("Strings.ja.resx")]
    public void EveryKeyTheCodeAsksForIsInTheCatalog(string file)
    {
        string[] keys =
        [
            "Other", "None", "ChooserTitle", "AddTypeTitle", "AddTypePrompt", "AddPatternTitle", "AddPatternPrompt",
            "CloseTitle", "ClosePrompt", "Apply", "Discard", "ApplyFailed", "InvalidType", "TypeExists",
            .. Services.UtilityRoles.All.Select(static role => role.Key),
            .. Services.UtilityRoles.All.Select(static role => role.Group).Distinct(),
        ];
        var catalog = Catalog(file);
        Assert.All(keys, key => Assert.Contains(key, catalog));
    }

    [Fact]
    public void TheTwoCatalogsHaveTheSameKeys() =>
        Assert.Equal(Catalog("Strings.resx").Order(), Catalog("Strings.ja.resx").Order());
}
