// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Wlrix.Writer.Tests;

/// <summary>That the Japanese strings say everything the English ones do.</summary>
/// <remarks>
/// Each check here is a mistake that shows nowhere but in the running window: a missing key
/// falls back to English, a lost <c>{0}</c> drops the file name from a message, and a lost or
/// changed access key leaves an Alt combination dead or doubled in a menu.
/// </remarks>
public partial class LocalizationTests
{
    private static IReadOnlyDictionary<string, string> Catalog(string name) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Localization", name))
            .Root!
            .Elements("data")
            .ToDictionary(element => element.Attribute("name")!.Value,
                element => element.Element("value")?.Value ?? "", StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> English = Catalog("Strings.resx");
    private static readonly IReadOnlyDictionary<string, string> Japanese = Catalog("Strings.ja.resx");

    [Fact]
    public void BothCatalogsHaveTheSameKeys()
    {
        Assert.True(English.Count > 50, "the English catalog was not copied, or has lost its strings");
        Assert.Equal(English.Keys.Order(), Japanese.Keys.Order());
    }

    [Fact]
    public void EveryPlaceholderSurvivesTranslation() =>
        Assert.All(English, pair =>
            Assert.Equal(Placeholders(pair.Value), Placeholders(Japanese[pair.Key])));

    [Fact]
    public void EveryAccessKeyIsKeptAndKeepsItsLetter() =>
        Assert.All(English, pair =>
            Assert.Equal(AccessKey(pair.Value), AccessKey(Japanese[pair.Key])));

    [Fact]
    public void NothingIsLeftInEnglish()
    {
        // The same in both languages on purpose: a name, a button word Japanese borrows, a mark.
        string[] alike = ["DialogOk", "TabModified"];
        var untranslated = English
            .Where(pair => !alike.Contains(pair.Key) && Japanese[pair.Key] == pair.Value)
            .Select(pair => pair.Key)
            .ToList();
        Assert.True(untranslated.Count == 0, "still English: " + string.Join(", ", untranslated));
    }

    private static string[] Placeholders(string value) =>
        [.. PlaceholderPattern().Matches(value).Select(match => match.Value).Order()];

    private static char? AccessKey(string value) =>
        AccessKeyPattern().Match(value) is { Success: true } match
            ? char.ToUpperInvariant(match.Groups[1].Value[0])
            : null;

    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex PlaceholderPattern();

    // An underscore before a letter, which Avalonia shows underlined and binds to Alt.
    [GeneratedRegex(@"_([A-Za-z0-9])")]
    private static partial Regex AccessKeyPattern();
}
