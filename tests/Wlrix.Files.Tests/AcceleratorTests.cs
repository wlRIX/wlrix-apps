using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Wlrix.Files.Tests;

/// <summary>
/// That the keyboard shortcuts the menus advertise actually do something.
/// </summary>
/// <remarks>
/// Two traps, one on top of the other.
///
/// <para>
/// The first is Avalonia's own, in its doc comment on <c>MenuItem.InputGesture</c>: "Setting
/// this property does not cause the input gesture to be handled by the menu item, it simply
/// displays the gesture text in the menu." So an item with a gesture and nothing else prints a
/// promise and keeps none of it. The file manager shipped that way from M3 to M9 — seventeen
/// shortcuts, every one decoration, Ctrl+C and Ctrl+V among them.
/// </para>
///
/// <para>
/// The second is that <c>MenuItem.HotKey</c>, which exists precisely to be the half that
/// works, does not fire from inside a submenu that has never been opened. Established under
/// the nested compositor with a virtual keyboard: the same command reached through
/// <c>Window.KeyBindings</c> ran, and through <c>HotKey</c> did not. So the window's
/// <c>KeyBindings</c> are where an accelerator has to live, and the gesture is written twice —
/// once to show, once to work. These tests are what keeps the two halves in step.
/// </para>
///
/// <para>
/// The markup is copied into the test output by the csproj, so this reads what the application
/// actually ships.
/// </para>
/// </remarks>
public class AcceleratorTests
{
    private static readonly XNamespace Xaml = "https://github.com/avaloniaui";

    private static IEnumerable<XElement> Elements(string name)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Localization");
        foreach (var path in Directory.GetFiles(directory, "*.axaml"))
        {
            foreach (var element in XDocument.Load(path).Descendants(Xaml + name))
                yield return element;
        }
    }

    private static string? Attribute(XElement element, string name) => element.Attribute(name)?.Value;

    /// <summary>The command a <c>{Binding Foo}</c> attribute names, or null.</summary>
    private static string? Bound(XElement element)
    {
        var text = Attribute(element, "Command");
        return text is null ? null : Regex.Match(text, @"\{Binding\s+([A-Za-z0-9_]+)").Groups[1].Value;
    }

    private static string Describe(XElement item) =>
        Attribute(item, "Header") ?? Attribute(item, "Name") ?? "(unnamed)";

    /// <summary>Gesture to command, as the window will actually act on them.</summary>
    private static Dictionary<string, string> Registered() =>
        Elements("KeyBinding")
            .Where(binding => Attribute(binding, "Gesture") is not null && Bound(binding) is not null)
            .ToDictionary(
                binding => Attribute(binding, "Gesture")!,
                binding => Bound(binding)!,
                StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Return, which the listing handles itself rather than registering on the window.
    /// </summary>
    /// <remarks>
    /// The one gesture that cannot live in <c>KeyBindings</c>. A window-level Return would fire
    /// wherever the key went unhandled — the path bar above all, where Return means "go to this
    /// path" and must not also open whatever happens to be selected behind it. So the listing
    /// owns it, which also means the key only opens something when the listing is what the user
    /// is looking at. Menus still advertise it, because it is real.
    /// </remarks>
    private static readonly string[] HandledByTheListing = ["Return"];

    [Fact]
    public void EveryShortcutAMenuAdvertisesIsRegisteredOnTheWindow()
    {
        var registered = Registered();

        var broken = Elements("MenuItem")
            .Where(item => Attribute(item, "InputGesture") is not null && Bound(item) is not null)
            .Where(item => !HandledByTheListing.Contains(Attribute(item, "InputGesture")!,
                       StringComparer.OrdinalIgnoreCase))
            .Where(item => !registered.ContainsKey(Attribute(item, "InputGesture")!))
            .Select(item => $"{Describe(item)} shows {Attribute(item, "InputGesture")} and nothing registers it")
            .ToList();

        Assert.Empty(broken);
    }

    [Fact]
    public void AnAdvertisedShortcutRunsTheCommandTheMenuItemRuns()
    {
        // A gesture registered to a different command is worse than an unregistered one: the
        // menu teaches a key that then does something else.
        var registered = Registered();

        var wrong = Elements("MenuItem")
            .Where(item => Attribute(item, "InputGesture") is { } gesture && registered.ContainsKey(gesture))
            .Where(item => Bound(item) is { } command
                           && registered[Attribute(item, "InputGesture")!] != command)
            .Select(item => $"{Describe(item)}: menu runs {Bound(item)}, "
                            + $"{Attribute(item, "InputGesture")} runs {registered[Attribute(item, "InputGesture")!]}")
            .ToList();

        Assert.Empty(wrong);
    }

    [Fact]
    public void NoShortcutIsRegisteredTwice()
    {
        // A duplicate gesture is two KeyBindings on one window, and which of them wins is not
        // something to leave to declaration order.
        var duplicates = Elements("KeyBinding")
            .Select(binding => Attribute(binding, "Gesture"))
            .Where(gesture => gesture is not null)
            .GroupBy(gesture => gesture!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void NothingReliesOnHotKeyBecauseItDoesNotFireFromASubmenu()
    {
        // Kept as a test rather than a comment, because HotKey is the obvious thing to reach
        // for — the Archiver and Desks both reach for it — and it looks entirely correct
        // right up until the key is pressed.
        var users = Elements("MenuItem")
            .Where(item => Attribute(item, "HotKey") is not null)
            .Select(Describe)
            .ToList();

        Assert.Empty(users);
    }

    [Fact]
    public void EveryRegisteredShortcutIsAlsoShownSomewhere()
    {
        // A key nothing advertises is a key nobody finds. The menu is the only documentation
        // this application has.
        var advertised = Elements("MenuItem")
            .Select(item => Attribute(item, "InputGesture"))
            .Where(gesture => gesture is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var hidden = Registered().Keys.Where(gesture => !advertised.Contains(gesture)).ToList();

        Assert.Empty(hidden);
    }

    [Fact]
    public void TheScanFoundSomethingToScan()
    {
        // The guard that earned its place on the localization tests' first run, for the same
        // reason: Avalonia's build claims every *.axaml as AvaloniaXaml and removes it from
        // None again, so a copy that looks configured can deliver nothing at all.
        Assert.NotEmpty(Registered());
    }
}
