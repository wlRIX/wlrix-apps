// SPDX-License-Identifier: GPL-3.0-or-later

using Wlrix.Settings.Audio.Models;
using Wlrix.Settings.Audio.Services;
using Xunit;
using RawCard = Wlrix.Settings.Audio.Services.PulseAudioFeed.RawCard;
using RawProfile = Wlrix.Settings.Audio.Services.PulseAudioFeed.RawProfile;

namespace Wlrix.Settings.Audio.Tests;

/// <summary>The parts of the libpulse feed that do not need a server.</summary>
public class PulseHelpersTests
{
    [Fact]
    public void ReadsPipeWireSettings()
    {
        const string dump = """
            Found "settings" metadata 31
            update: id:0 key:'log.level' value:'2' type:''
            update: id:0 key:'clock.rate' value:'48000' type:''
            update: id:0 key:'clock.allowed-rates' value:'[ 44100 48000 96000 ]' type:''
            update: id:0 key:'clock.quantum' value:'1024' type:''
            update: id:0 key:'clock.force-rate' value:'44100' type:''
            """;
        var (allowed, forced) = PipeWireRates.Parse(dump);
        Assert.Equal([44100, 48000, 96000], allowed);
        Assert.Equal(44100, forced);
    }

    [Fact]
    public void MissingAllowedRatesMeansTheDefault()
    {
        var (allowed, forced) = PipeWireRates.Parse("Found \"settings\" metadata 31\n");
        Assert.Equal([48000], allowed);
        Assert.Equal(0, forced);
    }

    [Theory]
    [InlineData("usb", "alsa_output.usb-Foo.analog-stereo", null, DeviceBus.Usb)]
    [InlineData("pci", "alsa_output.pci-0000_00_1f.3.analog-stereo", null, DeviceBus.Internal)]
    [InlineData("pci", "alsa_output.pci-0000_e3_00.1.hdmi-stereo", null, DeviceBus.Hdmi)]
    [InlineData("bluetooth", "bluez_output.00_11_22.1", null, DeviceBus.Bluetooth)]
    [InlineData(null, "bluez_output.00_11_22.1", null, DeviceBus.Bluetooth)]
    [InlineData(null, "tunnel.host.sink", "module-tunnel-sink.c", DeviceBus.Network)]
    [InlineData(null, "effect_output.eq", null, DeviceBus.Virtual)]
    public void TellsWhereADeviceIs(string? bus, string name, string? driver, DeviceBus expected) =>
        Assert.Equal(expected, PulseAudioFeed.ParseBus(bus, name, driver));

    [Fact]
    public void AnHdmiCardIsKnownByItsDescription() =>
        Assert.Equal(DeviceBus.Hdmi, PulseAudioFeed.ParseBus("pci", "alsa_card.pci-0000_c3_00.1", null,
            "Navi 48 HDMI/DP Audio Controller"));

    [Fact]
    public void FindsAModuleArgument()
    {
        const string argument = "source=alsa_input.usb-Mic latency_msec=40 sink_input_properties=media.name=wlrix-audio-monitor";
        Assert.Equal("alsa_input.usb-Mic", PulseAudioFeed.ArgumentValue(argument, "source"));
        Assert.Equal("40", PulseAudioFeed.ArgumentValue(argument, "latency_msec"));
        Assert.Null(PulseAudioFeed.ArgumentValue(argument, "sink"));
    }

    private static RawCard Card(string? active, params RawProfile[] profiles) =>
        new(1, "alsa_card.pci", "Built-in Audio", DeviceBus.Internal, profiles, active);

    private static readonly RawProfile Off = new("off", "Off", 0, 0, 0, true);
    private static readonly RawProfile Duplex = new("duplex", "Analog Duplex", 1, 1, 6500, true);
    private static readonly RawProfile OutputOnly = new("output", "Analog Output", 1, 0, 6600, true);
    private static readonly RawProfile InputOnly = new("input", "Analog Input", 0, 1, 100, true);
    private static readonly RawProfile Hdmi = new("hdmi", "HDMI Output", 1, 0, 5900, false);

    [Fact]
    public void ACardThatIsOffWakesWithItsBestProfile()
    {
        var card = Card("off", Off, Duplex, OutputOnly, InputOnly);
        Assert.Equal("output", PulseAudioFeed.PlaceholderProfile(card, DeviceKind.Output));
        Assert.Equal("duplex", PulseAudioFeed.PlaceholderProfile(card, DeviceKind.Input));
    }

    [Fact]
    public void EnablingAnOutputKeepsTheInput()
    {
        // Input-only now: the output-only profile ranks higher, but would take the microphone.
        var card = Card("input", Off, Duplex, OutputOnly, InputOnly);
        Assert.Equal("duplex", PulseAudioFeed.PlaceholderProfile(card, DeviceKind.Output));
    }

    [Fact]
    public void UnavailableProfilesAreNotOffered()
    {
        var card = Card("off", Off, Hdmi);
        Assert.Null(PulseAudioFeed.PlaceholderProfile(card, DeviceKind.Output));
        Assert.Null(PulseAudioFeed.PlaceholderProfile(card, DeviceKind.Input));
    }
}
