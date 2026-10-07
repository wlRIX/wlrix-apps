// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using Wlrix.Settings.Audio.Localization;
using Wlrix.Settings.Audio.Models;
using Wlrix.Settings.Audio.Pulse;

namespace Wlrix.Settings.Audio.Services;

/// <summary>
/// The sound server, through libpulse: PulseAudio itself, or PipeWire through pipewire-pulse,
/// which is the same path pavucontrol takes.
///
/// <para><b>Threads.</b> libpulse runs on its own thread (the threaded mainloop), and every
/// callback below arrives there with the mainloop lock held. Calls from the UI take the lock
/// first (<see cref="Run"/>); calls from inside a callback must not, and make their requests
/// directly. What the UI reads, the last snapshot, is swapped whole under <see cref="_gate"/>.</para>
///
/// <para><b>Refreshing.</b> Any change the server reports re-reads everything: server, sinks,
/// sources, cards and modules, five requests whose answers are put together into one snapshot
/// once the last has arrived. A change that arrives while a read is under way marks it to be
/// read again afterwards, so a slider dragged against the server's change events costs at most
/// one read behind, never one per event. There are a handful of devices; reading them all is
/// cheaper than keeping a partial picture correct.</para>
///
/// <para><b>Reconnecting.</b> <c>PA_CONTEXT_NOFAIL</c> waits for a server that is not there yet,
/// but a connection that drops later fails for good. The panel then makes a new context after a
/// second, off the mainloop thread since a context cannot be freed from its own callback.</para>
/// </summary>
internal sealed unsafe partial class PulseAudioFeed : IAudioFeed
{
    /// <summary>Marks the loopback modules Monitor loads, so they are recognized when listed.</summary>
    internal const string MonitorTag = "wlrix-audio-monitor";

    private readonly object _gate = new();
    private nint _mainloop;
    private nint _context;
    private GCHandle _self;
    private volatile bool _disposed;
    private bool _ready;

    // Mainloop thread only: the read in progress.
    private int _outstanding;
    private bool _readAgain;
    private ServerSummary _server = new(null, null, false);
    private readonly List<AudioDevice> _sinks = [];
    private readonly List<AudioDevice> _sources = [];
    private readonly List<RawCard> _cards = [];
    private readonly Dictionary<string, uint> _monitorModules = [];

    // Under _gate.
    private AudioSnapshot _snapshot = AudioSnapshot.Empty;
    private IReadOnlyList<AudioDevice> _devices = [];
    private IReadOnlyList<AudioCard> _audioCards = [];
    private IReadOnlyList<int> _allowedRates = [];
    private int _forcedRate;
    private bool _isPipeWire;
    private readonly Dictionary<string, uint[]> _pendingVolumes = [];
    private readonly HashSet<string> _volumesInFlight = [];
    private readonly HashSet<string> _metered = [];
    private readonly HashSet<uint> _loadedModules = [];

    // Mainloop thread only.
    private readonly Dictionary<string, MeterStream> _meters = [];

    public event Action<AudioSnapshot>? SnapshotReceived;
    public event Action<string?>? AvailabilityChanged;
    public event Action<string, float, float>? PeaksReceived;

    public void Start()
    {
        _self = GCHandle.Alloc(this);
        _mainloop = Native.MainloopNew();
        if (_mainloop == 0 || Native.MainloopStart(_mainloop) < 0)
        {
            AvailabilityChanged?.Invoke(Strings.NoSoundServer);
            return;
        }

        AvailabilityChanged?.Invoke(Strings.Connecting);
        Native.MainloopLock(_mainloop);
        try
        {
            Connect();
        }
        finally
        {
            Native.MainloopUnlock(_mainloop);
        }
    }

    // ---------------------------------------------------------------- connection

