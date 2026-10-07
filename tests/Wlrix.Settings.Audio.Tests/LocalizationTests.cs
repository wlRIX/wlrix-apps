// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Wlrix.Settings.Audio.Tests;

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
            "WindowTitle", "HelpTitle", "HelpText", "AboutTitle", "About", "Connecting",
            "NoSoundServer", "ServerLost", "MeterStreamName", "NoDevices", "Off", "ChannelLeft",
            "ChannelRight", "Clip", "RateKHz", "RateAutomatic", "BusInternal", "BusUsb",
            "BusBluetooth", "BusHdmi", "BusNetwork", "BusVirtual", "MenuSampleRate",
            "MenuOutputDestination", "MenuInputSource", "MenuGroupSliders", "MenuMakeDefaultOutput",
            "MenuMakeDefaultInput", "MenuEnableDevice", "MenuPreferences", "Left", "Right",
            "HintOutputGain", "HintOutputGainMono", "HintInputGain", "HintInputGainMono",
            "HintMeterLevel", "HintMeter", "HintMonitor", "HintMute", "HintLamp", "HintColumn",
            "HintUnavailable", "HintSwitchedOff", "HintRateAutomatic", "HintRateUnavailable",
            "HintRateNotPipeWire", "HintRateGlobal", "HintPort", "HintPortUnavailable", "HintGroup",
            "HintMakeDefaultOutput", "HintMakeDefaultInput", "HintEnableDevice", "HintPreferences",
            "NoDeviceSelected", "NoDevicesOfKind", "MenuViewDefaultInput", "MenuViewDefaultOutput",
            "HintViewDefaultInput", "HintViewDefaultOutput", "HintViewDevice",
            .. new[] { 8000, 11025, 16000, 22050, 32000, 44100, 48000, 96000, 192000 }
                .Select(rate => "HintRate" + rate),
        ];
        var catalog = Catalog();
        Assert.All(keys, key => Assert.Contains(key, catalog));
    }
}
