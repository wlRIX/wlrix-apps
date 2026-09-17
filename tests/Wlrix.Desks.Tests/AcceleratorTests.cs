using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Wlrix.Desks.Tests;

/// <summary>
/// That the shortcuts the Window menu advertises actually run.
/// </summary>
/// <remarks>
/// Desks had the defect twice over. Its accelerators were on <c>MenuItem.HotKey</c>, which does
/// not fire from inside a submenu that has never been opened; and its menu ran from <c>Click</c>
/// handlers, so there was no command for a key binding to invoke even once the first problem was
/// understood. Both were found while fixing wlRIX Files, and this is the third copy of the same
/// guard — one per application with a menu.
///
/// <para>
/// The extra check here is the one Desks needed: an enabled item that advertises a shortcut and
/// runs from a <c>Click</c> handler is unreachable from the keyboard by construction, and looks
/// entirely correct in the markup.
/// </para>
/// </remarks>
public class AcceleratorTests
{
    private static readonly XNamespace Xaml = "https://github.com/avaloniaui";

    private static IEnumerable<XElement> Elements(string name)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Markup");
        foreach (var path in Directory.GetFiles(directory, "*.axaml"))
        {
            foreach (var element in XDocument.Load(path).Descendants(Xaml + name))
                yield return element;
        }
    }

    private static string? Attribute(XElement element, string name) => element.Attribute(name)?.Value;

    private static string? Bound(XElement element)
    {
        var text = Attribute(element, "Command");
        return text is null ? null : Regex.Match(text, @"\{Binding\s+([A-Za-z0-9_]+)").Groups[1].Value;
    }

    private static Dictionary<string, string> Registered() =>
        Elements("KeyBinding")
            .Where(binding => Attribute(binding, "Gesture") is not null && Bound(binding) is not null)
            .ToDictionary(
                binding => Attribute(binding, "Gesture")!,
                binding => Bound(binding)!,
                StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void EveryShortcutTheMenuAdvertisesIsRegisteredOnTheWindowAndRunsTheSameCommand()
    {
        var registered = Registered();

        var broken = Elements("MenuItem")
            .Where(item => Attribute(item, "InputGesture") is not null && Bound(item) is not null)
            .Where(item => !registered.TryGetValue(Attribute(item, "InputGesture")!, out var command)
                           || command != Bound(item))
            .Select(item => $"{Attribute(item, "Header")} shows {Attribute(item, "InputGesture")}")
            .ToList();

        Assert.Empty(broken);
    }

    [Fact]
    public void NoEnabledItemAdvertisesAShortcutItHasNoCommandFor()
    {
        // A Click handler cannot be reached by a key binding. The one exception is a disabled
        // placeholder — "List All" shows Ctrl+L for a feature that does not exist yet — and
        // saying IsEnabled="False" is how that stays distinguishable from an oversight.
        var unreachable = Elements("MenuItem")
            .Where(item => Attribute(item, "InputGesture") is not null)
            .Where(item => Bound(item) is null && Attribute(item, "IsEnabled") != "False")
            .Select(item => $"{Attribute(item, "Header")} shows {Attribute(item, "InputGesture")} "
                            + "but runs from a Click handler")
            .ToList();

        Assert.Empty(unreachable);
    }

    [Fact]
    public void NothingReliesOnHotKeyBecauseItDoesNotFireFromASubmenu()
    {
        Assert.DoesNotContain(Elements("MenuItem"), item => Attribute(item, "HotKey") is not null);
    }

    [Fact]
    public void TheScanFoundSomethingToScan()
    {
        Assert.NotEmpty(Registered());
    }
}
