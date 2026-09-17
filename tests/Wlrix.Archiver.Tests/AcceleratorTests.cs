using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Wlrix.Archiver.Tests;

/// <summary>
/// That the shortcuts the File and Edit menus advertise actually run.
/// </summary>
/// <remarks>
/// The Archiver had them on <c>MenuItem.HotKey</c> from the day the menu was written, with a
/// comment saying that was the half that works. It is not: <c>HotKey</c> does not fire from
/// inside a submenu that has never been opened, which was established under the nested
/// compositor with a virtual keyboard on 2026-09-13 and then confirmed here — Ctrl+O did
/// nothing until the accelerators moved to <c>Window.KeyBindings</c>.
///
/// <para>
/// The same test guards wlRIX Files, where the defect was found. Two copies rather than one
/// shared project, because a test project referencing another app's markup would be a stranger
/// arrangement than twenty duplicated lines.
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