    /// <summary>Make a context and start connecting it. Mainloop lock held.</summary>
    private void Connect()
    {
        var props = Native.ProplistNew();
        Native.ProplistSets(props, "application.name", Strings.WindowTitle);
        Native.ProplistSets(props, "application.id", "com.wlrix.settings.audio");
        Native.ProplistSets(props, "application.icon_name", "multimedia-volume-control");
        _context = Native.ContextNew(Native.MainloopGetApi(_mainloop), Strings.WindowTitle, props);
        Native.ProplistFree(props);
        if (_context == 0)
        {
            AvailabilityChanged?.Invoke(Strings.NoSoundServer);
            return;
        }

        var self = GCHandle.ToIntPtr(_self);
        Native.ContextSetStateCallback(_context, &OnContextState, self);
        Native.ContextSetSubscribeCallback(_context, &OnSubscribe, self);
        if (Native.ContextConnect(_context, 0, PaConst.ContextNoFail, 0) < 0)
            AvailabilityChanged?.Invoke(Strings.NoSoundServer);
    }

    [UnmanagedCallersOnly]
    private static void OnContextState(nint context, nint userdata)
    {
        if (From(userdata) is not { } feed || context != feed._context)
            return;

        switch (Native.ContextGetState(context))
        {
            case ContextState.Ready:
                feed._ready = true;
                Native.OperationUnref(Native.ContextSubscribe(context,
                    PaConst.SubscriptionMaskSink | PaConst.SubscriptionMaskSource
                    | PaConst.SubscriptionMaskCard | PaConst.SubscriptionMaskServer
                    | PaConst.SubscriptionMaskModule, null, 0));
                feed.AvailabilityChanged?.Invoke(null);
                feed.Read();
                feed.ReadRates();
                break;

            case ContextState.Failed:
            case ContextState.Terminated:
                feed._ready = false;
                // A read cut off halfway never finishes; the next context starts a new one.
                feed._outstanding = 0;
                feed._readAgain = false;
                if (feed._disposed)
                    return;
                feed.AvailabilityChanged?.Invoke(Strings.ServerLost);
                // The meters went with the context; Reconcile starts them again once there is
                // one. The streams' own state callbacks free them.
                feed._meters.Clear();
                lock (feed._gate)
                {
                    feed._volumesInFlight.Clear();
                    feed._loadedModules.Clear();
                }
                _ = feed.ReconnectAsync();
                break;
        }
    }

    /// <summary>Drop the failed context and start a new one. Off the mainloop thread.</summary>
    private void Reconnect()
    {
        if (_disposed)
            return;
        Native.MainloopLock(_mainloop);
        try
        {
            var old = _context;
            _context = 0;
            Native.ContextSetStateCallback(old, null, 0);
            Native.ContextSetSubscribeCallback(old, null, 0);
            Native.ContextUnref(old);
            Connect();
        }
        finally
        {
            Native.MainloopUnlock(_mainloop);
        }
    }

    // ---------------------------------------------------------------- reading

    [UnmanagedCallersOnly]
    private static void OnSubscribe(nint context, uint type, uint index, nint userdata)
    {
        if (From(userdata) is not { } feed || context != feed._context)
            return;
        feed.Read();
        // A server change can be the graph rate moving; a sink change can be too.
        if ((type & 0x0F) == 7)
            feed.ReadRates();
    }

    /// <summary>Read everything again, or again after the read under way. Mainloop thread.</summary>
    private void Read()
    {
        if (!_ready)
            return;
        if (_outstanding > 0)
        {
            _readAgain = true;
            return;
        }

        _outstanding = 5;
        _sinks.Clear();
        _sources.Clear();
        _cards.Clear();
        _monitorModules.Clear();
        var self = GCHandle.ToIntPtr(_self);
        Native.OperationUnref(Native.GetServerInfo(_context, &OnServerInfo, self));
        Native.OperationUnref(Native.GetSinkInfoList(_context, &OnSinkInfo, self));
        Native.OperationUnref(Native.GetSourceInfoList(_context, &OnSourceInfo, self));
        Native.OperationUnref(Native.GetCardInfoList(_context, &OnCardInfo, self));
        Native.OperationUnref(Native.GetModuleInfoList(_context, &OnModuleInfo, self));
    }

    private void ReadDone()
    {
        if (--_outstanding > 0)
            return;
        Publish();
        if (_readAgain)
        {
            _readAgain = false;
            Read();
        }
    }

