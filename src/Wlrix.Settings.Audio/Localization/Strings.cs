// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Wlrix.Common.Localization;
using Wlrix.Settings.Audio.Models;

namespace Wlrix.Settings.Audio.Localization;

/// <summary>
/// Strongly-typed access to the app's localized UI strings (backed by <c>Strings.resx</c>).
///
/// The static labels in the window come through <c>{loc:Tr}</c>, which reads the same
/// <see cref="Catalog"/>. What is here is what code has to build: the labels made from a
/// device's own names and numbers, the status-line hints and the status messages.
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.Settings.Audio.Localization.Strings", typeof(Strings).Assembly);

    public static string WindowTitle => Catalog.Get("WindowTitle");
    public static string HelpTitle => Catalog.Get("HelpTitle");
    public static string HelpText => Catalog.Get("HelpText");
    public static string AboutTitle => Catalog.Get("AboutTitle");
    public static string About(string version) => Catalog.Format("About", version);

    public static string Connecting => Catalog.Get("Connecting");
    public static string NoSoundServer => Catalog.Get("NoSoundServer");
    public static string ServerLost => Catalog.Get("ServerLost");
    public static string MeterStreamName => Catalog.Get("MeterStreamName");
    public static string NoDevices => Catalog.Get("NoDevices");

    public static string Off => Catalog.Get("Off");
    public static string ChannelLeft => Catalog.Get("ChannelLeft");
    public static string ChannelRight => Catalog.Get("ChannelRight");

    /// <summary>"48 kHz", "44.1 kHz", "11.025 kHz".</summary>
    public static string Rate(uint hz) =>
        Catalog.Format("RateKHz", (hz / 1000.0).ToString("0.###", CultureInfo.CurrentCulture));

    public static string RateAutomatic => Catalog.Get("RateAutomatic");

    public static string Bus(DeviceBus bus) => Catalog.Get(bus switch
    {
        DeviceBus.Internal => "BusInternal",
        DeviceBus.Usb => "BusUsb",
        DeviceBus.Bluetooth => "BusBluetooth",
        DeviceBus.Hdmi => "BusHdmi",
        DeviceBus.Network => "BusNetwork",
        _ => "BusVirtual",
    });

    public static string MenuSampleRate => Catalog.Get("MenuSampleRate");
    public static string MenuOutputDestination => Catalog.Get("MenuOutputDestination");
    public static string MenuInputSource => Catalog.Get("MenuInputSource");
    public static string MenuGroupSliders => Catalog.Get("MenuGroupSliders");
    public static string MenuMakeDefaultOutput => Catalog.Get("MenuMakeDefaultOutput");
    public static string MenuMakeDefaultInput => Catalog.Get("MenuMakeDefaultInput");
    public static string MenuEnableDevice => Catalog.Get("MenuEnableDevice");
    public static string MenuPreferences => Catalog.Get("MenuPreferences");

    /// <summary>
    /// The hint for a gain slider: "Speaker/Headphone Right channel output gain (output
    /// volume)", as IRIX words it. <paramref name="side"/> is null for a mono device's one
    /// slider.
    /// </summary>
    public static string GainHint(DeviceKind kind, string device, ChannelPosition? side)
    {
        var input = kind == DeviceKind.Input;
        if (side is null)
            return Catalog.Format(input ? "HintInputGainMono" : "HintOutputGainMono", device);
        return Catalog.Format(input ? "HintInputGain" : "HintOutputGain", device, Side(side.Value));
    }

    public static string MeterLevelHint(ChannelPosition side) => Catalog.Format("HintMeterLevel", Side(side));

    private static string Side(ChannelPosition side) =>
        Catalog.Get(side == ChannelPosition.Right ? "Right" : "Left");

    public static string HintMeter => Catalog.Get("HintMeter");
    public static string HintMonitor => Catalog.Get("HintMonitor");
    public static string HintMute => Catalog.Get("HintMute");
    public static string HintLamp => Catalog.Get("HintLamp");
    public static string ColumnHint(string device) => Catalog.Format("HintColumn", device);
    public static string UnavailableHint(string device) => Catalog.Format("HintUnavailable", device);
    public static string SwitchedOffHint(string device) => Catalog.Format("HintSwitchedOff", device);

    /// <summary>What a rate is good for, as the IRIX Sample Rate menu said; 0 is Automatic.</summary>
    public static string RateHint(int rate) => rate switch
    {
        0 => Catalog.Get("HintRateAutomatic"),
        8000 or 11025 or 16000 or 22050 or 32000 or 44100 or 48000 or 96000 or 192000 =>
            Catalog.Get("HintRate" + rate.ToString(CultureInfo.InvariantCulture)),
        _ => Catalog.Get("HintRateGlobal"),
    };

    public static string HintRateUnavailable => Catalog.Get("HintRateUnavailable");
    public static string HintRateNotPipeWire => Catalog.Get("HintRateNotPipeWire");
    public static string HintRateGlobal => Catalog.Get("HintRateGlobal");
    public static string PortHint(string port) => Catalog.Format("HintPort", port);
    public static string PortUnavailableHint(string port) => Catalog.Format("HintPortUnavailable", port);
    public static string HintGroup => Catalog.Get("HintGroup");
    public static string HintMakeDefaultOutput => Catalog.Get("HintMakeDefaultOutput");
    public static string HintMakeDefaultInput => Catalog.Get("HintMakeDefaultInput");
    public static string HintEnableDevice => Catalog.Get("HintEnableDevice");
    public static string HintPreferences => Catalog.Get("HintPreferences");

    public static string NoDeviceSelected => Catalog.Get("NoDeviceSelected");
    public static string NoDevicesOfKind => Catalog.Get("NoDevicesOfKind");
    public static string MenuViewDefaultInput => Catalog.Get("MenuViewDefaultInput");
    public static string MenuViewDefaultOutput => Catalog.Get("MenuViewDefaultOutput");
    public static string HintViewDefaultInput => Catalog.Get("HintViewDefaultInput");
    public static string HintViewDefaultOutput => Catalog.Get("HintViewDefaultOutput");
    public static string ViewDeviceHint(string device) => Catalog.Format("HintViewDevice", device);
}
