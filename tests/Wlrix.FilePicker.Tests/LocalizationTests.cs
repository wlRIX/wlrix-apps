using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Wlrix.FilePicker.Tests;

/// <summary>That every string the dialog asks for exists, in both languages.</summary>
/// <remarks>
/// The trap this exists for: a <c>{loc:Tr Foo}</c> whose key is not in the catalog renders as
/// the literal text <c>Foo</c>. No exception, no warning, no log line. A dialog that says
/// "ButtonCancel" on its cancel button is the failure, and the only way to notice it otherwise
/// is to open the thing in the right language.
/// </remarks>
public class LocalizationTests
{
    private static string Data(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Localization", name);

    private static IReadOnlyDictionary<string, string> Catalog(string file) =>
        XDocument.Load(Data(file))
            .Root!
            .Elements("data")
            .Where(element => element.Attribute("name") is not null)
            .ToDictionary(
                element => element.Attribute("name")!.Value,
                element => element.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal);

    /// <summary>Every key the markup asks the catalog for.</summary>
    private static IReadOnlyList<(string Key, string File)> MarkupKeys()
    {
        var keys = new List<(string, string)>();
        foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Localization"), "*.axaml"))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(path), @"\{loc:Tr\s+([A-Za-z0-9_]+)\s*\}"))
                keys.Add((match.Groups[1].Value, Path.GetFileName(path)));
        }

        return keys;
    }

    /// <summary>Every key the code asks for, which the markup scan cannot see.</summary>
    /// <remarks>
    /// The titles, the accept labels and the status lines are all built in
    /// <c>Localization/Strings.cs</c> and never appear in the markup. They are listed here
    /// rather than scanned because a name built from a <c>switch</c> is not a literal the
    /// regular expression above could find.
    /// </remarks>
    private static readonly string[] CodeKeys =
    [
        "TitleOpen", "TitleOpenFolder", "TitleSave", "TitleSaveFiles",
        "AcceptOpen", "AcceptSave",
        "PlaceHome", "PlaceDesktop", "PlaceDocuments", "PlaceDownloads",
        "PlaceMusic", "PlacePictures", "PlaceVideos",
        "StatusLoading", "StatusEmpty", "StatusItems",
        "SaveFilesNote", "ConfirmTitle", "ConfirmOverwrite", "ErrorTitle",
    ];

    [Fact]
    public void TheMarkupIsScannedAtAllRatherThanQuietlyFindingNothing()
    {
        // Without this the tests below pass trivially the day the copy-to-output rule breaks,
        // and go on passing while the catalog rots.
        Assert.True(MarkupKeys().Count > 5, "no localized keys were found in the markup");
    }

    [Fact]
    public void EveryKeyTheDialogAsksForIsInTheNeutralCatalog()
    {
        var catalog = Catalog("Strings.resx");
        var missing = MarkupKeys()
            .Select(entry => (entry.Key, Where: entry.File))
            .Concat(CodeKeys.Select(key => (Key: key, Where: "Strings.cs")))
            .Where(entry => !catalog.ContainsKey(entry.Key))
            .Select(entry => $"{entry.Key} ({entry.Where})")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0, "not in Strings.resx: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheJapaneseCatalogHasEveryKeyTheNeutralOneDoes()
    {
        var neutral = Catalog("Strings.resx");
        var japanese = Catalog("Strings.ja.resx");

        var missing = neutral.Keys.Where(key => !japanese.ContainsKey(key)).ToList();
        Assert.True(missing.Count == 0, "not translated: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheJapaneseCatalogHasNoKeysTheNeutralOneHasLost()
    {
        // The other direction, which is how a translation outlives the string it translated:
        // the key is renamed in the neutral catalog and the old entry sits in the Japanese one
        // for ever, translated and unreachable.
        var neutral = Catalog("Strings.resx");
        var japanese = Catalog("Strings.ja.resx");

        var orphans = japanese.Keys.Where(key => !neutral.ContainsKey(key)).ToList();
        Assert.True(orphans.Count == 0, "no longer in Strings.resx: " + string.Join(", ", orphans));
    }

    [Theory]
    [InlineData("Strings.resx")]
    [InlineData("Strings.ja.resx")]
    public void NoTwoControlsInTheDialogShareAMnemonic(string catalog)
    {
        // Four labels carry one, and they are all on screen at once: two under the same letter
        // means one of them cannot be reached from the keyboard. Invisible in the Japanese
        // catalog, where the letter sits in parentheses after the label.
        //
        // The application's own accept label is deliberately not counted. It is the requesting
        // application's word with the requesting application's underline, and nothing here can
        // change it -- what this checks is that wlRIX's own four do not collide among
        // themselves or with either of the two defaults.
        var strings = Catalog(catalog);
        string[] keys = ["ButtonCancel", "ButtonUp", "ShowHidden", "AcceptOpen", "AcceptSave"];

        var byLetter = new Dictionary<char, List<string>>();
        foreach (var key in keys)
        {
            if (Mnemonic(strings.GetValueOrDefault(key, "")) is not { } letter)
                continue;
            byLetter.TryAdd(letter, []);
            byLetter[letter].Add(key);
        }

        // AcceptOpen and AcceptSave are never shown together, so only one may share with the
        // rest at a time.
        var collisions = byLetter
            .Where(entry => entry.Value.Count > 1
                            && !(entry.Value.Count == 2 && entry.Value.All(key => key.StartsWith("Accept", StringComparison.Ordinal))))
            .Select(entry => $"{entry.Key}: {string.Join(" + ", entry.Value)}")
            .ToList();

        Assert.True(collisions.Count == 0, string.Join("; ", collisions));
    }

    /// <summary>The mnemonic letter an underscore marks, or null if there is none.</summary>
    private static char? Mnemonic(string label)
    {
        var match = Regex.Match(label, "_(.)");
        return match.Success ? char.ToUpperInvariant(match.Groups[1].Value[0]) : null;
    }
}
