using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Wlrix.Settings.Displays.Tests;

/// <summary>
/// That every string the windows and the code ask for exists. A <c>{loc:Tr Foo}</c> whose key
/// is missing renders as the literal text <c>Foo</c>, with no error anywhere.
/// </summary>
public class LocalizationTests
{
    private static string Data(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Localization", name);

    private static IReadOnlySet<string> Catalog() =>
        XDocument.Load(Data("Strings.resx"))
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
        Assert.True(MarkupKeys().Count > 20, "the markup was not copied, or has lost its strings");

    [Fact]
    public void EveryKeyTheMarkupAsksForIsInTheCatalog()
    {
        var catalog = Catalog();
        var missing = MarkupKeys().Where(key => !catalog.Contains(key)).ToList();
        Assert.True(missing.Count == 0, "not in Strings.resx: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryKeyTheCodeAsksForIsInTheCatalog()
    {
        // Read through the same catalog the app uses: a missing key comes back as itself.
        string[] keys =
        [
            "Resolution", "ResolutionAspect", "PreferredResolution", "RefreshRate", "NoCompositor",
            "ApplyFailed", "ApplyCancelled", "Reverted", "RevertFailed", "DisplaysChanged",
            "HdrUnsupported", "AdaptiveSyncUnsupported", "RevertingIn",
        ];
        var catalog = Catalog();
        Assert.All(keys, key => Assert.Contains(key, catalog));
    }
}
