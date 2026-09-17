using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Wlrix.Files.Tests;

/// <summary>
/// That every string the window asks for exists, in both languages.
/// </summary>
/// <remarks>
/// The trap this exists for: a <c>{loc:Tr Foo}</c> whose key is not in the catalog renders as
/// the literal text <c>Foo</c>. No exception, no warning, no log line — the menu just says
/// "SelectedMakeReference" and the only way to find out is to look at the window in the right
/// language. A typo in a key, or a key added to the neutral catalog and not the Japanese one,
/// both fail exactly that way.
///
/// <para>
/// The markup and the catalogs are copied into the test output as data, so this reads what the
/// application actually ships rather than a second copy that could drift from it.
/// </para>
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

    [Fact]
    public void TheMarkupIsScannedAtAllRatherThanQuietlyFindingNothing()
    {
        // Without this the two tests below pass trivially the day the copy-to-output rule
        // breaks, and go on passing while the catalog rots.
        var keys = MarkupKeys();
        Assert.True(keys.Count > 30, $"only {keys.Count} localized keys found in the markup");
    }

    [Fact]
    public void EveryKeyTheMarkupAsksForIsInTheNeutralCatalog()
    {
        var catalog = Catalog("Strings.resx");
        var missing = MarkupKeys()
            .Where(entry => !catalog.ContainsKey(entry.Key))
            .Select(entry => $"{entry.Key} ({entry.File})")
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

    [Fact]
    public void NoJapaneseValueIsStillTheEnglishOne()
    {
        // A key copied across and not translated. Allowed where the two genuinely coincide --
        // a format string that is only punctuation, a proper noun -- so this only flags values
        // that contain Latin letters and no Japanese at all.
        var neutral = Catalog("Strings.resx");
        var japanese = Catalog("Strings.ja.resx");

        var untranslated = japanese
            .Where(entry => neutral.TryGetValue(entry.Key, out var english)
                            && string.Equals(english, entry.Value, StringComparison.Ordinal)
                            && Regex.IsMatch(entry.Value, "[A-Za-z]{3,}")
                            && !HasJapanese(entry.Value))
            .Select(entry => entry.Key)
            .ToList();

        Assert.True(untranslated.Count == 0, "identical to the English: " + string.Join(", ", untranslated));
    }

    [Theory]
    [InlineData("Strings.resx")]
    [InlineData("Strings.ja.resx")]
    public void NoTwoItemsInOneMenuShareAMnemonic(string catalog)
    {
        // Two items under the same letter means one of them cannot be reached from the
        // keyboard, and which one depends on the order the menu was built in. It is also the
        // kind of thing that survives a translation unnoticed: the Japanese labels carry the
        // letter in parentheses, so a collision is invisible unless something counts them.
        var strings = Catalog(catalog);
        var collisions = new List<string>();

        foreach (var (menu, keys) in MenuGroups())
        {
            var byLetter = new Dictionary<char, List<string>>();
            foreach (var key in keys)
            {
                if (Mnemonic(strings.GetValueOrDefault(key, string.Empty)) is not { } letter)
                    continue;
                byLetter.TryAdd(letter, []);
                byLetter[letter].Add(key);
            }

            collisions.AddRange(byLetter
                .Where(entry => entry.Value.Count > 1)
                .Select(entry => $"{menu}/{entry.Key}: {string.Join(" + ", entry.Value)}"));
        }

        Assert.True(collisions.Count == 0, string.Join("; ", collisions));
    }

    /// <summary>The mnemonic letter an underscore marks, or null if there is none.</summary>
    private static char? Mnemonic(string label)
    {
        var match = Regex.Match(label, "_(.)");
        return match.Success ? char.ToUpperInvariant(match.Groups[1].Value[0]) : null;
    }

    /// <summary>
    /// The window's top-level menus, each with the keys of the items directly under it.
    /// </summary>
    /// <remarks>
    /// Read out of the markup by tracking nesting, rather than listed here: a menu item added
    /// to the window and not to a list here would be exactly the item the collision check
    /// failed to look at.
    /// </remarks>
    private static IReadOnlyList<(string Menu, IReadOnlyList<string> Keys)> MenuGroups()
    {
        var markup = File.ReadAllText(Data("MainWindow.axaml"));
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var order = new List<string>();
        var stack = new Stack<string>();

        // The lookahead matters: `\b` also matches before a dot, so `<MenuItem.ItemTemplate>`
        // — property-element syntax, which any menu built from a collection uses — was read
        // as an opening tag with no matching close, and everything after it was attributed to
        // the wrong menu.
        foreach (Match match in Regex.Matches(markup, @"<MenuItem(?=[\s/>])((?:[^>])*?)(/?)>|</MenuItem>", RegexOptions.Singleline))
        {
            if (match.Value == "</MenuItem>")
            {
                if (stack.Count > 0)
                    stack.Pop();
                continue;
            }

            var key = Regex.Match(match.Groups[1].Value, @"\{loc:Tr ([A-Za-z0-9_]+)\}") is { Success: true } found
                ? found.Groups[1].Value
                : null;

            // Depth one is a top-level menu and has no mnemonic peer worth checking; deeper
            // items belong to whichever menu is at the bottom of the stack.
            if (stack.Count > 0 && key is not null)
            {
                var menu = stack.Last();
                if (groups.TryAdd(menu, []))
                    order.Add(menu);
                groups[menu].Add(key);
            }

            if (match.Groups[2].Value.Length == 0)
                stack.Push(key ?? "?");
        }

        return [.. order.Select(menu => (menu, (IReadOnlyList<string>)groups[menu]))];
    }

    [Fact]
    public void TheMenuStructureIsReadAtAll()
    {
        // The same guard as the markup scan: a parser that finds nothing makes the collision
        // check pass for ever without looking at anything.
        var groups = MenuGroups();
        Assert.True(groups.Count >= 5, $"only {groups.Count} menus found");
        Assert.All(groups, group => Assert.NotEmpty(group.Keys));
    }

    /// <summary>Whether a string contains kana or CJK ideographs.</summary>
    private static bool HasJapanese(string text) =>
        text.Any(c => c is >= '぀' and <= 'ヿ' or >= '一' and <= '鿿');
}