    [UnmanagedCallersOnly]
    private static void OnServerInfo(nint context, ServerInfo* info, nint userdata)
    {
        if (From(userdata) is not { } feed)
            return;
        if (info is not null)
        {
            feed._server = new ServerSummary(
                Native.Str(info->DefaultSinkName),
                Native.Str(info->DefaultSourceName),
                Native.Str(info->ServerName)?.Contains("PipeWire", StringComparison.Ordinal) == true);
        }
        feed.ReadDone();
    }

    [UnmanagedCallersOnly]
    private static void OnSinkInfo(nint context, DeviceInfo* info, int eol, nint userdata)
    {
        if (From(userdata) is not { } feed)
            return;
        if (eol != 0 || info is null)
        {
            feed.ReadDone();
            return;
        }
        feed._sinks.Add(ToDevice(DeviceKind.Output, info));
    }

    [UnmanagedCallersOnly]
    private static void OnSourceInfo(nint context, DeviceInfo* info, int eol, nint userdata)
    {
        if (From(userdata) is not { } feed)
            return;
        if (eol != 0 || info is null)
        {
            feed.ReadDone();
            return;
        }
        // A sink's monitor is a source too, but it is not a device anybody plugged in: it is
        // what the output is playing, and it belongs with the output if anywhere.
        if (info->Monitor == PaConst.InvalidIndex)
            feed._sources.Add(ToDevice(DeviceKind.Input, info));
    }

    [UnmanagedCallersOnly]
    private static void OnCardInfo(nint context, CardInfo* info, int eol, nint userdata)
    {
        if (From(userdata) is not { } feed)
            return;
        if (eol != 0 || info is null)
        {
            feed.ReadDone();
            return;
        }

        var profiles = new List<RawProfile>();
        for (var i = 0; i < info->NProfiles; i++)
        {
            var profile = info->Profiles2[i];
            profiles.Add(new RawProfile(
                Native.Str(profile->Name) ?? "",
                Native.Str(profile->Description) ?? "",
                profile->NSinks, profile->NSources, profile->Priority, profile->Available != 0));
        }
        var name = Native.Str(info->Name) ?? "";
        feed._cards.Add(new RawCard(
            info->Index, name,
            Native.Prop(info->Proplist, "device.description") ?? name,
            ParseBus(Native.Prop(info->Proplist, "device.bus"), name, null,
                Native.Prop(info->Proplist, "device.description")),
            profiles,
            info->ActiveProfile2 is null ? null : Native.Str(info->ActiveProfile2->Name)));
    }

    [UnmanagedCallersOnly]
    private static void OnModuleInfo(nint context, ModuleInfo* info, int eol, nint userdata)
    {
        if (From(userdata) is not { } feed)
            return;
        if (eol != 0 || info is null)
        {
            feed.ReadDone();
            return;
        }

        if (Native.Str(info->Name) == "module-loopback"
            && Native.Str(info->Argument) is { } argument
            && argument.Contains(MonitorTag, StringComparison.Ordinal)
            && ArgumentValue(argument, "source") is { } source)
        {
            feed._monitorModules[source] = info->Index;
        }
    }

    private static AudioDevice ToDevice(DeviceKind kind, DeviceInfo* info)
    {
        var name = Native.Str(info->Name) ?? "";
        var channels = Math.Min((int)info->Volume.Channels, PaConst.ChannelsMax);
        var volumes = new uint[channels];
        var positions = new ChannelPosition[channels];
        for (var i = 0; i < channels; i++)
        {
            volumes[i] = info->Volume.Values[i];
            positions[i] = info->ChannelMap.Map[i] switch
            {
                PaConst.ChannelMono => ChannelPosition.Mono,
                PaConst.ChannelFrontLeft => ChannelPosition.Left,
                PaConst.ChannelFrontRight => ChannelPosition.Right,
                _ => ChannelPosition.Other,
            };
        }

        var ports = new List<AudioPort>();
        for (var i = 0; i < info->NPorts; i++)
        {
            var port = info->Ports[i];
            ports.Add(new AudioPort(
                Native.Str(port->Name) ?? "",
                Native.Str(port->Description) ?? "",
                port->Available != PaConst.PortAvailableNo));
        }
        var active = info->ActivePort is null ? null : Native.Str(info->ActivePort->Name);
        var available = info->ActivePort is null || info->ActivePort->Available != PaConst.PortAvailableNo;

        return new AudioDevice(
            Key: AudioDevice.MakeKey(kind, name),
            Kind: kind,
            Index: info->Index,
            Name: name,
            Description: Native.Str(info->Description) ?? name,
            Volumes: volumes,
            Positions: positions,
            Muted: info->Mute != 0,
            State: info->State switch
            {
                PaConst.StateRunning => DeviceState.Running,
                PaConst.StateIdle => DeviceState.Idle,
                _ => DeviceState.Suspended,
            },
            SampleRate: info->SampleSpec.Rate,
            Bus: ParseBus(Native.Prop(info->Proplist, "device.bus"), name, Native.Str(info->Driver),
                Native.Str(info->Description)),
            Driver: Native.Str(info->Driver),
            Ports: ports,
            ActivePort: active,
            CardIndex: info->Card,
            IsDefault: false,
            IsAvailable: available,
            IsMonitored: false);
    }

