// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Threading;
using Wlrix.Settings.Audio.Models;

namespace Wlrix.Settings.Audio.Services;

/// <summary>
/// Made-up devices, for <c>--demo</c> and the tests: the three columns of the IRIX screenshot.
/// Analog In is a microphone, Analog Out is the headphones and speakers, and Digital Out is
/// switched off by the card's profile. Changes are kept and handed back as a new snapshot, as a server would, and a
/// metered input gets a wandering signal.
/// </summary>
public sealed class SampleAudioFeed : IAudioFeed
{
    private readonly List<AudioDevice> _devices;
    private readonly List<AudioCard> _cards;
    private readonly HashSet<string> _metered = [];
    private readonly bool _isPipeWire;
    private readonly bool _animate;
    private int _forcedRate;
    private DispatcherTimer? _timer;
    private double _phase;

    /// <param name="isPipeWire">Whether to pretend to be PipeWire, which enables Sample Rate.</param>
    /// <param name="animate">
    /// Whether metered inputs get a made-up signal. The tests turn it off: it needs the UI
    /// thread's dispatcher, and they hand out peaks themselves.
    /// </param>
    public SampleAudioFeed(bool isPipeWire = true, bool animate = true)
    {
        _isPipeWire = isPipeWire;
        _animate = animate;
        _devices =
        [
            Device(DeviceKind.Input, "analog-input", "Analog In", [0.55, 0.55],
                [new AudioPort("analog-input-mic", "Microphone", true),
                 new AudioPort("analog-input-linein", "Line In", false)],
                "analog-input-mic", isDefault: true),
            Device(DeviceKind.Output, "analog-output", "Analog Out", [0.5, 0.5],
                [new AudioPort("analog-output-speaker", "Headphone/Speakers", true),
                 new AudioPort("analog-output-lineout", "Line Out", true)],
                "analog-output-speaker", isDefault: true) with { State = DeviceState.Running },
            Device(DeviceKind.Output, "digital-output", "Digital Out", [1, 1],
                [new AudioPort("iec958-stereo-output", "Digital Out", false)],
                "iec958-stereo-output", isDefault: false)
                with { IsAvailable = false, Index = uint.MaxValue, EnableProfile = "output:iec958-stereo" },
        ];
        _cards =
        [
            new AudioCard(0, "internal", "Internal Audio",
                [new CardProfile("output:analog-stereo+input:analog-stereo", "Analog Stereo Duplex", true),
                 new CardProfile("output:analog-stereo", "Analog Stereo Output", true),
                 new CardProfile("off", "Off", true)],
                "output:analog-stereo+input:analog-stereo"),
        ];
    }

    public event Action<AudioSnapshot>? SnapshotReceived;
    public event Action<string?>? AvailabilityChanged;
    public event Action<string, float, float>? PeaksReceived;

    /// <summary>The devices as they now stand, for the tests.</summary>
    internal IReadOnlyList<AudioDevice> Devices => _devices;

    /// <summary>The inputs being metered, for the tests.</summary>
    internal IReadOnlyCollection<string> Metered => _metered;

    /// <summary>Change a device as the server might on its own, for the tests.</summary>
    internal void Change(string key, Func<AudioDevice, AudioDevice> change) => Update(key, change);

    public void Start() => Publish();

    /// <summary>Pretend the server went away (a reason) or came back (null). For the tests.</summary>
    internal void RaiseAvailability(string? reason) => AvailabilityChanged?.Invoke(reason);

    /// <summary>Hand out a peak as the meter stream would. For the tests.</summary>
    internal void RaisePeaks(string key, float left, float right) =>
        PeaksReceived?.Invoke(key, left, right);

    public void SetVolume(string key, IReadOnlyList<uint> volumes) =>
        Update(key, device => device with { Volumes = [.. volumes] });

    public void SetMute(string key, bool muted) => Update(key, device => device with { Muted = muted });

    public void SetDefault(string key)
    {
        var kind = _devices.First(device => device.Key == key).Kind;
        for (var i = 0; i < _devices.Count; i++)
        {
            if (_devices[i].Kind == kind)
                _devices[i] = _devices[i] with { IsDefault = _devices[i].Key == key };
        }
        Publish();
    }

    public void SetPort(string key, string port) => Update(key, device => device with
    {
        ActivePort = port,
        IsAvailable = device.Ports.FirstOrDefault(p => p.Name == port)?.Available ?? device.IsAvailable,
    });

    public void EnableDevice(string key) => Update(key, device => device with
    {
        IsAvailable = true,
        Index = 0,
        EnableProfile = null,
        Ports = [.. device.Ports.Select(port => port with { Available = true })],
    });

    public void SetCardProfile(uint card, string profile)
    {
        var index = _cards.FindIndex(c => c.Index == card);
        if (index < 0)
            return;
        _cards[index] = _cards[index] with { ActiveProfile = profile };
        Publish();
    }

    public void SetForcedRate(int rate)
    {
        _forcedRate = rate;
        for (var i = 0; i < _devices.Count; i++)
            _devices[i] = _devices[i] with { SampleRate = rate == 0 ? 48000u : (uint)rate };
        Publish();
    }

    public void SetMetering(string key, bool on)
    {
        if (on)
            _metered.Add(key);
        else
            _metered.Remove(key);

        if (_metered.Count > 0 && _timer is null && _animate)
        {
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, OnTick);
            _timer.Start();
        }
        else if (_metered.Count == 0)
        {
            _timer?.Stop();
            _timer = null;
        }
    }

    public void SetMonitor(string key, bool on) => Update(key, device => device with { IsMonitored = on });

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Something like a voice: a slow swell with syllables on top, now and then hitting the
        // top of the scale.
        _phase += 0.04;
        var swell = 0.45 + 0.35 * Math.Sin(_phase * 0.7);
        var syllable = Math.Abs(Math.Sin(_phase * 5.3)) * Math.Abs(Math.Sin(_phase * 1.9));
        var level = (float)Math.Clamp(swell * syllable * 1.3, 0, 1.05);
        foreach (var key in _metered)
            PeaksReceived?.Invoke(key, level, level * 0.9f);
    }

    private void Update(string key, Func<AudioDevice, AudioDevice> change)
    {
        var index = _devices.FindIndex(device => device.Key == key);
        if (index < 0)
            return;
        _devices[index] = change(_devices[index]);
        Publish();
    }

    private void Publish() => SnapshotReceived?.Invoke(new AudioSnapshot(
        [.. _devices], [.. _cards], _isPipeWire,
        _isPipeWire ? [44100, 48000] : [], _isPipeWire ? _forcedRate : 0));

    private static AudioDevice Device(DeviceKind kind, string name, string description,
        double[] volumes, IReadOnlyList<AudioPort> ports, string activePort, bool isDefault) =>
        new(
            Key: AudioDevice.MakeKey(kind, name),
            Kind: kind,
            Index: 0,
            Name: name,
            Description: description,
            Volumes: volumes.Select(v => (uint)(v * AudioDevice.VolumeNorm)).ToList(),
            Positions: [ChannelPosition.Left, ChannelPosition.Right],
            Muted: false,
            State: DeviceState.Idle,
            SampleRate: 48000,
            Bus: DeviceBus.Internal,
            Driver: "sample",
            Ports: ports,
            ActivePort: activePort,
            CardIndex: 0,
            IsDefault: isDefault,
            IsAvailable: true,
            IsMonitored: false);
}
