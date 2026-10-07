// SPDX-License-Identifier: GPL-3.0-or-later

namespace Wlrix.Settings.Audio.Models;

/// <summary>Which way sound goes through a device.</summary>
public enum DeviceKind
{
    /// <summary>A source: a microphone, a line in.</summary>
    Input,

    /// <summary>A sink: speakers, headphones, a digital out.</summary>
    Output,
}

/// <summary>What the sound server says a device is doing.</summary>
public enum DeviceState
{
    /// <summary>Something is playing to it or recording from it. Lights the header lamp.</summary>
    Running,

    /// <summary>Open, but nothing is flowing.</summary>
    Idle,

    /// <summary>Closed to save power; the first stream wakes it.</summary>
    Suspended,
}

/// <summary>Where a device is connected, as the column's last line says it.</summary>
public enum DeviceBus
{
    Internal,
    Usb,
    Bluetooth,
    Hdmi,
    Network,
    Virtual,
}

/// <summary>One of a device's ports: a jack, a speaker pair, a digital out.</summary>
/// <param name="Name">The server's name for it, which is what selecting it sends.</param>
/// <param name="Description">What it is called on screen.</param>
/// <param name="Available">False when the jack reports nothing plugged in.</param>
public sealed record AudioPort(string Name, string Description, bool Available);

/// <summary>A card profile, for the Preferences window.</summary>
/// <param name="Name">The server's name for it, which is what selecting it sends.</param>
/// <param name="Description">What it is called on screen.</param>
/// <param name="Available">False when the server knows it cannot work right now.</param>
public sealed record CardProfile(string Name, string Description, bool Available);

/// <summary>
/// One sink or source, or a placeholder for one a card could have if its profile allowed it.
/// </summary>
/// <param name="Key">
/// What the panel knows the device by across updates and reconnections: the kind and the
/// server's name for it. Indexes are not stable: a device that comes back gets a new one.
/// </param>
/// <param name="Index">The server's index for it right now, or <c>uint.MaxValue</c> for a placeholder.</param>
/// <param name="Name">The server's name for it.</param>
/// <param name="Description">The human-readable name, which heads the column.</param>
/// <param name="Volumes">
/// One volume per channel, 0 to <see cref="VolumeNorm"/> (and beyond, if something else has
/// amplified it), in the order of <paramref name="Positions"/>.
/// </param>
/// <param name="Positions">The channel names, "front-left" and the like.</param>
/// <param name="EnableProfile">
/// For a placeholder only: the card profile that would bring this device into being.
/// </param>
public sealed record AudioDevice(
    string Key,
    DeviceKind Kind,
    uint Index,
    string Name,
    string Description,
    IReadOnlyList<uint> Volumes,
    IReadOnlyList<ChannelPosition> Positions,
    bool Muted,
    DeviceState State,
    uint SampleRate,
    DeviceBus Bus,
    string? Driver,
    IReadOnlyList<AudioPort> Ports,
    string? ActivePort,
    uint CardIndex,
    bool IsDefault,
    bool IsAvailable,
    bool IsMonitored,
    string? EnableProfile = null)
{
    /// <summary>100%, the volume at which the device neither amplifies nor attenuates.</summary>
    public const uint VolumeNorm = 0x10000;

    /// <summary>Whether this is a stand-in for a device that a card profile has switched off.</summary>
    public bool IsPlaceholder => EnableProfile is not null;

    /// <summary>The active port's description, which is the column's italic subtitle.</summary>
    public string? ActivePortDescription =>
        Ports.FirstOrDefault(port => port.Name == ActivePort)?.Description;

    public static string MakeKey(DeviceKind kind, string name) =>
        (kind == DeviceKind.Input ? "in:" : "out:") + name;
}

/// <summary>The channel positions the panel tells apart. Everything else is <see cref="Other"/>.</summary>
public enum ChannelPosition
{
    Mono,
    Left,
    Right,
    Other,
}

/// <summary>A card, for the Preferences window's profile list.</summary>
public sealed record AudioCard(uint Index, string Name, string Description,
    IReadOnlyList<CardProfile> Profiles, string? ActiveProfile);

/// <summary>Everything the panel shows, as one consistent picture.</summary>
/// <param name="Devices">Every device, inputs and outputs, placeholders included.</param>
/// <param name="Cards">Every card.</param>
/// <param name="IsPipeWire">
/// Whether the server is PipeWire behind pipewire-pulse, which is what makes the Sample Rate
/// menu work.
/// </param>
/// <param name="AllowedRates">PipeWire's <c>clock.allowed-rates</c>; empty on PulseAudio.</param>
/// <param name="ForcedRate">PipeWire's <c>clock.force-rate</c>; 0 when nothing is forced.</param>
public sealed record AudioSnapshot(
    IReadOnlyList<AudioDevice> Devices,
    IReadOnlyList<AudioCard> Cards,
    bool IsPipeWire,
    IReadOnlyList<int> AllowedRates,
    int ForcedRate)
{
    public static AudioSnapshot Empty { get; } = new([], [], false, [], 0);
}