    /// <summary>
    /// Where a device is, from <c>device.bus</c>, with HDMI and DisplayPort picked out by name
    /// or description, since they are on the GPU's PCI bus like an internal codec. A card's
    /// name is only its PCI address, so for a card the description is what says HDMI.
    /// </summary>
    internal static DeviceBus ParseBus(string? bus, string name, string? driver, string? description = null)
    {
        static bool IsHdmi(string? text) =>
            text is not null
            && (text.Contains("hdmi", StringComparison.OrdinalIgnoreCase)
                || text.Contains("displayport", StringComparison.OrdinalIgnoreCase));
        if (IsHdmi(name) || IsHdmi(description))
            return DeviceBus.Hdmi;
        if (driver?.Contains("tunnel", StringComparison.OrdinalIgnoreCase) == true)
            return DeviceBus.Network;
        return bus switch
        {
            "usb" => DeviceBus.Usb,
            "bluetooth" => DeviceBus.Bluetooth,
            "pci" or "isa" or "platform" or "firewire" => DeviceBus.Internal,
            _ when name.StartsWith("bluez", StringComparison.Ordinal) => DeviceBus.Bluetooth,
            _ when name.StartsWith("alsa", StringComparison.Ordinal) => DeviceBus.Internal,
            _ => DeviceBus.Virtual,
        };
    }

