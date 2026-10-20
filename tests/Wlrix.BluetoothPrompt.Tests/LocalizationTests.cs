using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Wlrix.BluetoothPrompt.Tests;

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

    /// <summary>
    /// The dialog's text all comes through its view model, so the markup asks for no keys of its
    /// own; this only checks that it was copied, so the scan below is not vacuous by accident.
    /// </summary>
    [Fact]
    public void TheMarkupIsCopied() =>
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Localization"), "*.axaml"));

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
            "WindowTitle", "PairHeading", "AuthorizeHeading", "ConfirmDescription",
            "DisplayDescription", "PinDescription", "PasskeyDescription", "AuthorizeDescription",
            "AuthorizeServiceDescription", "Pair", "Allow", "Deny", "Cancel",
            // Every service the daemon's agent::service_name can send.
            "Service_audio", "Service_media_controls", "Service_calls", "Service_input",
            "Service_file_transfer", "Service_network", "Service_contacts_and_messages",
        ];
        var catalog = Catalog(file);
        Assert.All(keys, key => Assert.Contains(key, catalog));
    }
}