    /// <summary><c>source=foo</c> out of a module argument string.</summary>
    internal static string? ArgumentValue(string argument, string key)
    {
        foreach (var part in argument.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length > key.Length && part.StartsWith(key, StringComparison.Ordinal)
                && part[key.Length] == '=')
            {
                return part[(key.Length + 1)..].Trim('"');
            }
        }
        return null;
    }

    /// <summary>Put the finished read together and hand it out. Mainloop thread.</summary>
    private void Publish()
    {
        var devices = new List<AudioDevice>();
        foreach (var source in _sources)
        {
            devices.Add(source with
            {
                IsDefault = source.Name == _server.DefaultSource,
                IsMonitored = _monitorModules.ContainsKey(source.Name),
            });
        }
        devices.AddRange(Placeholders(DeviceKind.Input, _sources));
        foreach (var sink in _sinks)
            devices.Add(sink with { IsDefault = sink.Name == _server.DefaultSink });
        devices.AddRange(Placeholders(DeviceKind.Output, _sinks));

        var cards = _cards.Select(card => new AudioCard(card.Index, card.Name, card.Description,
                card.Profiles.Select(p => new CardProfile(p.Name, p.Description, p.Available)).ToList(),
                card.ActiveProfile))
            .ToList();

        lock (_gate)
        {
            _devices = devices;
            _audioCards = cards;
            _isPipeWire = _server.IsPipeWire;
        }
        RaiseSnapshot();
        ReconcileMeters();
    }

    /// <summary>
    /// A greyed column for each card that could have a device of <paramref name="kind"/> but
    /// whose profile has switched it off, so it can be switched on again from the panel.
    /// </summary>
    private IEnumerable<AudioDevice> Placeholders(DeviceKind kind, List<AudioDevice> existing)
    {
        foreach (var card in _cards)
        {
            if (existing.Any(device => device.CardIndex == card.Index))
                continue;
            var profile = PlaceholderProfile(card, kind);
            if (profile is null)
                continue;

            var name = card.Name + (kind == DeviceKind.Input ? ".input" : ".output");
            yield return new AudioDevice(
                Key: AudioDevice.MakeKey(kind, name),
                Kind: kind,
                Index: PaConst.InvalidIndex,
                Name: name,
                Description: card.Description,
                Volumes: [0, 0],
                Positions: [ChannelPosition.Left, ChannelPosition.Right],
                Muted: false,
                State: DeviceState.Suspended,
                SampleRate: 0,
                Bus: card.Bus,
                Driver: null,
                Ports: [],
                ActivePort: null,
                CardIndex: card.Index,
                IsDefault: false,
                IsAvailable: false,
                IsMonitored: false,
                EnableProfile: profile);
        }
    }

    /// <summary>
    /// The profile to switch to for a device of <paramref name="kind"/>: the best one that
    /// has it and keeps whatever the active profile already provides the other way, so
    /// enabling a card's output does not take its microphone away; failing that, the best one
    /// that has it at all.
    /// </summary>
    internal static string? PlaceholderProfile(RawCard card, DeviceKind kind)
    {
        var active = card.Profiles.FirstOrDefault(p => p.Name == card.ActiveProfile);
        uint Has(RawProfile p) => kind == DeviceKind.Input ? p.Sources : p.Sinks;
        uint Other(RawProfile p) => kind == DeviceKind.Input ? p.Sinks : p.Sources;

        var candidates = card.Profiles.Where(p => p.Available && Has(p) > 0)
            .OrderByDescending(p => p.Priority).ToList();
        var keeping = active is null ? null
            : candidates.FirstOrDefault(p => Other(p) >= Other(active));
        return (keeping ?? candidates.FirstOrDefault())?.Name;
    }

    private void RaiseSnapshot()
    {
        AudioSnapshot snapshot;
        lock (_gate)
        {
            snapshot = new AudioSnapshot(_devices, _audioCards, _isPipeWire,
                _isPipeWire ? _allowedRates : [], _isPipeWire ? _forcedRate : 0);
            _snapshot = snapshot;
        }
        SnapshotReceived?.Invoke(snapshot);
    }

    private void ReadRates() => _ = ReadRatesAsync();

    // ---------------------------------------------------------------- requests

    /// <summary>
    /// Run <paramref name="request"/> under the mainloop lock, unref'ing the operation it
    /// returns. Does nothing while there is no server. Never call this from a callback.
    /// </summary>
    private void Run(Func<nint, nint> request)
    {
        if (_disposed || _mainloop == 0)
            return;
        Native.MainloopLock(_mainloop);
        try
        {
            if (!_ready)
                return;
            var operation = request(_context);
            if (operation != 0)
                Native.OperationUnref(operation);
        }
        finally
        {
            Native.MainloopUnlock(_mainloop);
        }
    }

    private AudioDevice? Find(string key)
    {
        lock (_gate)
            return _snapshot.Devices.FirstOrDefault(device => device.Key == key);
    }

    public void SetVolume(string key, IReadOnlyList<uint> volumes)
    {
        lock (_gate)
        {
            _pendingVolumes[key] = [.. volumes];
            // One write per device on the wire at a time; the newest value waits behind it and
            // everything in between is dropped.
            if (!_volumesInFlight.Add(key))
                return;
        }
        Run(context => SendVolume(context, key));
    }

    /// <summary>Send the newest waiting volume for <paramref name="key"/>. Mainloop lock held.</summary>
    private nint SendVolume(nint context, string key)
    {
        uint[]? volumes;
        AudioDevice? device;
        lock (_gate)
        {
            _pendingVolumes.Remove(key, out volumes);
            device = _snapshot.Devices.FirstOrDefault(d => d.Key == key);
            if (volumes is null || device is null || device.IsPlaceholder)
            {
                _volumesInFlight.Remove(key);
                return 0;
            }
        }

        var cv = new CVolume { Channels = (byte)Math.Min(volumes.Length, PaConst.ChannelsMax) };
        for (var i = 0; i < cv.Channels; i++)
            cv.Values[i] = volumes[i];
        var handle = GCHandle.ToIntPtr(GCHandle.Alloc(new VolumeWrite(this, key)));
        return device.Kind == DeviceKind.Output
            ? Native.SetSinkVolume(context, device.Index, &cv, &OnVolumeSet, handle)
            : Native.SetSourceVolume(context, device.Index, &cv, &OnVolumeSet, handle);
    }

    [UnmanagedCallersOnly]
    private static void OnVolumeSet(nint context, int success, nint userdata)
    {
        var handle = GCHandle.FromIntPtr(userdata);
        var write = (VolumeWrite)handle.Target!;
        handle.Free();
        var feed = write.Feed;
        bool more;
        lock (feed._gate)
        {
            more = feed._pendingVolumes.ContainsKey(write.Key);
            if (!more)
                feed._volumesInFlight.Remove(write.Key);
        }
        if (more && feed._ready)
        {
            var operation = feed.SendVolume(context, write.Key);
            if (operation != 0)
                Native.OperationUnref(operation);
        }
    }

    public void SetMute(string key, bool muted)
    {
        if (Find(key) is not { IsPlaceholder: false } device)
            return;
        Run(context => device.Kind == DeviceKind.Output
            ? Native.SetSinkMute(context, device.Index, muted ? 1 : 0, null, 0)
            : Native.SetSourceMute(context, device.Index, muted ? 1 : 0, null, 0));
    }

    public void SetDefault(string key)
    {
        if (Find(key) is not { IsPlaceholder: false } device)
            return;
        Run(context => device.Kind == DeviceKind.Output
            ? Native.SetDefaultSink(context, device.Name, null, 0)
            : Native.SetDefaultSource(context, device.Name, null, 0));
    }

    public void SetPort(string key, string port)
    {
        if (Find(key) is not { IsPlaceholder: false } device)
            return;
        Run(context => device.Kind == DeviceKind.Output
            ? Native.SetSinkPort(context, device.Index, port, null, 0)
            : Native.SetSourcePort(context, device.Index, port, null, 0));
    }

    public void EnableDevice(string key)
    {
        if (Find(key) is { EnableProfile: { } profile } device)
            SetCardProfile(device.CardIndex, profile);
    }

    public void SetCardProfile(uint card, string profile) =>
        Run(context => Native.SetCardProfile(context, card, profile, null, 0));


    public void SetMonitor(string key, bool on)
    {
        if (Find(key) is not { Kind: DeviceKind.Input, IsPlaceholder: false } device
            || device.IsMonitored == on)
        {
            return;
        }

        if (on)
        {
            // The loopback plays into whatever the default output is, and follows it. The
            // tag in its stream name is how the module is found again in the module list,
            // across a restart of the panel too.
            var argument = $"source={device.Name} latency_msec=40 "
                + $"sink_input_properties=media.name={MonitorTag}";
            var handle = GCHandle.ToIntPtr(GCHandle.Alloc(this));
            Run(context => Native.LoadModule(context, "module-loopback", argument, &OnModuleLoaded, handle));
        }
        else
        {
            Run(context => _monitorModules.TryGetValue(device.Name, out var index)
                ? Native.UnloadModule(context, index, null, 0)
                : 0);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnModuleLoaded(nint context, uint index, nint userdata)
    {
        var handle = GCHandle.FromIntPtr(userdata);
        var feed = (PulseAudioFeed)handle.Target!;
        handle.Free();
        if (index == PaConst.InvalidIndex)
            return;
        lock (feed._gate)
            feed._loadedModules.Add(index);
    }

    // ---------------------------------------------------------------- meters

    public void SetMetering(string key, bool on)
    {
        lock (_gate)
        {
            if (on ? !_metered.Add(key) : !_metered.Remove(key))
                return;
        }
        if (_disposed || _mainloop == 0)
            return;
        Native.MainloopLock(_mainloop);
        try
        {
            ReconcileMeters();
        }
        finally
        {
            Native.MainloopUnlock(_mainloop);
        }
    }

    /// <summary>
    /// Start a peak stream for each metered input that has none, and stop the ones no longer
    /// wanted. Mainloop lock held.
    /// </summary>
    private void ReconcileMeters()
    {
        if (!_ready)
            return;
        List<AudioDevice> wanted;
        lock (_gate)
        {
            wanted = _devices.Where(device => device is { Kind: DeviceKind.Input, IsPlaceholder: false }
                && _metered.Contains(device.Key)).ToList();
        }

        foreach (var (key, meter) in _meters.ToList())
        {
            if (wanted.All(device => device.Key != key))
            {
                _meters.Remove(key);
                Native.StreamDisconnect(meter.Stream);
            }
        }

        foreach (var device in wanted)
        {
            if (_meters.ContainsKey(device.Key))
                continue;
            if (MeterStream.Open(this, device) is { } meter)
                _meters[device.Key] = meter;
        }
    }

    /// <summary>
    /// One input's peak stream. PEAK_DETECT has the server reduce the input to one frame per
    /// 1/25 s holding the peak of each channel, so the meters cost next to nothing.
    /// </summary>
    private sealed class MeterStream
    {
        private const uint Rate = 25;

        private MeterStream(PulseAudioFeed feed, string key, int channels)
        {
            Feed = feed;
            Key = key;
            Channels = channels;
        }

        public PulseAudioFeed Feed { get; }
        public string Key { get; }
        public int Channels { get; }
        public nint Stream { get; private set; }
        private GCHandle _handle;

        public static MeterStream? Open(PulseAudioFeed feed, AudioDevice device)
        {
            var stereo = device.Volumes.Count >= 2;
            var spec = new SampleSpec
            {
                Format = PaConst.SampleFloat32Le,
                Rate = Rate,
                Channels = (byte)(stereo ? 2 : 1),
            };
            var map = new ChannelMap { Channels = spec.Channels };
            if (stereo)
            {
                map.Map[0] = PaConst.ChannelFrontLeft;
                map.Map[1] = PaConst.ChannelFrontRight;
            }
            else
            {
                map.Map[0] = PaConst.ChannelMono;
            }

            var stream = Native.StreamNew(feed._context, Strings.MeterStreamName, &spec, &map);
            if (stream == 0)
                return null;

            var meter = new MeterStream(feed, device.Key, spec.Channels) { Stream = stream };
            meter._handle = GCHandle.Alloc(meter);
            var self = GCHandle.ToIntPtr(meter._handle);
            Native.StreamSetReadCallback(stream, &OnRead, self);
            Native.StreamSetStateCallback(stream, &OnState, self);

            var attr = new BufferAttr
            {
                MaxLength = uint.MaxValue,
                TLength = uint.MaxValue,
                PreBuf = uint.MaxValue,
                MinReq = uint.MaxValue,
                FragSize = (uint)(sizeof(float) * spec.Channels),
            };
            // DONT_MOVE: a meter belongs to its device, never to wherever the server would
            // rather route it. Not DONT_INHIBIT_AUTO_SUSPEND: a suspended input delivers
            // nothing, so a meter that let it sleep would never move. Metering is recording,
            // and the header lamp says so, as it did on IRIX.
            const uint flags = PaConst.StreamDontMove | PaConst.StreamPeakDetect
                | PaConst.StreamAdjustLatency;
            if (Native.StreamConnectRecord(stream, device.Name, &attr, flags) < 0)
            {
                meter.Release();
                return null;
            }
            return meter;
        }

        private void Release()
        {
            if (Stream == 0)
                return;
            Native.StreamSetReadCallback(Stream, null, 0);
            Native.StreamSetStateCallback(Stream, null, 0);
            Native.StreamUnref(Stream);
            Stream = 0;
            _handle.Free();
        }

        [UnmanagedCallersOnly]
        private static void OnState(nint stream, nint userdata)
        {
            var meter = (MeterStream)GCHandle.FromIntPtr(userdata).Target!;
            var state = Native.StreamGetState(stream);
            if (state is not (StreamState.Failed or StreamState.Terminated))
                return;
            // Forget it unless it has already been replaced, so the next read starts a fresh
            // one if the input is still metered.
            if (meter.Feed._meters.TryGetValue(meter.Key, out var current) && current == meter)
                meter.Feed._meters.Remove(meter.Key);
            meter.Release();
        }

        [UnmanagedCallersOnly]
        private static void OnRead(nint stream, nuint length, nint userdata)
        {
            var meter = (MeterStream)GCHandle.FromIntPtr(userdata).Target!;
            float left = 0, right = 0;
            var any = false;
            void* data;
            nuint bytes;
            while (Native.StreamPeek(stream, &data, &bytes) == 0 && bytes > 0)
            {
                if (data is not null)
                {
                    var samples = (float*)data;
                    var count = (int)(bytes / sizeof(float));
                    for (var i = 0; i + meter.Channels <= count; i += meter.Channels)
                    {
                        left = Math.Max(left, Math.Abs(samples[i]));
                        right = Math.Max(right, Math.Abs(samples[i + meter.Channels - 1]));
                    }
                    any = true;
                }
                Native.StreamDrop(stream);
            }
            if (any)
                meter.Feed.PeaksReceived?.Invoke(meter.Key, left, right);
        }
    }

    // ---------------------------------------------------------------- teardown

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_mainloop == 0)
            return;

        // The loopbacks this panel started stop with it, as Monitor in IRIX's apanel did.
        // Their unload requests have to reach the server before the connection closes.
        using var done = new CountdownEvent(1);
        var handle = GCHandle.Alloc(done);
        Native.MainloopLock(_mainloop);
        try
        {
            if (_ready)
            {
                uint[] loaded;
                lock (_gate)
                    loaded = [.. _loadedModules];
                foreach (var index in loaded)
                {
                    done.AddCount();
                    Native.OperationUnref(Native.UnloadModule(_context, index, &OnUnloaded,
                        GCHandle.ToIntPtr(handle)));
                }
            }
        }
        finally
        {
            Native.MainloopUnlock(_mainloop);
        }
        done.Signal();
        done.Wait(TimeSpan.FromSeconds(1));

        Native.MainloopLock(_mainloop);
        _ready = false;
        foreach (var meter in _meters.Values)
            Native.StreamDisconnect(meter.Stream);
        _meters.Clear();
        if (_context != 0)
        {
            Native.ContextSetStateCallback(_context, null, 0);
            Native.ContextSetSubscribeCallback(_context, null, 0);
            Native.ContextDisconnect(_context);
            Native.ContextUnref(_context);
            _context = 0;
        }
        Native.MainloopUnlock(_mainloop);
        Native.MainloopStop(_mainloop);
        Native.MainloopFree(_mainloop);
        _mainloop = 0;
        handle.Free();
        _self.Free();
    }

    [UnmanagedCallersOnly]
    private static void OnUnloaded(nint context, int success, nint userdata)
    {
        var done = (CountdownEvent)GCHandle.FromIntPtr(userdata).Target!;
        done.Signal();
    }

    private static PulseAudioFeed? From(nint userdata) =>
        userdata == 0 ? null : GCHandle.FromIntPtr(userdata).Target as PulseAudioFeed;

    private sealed record ServerSummary(string? DefaultSink, string? DefaultSource, bool IsPipeWire);

    private sealed record VolumeWrite(PulseAudioFeed Feed, string Key);

    internal sealed record RawProfile(string Name, string Description, uint Sinks, uint Sources,
        uint Priority, bool Available);

    internal sealed record RawCard(uint Index, string Name, string Description, DeviceBus Bus,
        IReadOnlyList<RawProfile> Profiles, string? ActiveProfile);
}

// The awaiting half: C# allows no await inside an unsafe context, and the other half is one.
internal sealed partial class PulseAudioFeed
{
    private async Task ReconnectAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        Reconnect();
    }

    private async Task ReadRatesAsync()
    {
        if (await PipeWireRates.ReadAsync().ConfigureAwait(false) is not { } rates)
            return;
        lock (_gate)
        {
            if (rates.Allowed.SequenceEqual(_allowedRates) && rates.Forced == _forcedRate)
                return;
            _allowedRates = rates.Allowed;
            _forcedRate = rates.Forced;
        }
        RaiseSnapshot();
    }

    public void SetForcedRate(int rate)
    {
        lock (_gate)
        {
            if (!_isPipeWire)
                return;
        }
        _ = Task.Run(async () =>
        {
            await PipeWireRates.SetForcedAsync(rate).ConfigureAwait(false);
            await ReadRatesAsync().ConfigureAwait(false);
        });
    }
}
