// The live output feed: wlr-output-management for the displays themselves, and the bespoke
// wlrix-output-color extension for what that protocol cannot say (HDR, and whether adaptive
// sync is possible at all).
//
// The threading follows Wlrix.Desks' WaylandDeskFeed: a dedicated thread owns a separate
// wl_display connection, everything the UI asks for is queued onto that thread (libwayland is
// not safe for cross-thread proxy use), and the queue is drained between roundtrips.
//
// wlr-output-management describes the displays as a burst of head and mode objects closed by
// the manager's `done`, and re-creates every one of them whenever anything changes. So a
// snapshot is built at each `done`: an extension is asked for every head, and the snapshot is
// raised once the last extension has answered.

using System.Collections.Concurrent;
using NWayland;
using NWayland.Protocols.Wayland;
using NWayland.Protocols.WlrixOutputColorV1;
using NWayland.Protocols.WlrOutputManagementUnstableV1;
using Wlrix.Settings.Displays.Models;

namespace Wlrix.Settings.Displays.Services;

public sealed class WaylandOutputFeed : IOutputFeed
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    // Events only arrive when something changes, so there is no need to poll as often as Desks
    // does for live window geometry; this only bounds how long an Apply waits to be sent.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private bool _notifiedUnavailable;

    // Accumulated state, only touched on the Wayland thread.
    private ZwlrOutputManagerV1? _manager;
    private uint _managerVersion;
    private WlrixOutputColorManagerV1? _color;
    private uint _serial;
    private readonly List<HeadState> _heads = [];
    private readonly Dictionary<ZwlrOutputModeV1, ModeState> _modes = new();
    // Extensions still to answer for the current burst; the snapshot waits for them.
    private int _pendingExtensions;
    private readonly List<TaskCompletionSource<ApplyResult>> _inFlight = [];

    public event Action<OutputSnapshot>? SnapshotReceived;
    public event Action? Unavailable;

    public void Start() => _thread ??= StartThread();

    public Task<ApplyResult> ApplyAsync(IReadOnlyList<OutputChange> changes)
    {
        var result = new TaskCompletionSource<ApplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Enqueue(() => Configure(changes, result));
        return result.Task;
    }

    public void Dispose()
    {
        if (!_cts.IsCancellationRequested)
            _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(1));
        _cts.Dispose();
    }

    // ── Configuring ─────────────────────────────────────────────────────────────────────

    private void Configure(IReadOnlyList<OutputChange> changes, TaskCompletionSource<ApplyResult> result)
    {
        if (_manager is not { } manager)
        {
            result.TrySetResult(ApplyResult.Unavailable);
            return;
        }

        _inFlight.Add(result);
        var configuration = manager.CreateConfiguration(_serial, new ConfigurationListener(this, result));

        foreach (var change in changes)
        {
            if (_heads.Find(h => h.Name == change.Name) is not { } head)
                continue;

            if (!change.Enabled)
            {
                configuration.DisableHead(head.Proxy);
                continue;
            }

            var configured = configuration.EnableHead(head.Proxy);

            // A mode the head does not offer is left out rather than sent: the compositor would
            // refuse the whole configuration over it.
            var mode = head.Modes.FirstOrDefault(m => _modes.TryGetValue(m, out var state) && state.ToModel().SameAs(change.Mode));
            if (mode is not null && (mode != head.CurrentMode || !head.Enabled))
                configured.SetMode(mode);

            configured.SetPosition(change.X, change.Y);
            configured.SetTransform((WlOutput.TransformEnum)(int)change.Transform);
            configured.SetScale(new WlFixed(change.Scale));

            // Only where it can work: asking for adaptive sync either way on a head that cannot
            // do it fails the configuration.
            if (_managerVersion >= 4 && head.AdaptiveSyncSupported && change.AdaptiveSync != head.AdaptiveSync)
            {
                configured.SetAdaptiveSync(change.AdaptiveSync
                    ? ZwlrOutputHeadV1.AdaptiveSyncStateEnum.Enabled
                    : ZwlrOutputHeadV1.AdaptiveSyncStateEnum.Disabled);
            }

            if (_color is { } color
                && (change.Hdr != head.HdrEnabled || change.SdrWhiteNits != head.SdrWhiteNits))
            {
                var extension = color.GetConfigurationHead(configured);
                if (change.Hdr != head.HdrEnabled)
                    extension.SetHdr(change.Hdr ? 1u : 0u);
                if (change.SdrWhiteNits != head.SdrWhiteNits)
                    extension.SetSdrWhiteLevel((uint)change.SdrWhiteNits);
                // What it staged stays staged.
                extension.Destroy();
            }
        }

        configuration.Apply();
    }

    private void Finish(ZwlrOutputConfigurationV1 configuration, TaskCompletionSource<ApplyResult> result, ApplyResult outcome)
    {
        _inFlight.Remove(result);
        configuration.Destroy();
        result.TrySetResult(outcome);
    }

    // ── Snapshots ───────────────────────────────────────────────────────────────────────

    private void OnDone(uint serial)
    {
        _serial = serial;
        if (_color is not { } color)
        {
            Emit();
            return;
        }

        _pendingExtensions = _heads.Count;
        if (_pendingExtensions == 0)
        {
            Emit();
            return;
        }
        foreach (var head in _heads)
            color.GetHead(head.Proxy, new ExtensionListener(this, head));
    }

    private void OnExtensionDone(HeadState head)
    {
        // An extension answering for a head a later burst has already replaced is stale.
        if (!_heads.Contains(head))
            return;
        if (--_pendingExtensions == 0)
            Emit();
    }

    private void Emit()
    {
        var heads = _heads.Select(head => new OutputHead(
                head.Name,
                head.Make,
                head.Model,
                head.Serial,
                head.Description,
                head.PhysicalWidth,
                head.PhysicalHeight,
                head.Modes.Where(_modes.ContainsKey).Select(m => _modes[m].ToModel()).ToList(),
                head.CurrentMode is { } current && _modes.TryGetValue(current, out var mode) ? mode.ToModel() : null,
                head.Enabled,
                head.X,
                head.Y,
                head.Transform,
                head.Scale,
                head.AdaptiveSync,
                head.AdaptiveSyncSupported,
                head.HdrSupported,
                head.HdrEnabled,
                head.SdrWhiteNits))
            .ToList();
        SnapshotReceived?.Invoke(new OutputSnapshot(heads));
    }

    // ── Connection ──────────────────────────────────────────────────────────────────────

    private Thread StartThread()
    {
        var thread = new Thread(() => Run(_cts.Token))
        {
            Name = "wlrix-displays-wayland",
            IsBackground = true,
        };
        thread.Start();
        return thread;
    }

    private void Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                RunConnection(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                NotifyUnavailable();
            }

            Reset();
            if (ct.IsCancellationRequested || !Sleep(RetryDelay, ct))
                return;
        }
    }

    private void RunConnection(CancellationToken ct)
    {
        using var display = WlDisplay.Connect(null);
        var queue = display.CreateEventQueue();

        display.GetRegistry(new RegistryListener(this), queue);
        // The first roundtrip surfaces the globals, so the managers can be bound.
        queue.Roundtrip();
        if (_manager is null)
            throw new InvalidOperationException("compositor does not offer wlr-output-management");

        _notifiedUnavailable = false;

        while (!ct.IsCancellationRequested)
        {
            while (_commands.TryDequeue(out var command))
                command();
            display.Flush();
            queue.Roundtrip();
            if (!Sleep(PollInterval, ct))
                return;
        }
    }

    private void Reset()
    {
        _manager = null;
        _color = null;
        _heads.Clear();
        _modes.Clear();
        _pendingExtensions = 0;
        // A configuration in flight on a dead connection will never be answered.
        foreach (var result in _inFlight)
            result.TrySetResult(ApplyResult.Unavailable);
        _inFlight.Clear();
        // Nor will anything still queued be sent.
        while (_commands.TryDequeue(out var command))
            command();
    }

    private void NotifyUnavailable()
    {
        if (_notifiedUnavailable)
            return;
        _notifiedUnavailable = true;
        Unavailable?.Invoke();
    }

    private static bool Sleep(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            return !ct.WaitHandle.WaitOne(delay);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    // ── Accumulated per-object state ────────────────────────────────────────────────────

    private sealed class HeadState(ZwlrOutputHeadV1 proxy)
    {
        public readonly ZwlrOutputHeadV1 Proxy = proxy;
        public string Name = string.Empty;
        public string Description = string.Empty;
        public string Make = string.Empty;
        public string Model = string.Empty;
        public string? Serial;
        public int PhysicalWidth, PhysicalHeight;
        public readonly List<ZwlrOutputModeV1> Modes = [];
        public ZwlrOutputModeV1? CurrentMode;
        public bool Enabled;
        public int X, Y;
        public OutputTransform Transform;
        public double Scale = 1.0;
        public bool AdaptiveSync;
        // From the extension; until it answers, nothing is supported.
        public bool AdaptiveSyncSupported;
        public bool HdrSupported;
        public bool HdrEnabled;
        public int SdrWhiteNits = 203;
    }

    private sealed class ModeState
    {
        public int Width, Height, Refresh;
        public bool Preferred;

        public OutputMode ToModel() => new(Width, Height, Refresh, Preferred);
    }

    // ── Listeners ───────────────────────────────────────────────────────────────────────

    private sealed class RegistryListener(WaylandOutputFeed feed) : WlRegistry.Listener
    {
        protected override void Global(WlRegistry sender, uint name, string @interface, uint version)
        {
            if (@interface == ZwlrOutputManagerV1.ProxyType.Interface.Name)
            {
                feed._managerVersion = Math.Min(version, (uint)ZwlrOutputManagerV1.ProxyType.Interface.Version);
                feed._manager = sender.Bind<ZwlrOutputManagerV1>(name, feed._managerVersion, new ManagerListener(feed));
            }
            else if (@interface == WlrixOutputColorManagerV1.ProxyType.Interface.Name)
            {
                var bind = Math.Min(version, (uint)WlrixOutputColorManagerV1.ProxyType.Interface.Version);
                feed._color = sender.Bind<WlrixOutputColorManagerV1>(name, bind);
            }
        }
    }

    private sealed class ManagerListener(WaylandOutputFeed feed) : ZwlrOutputManagerV1.Listener
    {
        protected override void Head(
            ZwlrOutputManagerV1 eventSender, NewId<ZwlrOutputHeadV1, ZwlrOutputHeadV1.Listener> head)
        {
            var state = new HeadState(head.GetAndConsume(new HeadListener(feed)));
            feed._heads.Add(state);
        }

        protected override void Done(ZwlrOutputManagerV1 eventSender, uint serial) => feed.OnDone(serial);

        protected override void Finished(ZwlrOutputManagerV1 eventSender) => feed._manager = null;
    }

    private sealed class HeadListener(WaylandOutputFeed feed) : ZwlrOutputHeadV1.Listener
    {
        private HeadState? State(ZwlrOutputHeadV1 proxy) => feed._heads.Find(h => h.Proxy == proxy);

        protected override void Name(ZwlrOutputHeadV1 eventSender, string name)
        {
            if (State(eventSender) is { } h) h.Name = name;
        }

        protected override void Description(ZwlrOutputHeadV1 eventSender, string description)
        {
            if (State(eventSender) is { } h) h.Description = description;
        }

        protected override void PhysicalSize(ZwlrOutputHeadV1 eventSender, int width, int height)
        {
            if (State(eventSender) is not { } h) return;
            h.PhysicalWidth = width;
            h.PhysicalHeight = height;
        }

        protected override void Mode(
            ZwlrOutputHeadV1 eventSender, NewId<ZwlrOutputModeV1, ZwlrOutputModeV1.Listener> mode)
        {
            var proxy = mode.GetAndConsume(new ModeListener(feed));
            feed._modes[proxy] = new ModeState();
            State(eventSender)?.Modes.Add(proxy);
        }

        protected override void Enabled(ZwlrOutputHeadV1 eventSender, int enabled)
        {
            if (State(eventSender) is { } h) h.Enabled = enabled != 0;
        }

        protected override void CurrentMode(ZwlrOutputHeadV1 eventSender, ZwlrOutputModeV1? mode)
        {
            if (State(eventSender) is { } h) h.CurrentMode = mode;
        }

        protected override void Position(ZwlrOutputHeadV1 eventSender, int x, int y)
        {
            if (State(eventSender) is not { } h) return;
            h.X = x;
            h.Y = y;
        }

        protected override void Transform(ZwlrOutputHeadV1 eventSender, WlOutput.TransformEnum transform)
        {
            if (State(eventSender) is { } h) h.Transform = (OutputTransform)(int)transform;
        }

        protected override void Scale(ZwlrOutputHeadV1 eventSender, WlFixed scale)
        {
            if (State(eventSender) is { } h) h.Scale = (double)scale;
        }

        protected override void Make(ZwlrOutputHeadV1 eventSender, string make)
        {
            if (State(eventSender) is { } h) h.Make = make;
        }

        protected override void Model(ZwlrOutputHeadV1 eventSender, string model)
        {
            if (State(eventSender) is { } h) h.Model = model;
        }

        protected override void SerialNumber(ZwlrOutputHeadV1 eventSender, string serialNumber)
        {
            if (State(eventSender) is { } h) h.Serial = serialNumber;
        }

        protected override void AdaptiveSync(ZwlrOutputHeadV1 eventSender, ZwlrOutputHeadV1.AdaptiveSyncStateEnum state)
        {
            if (State(eventSender) is { } h) h.AdaptiveSync = state == ZwlrOutputHeadV1.AdaptiveSyncStateEnum.Enabled;
        }

        protected override void Finished(ZwlrOutputHeadV1 eventSender)
        {
            if (State(eventSender) is { } h)
                feed._heads.Remove(h);
            // `release` arrived in version 3; before it the compositor destroys the object.
            if (feed._managerVersion >= 3)
                eventSender.Release();
        }
    }

    private sealed class ModeListener(WaylandOutputFeed feed) : ZwlrOutputModeV1.Listener
    {
        protected override void Size(ZwlrOutputModeV1 eventSender, int width, int height)
        {
            if (!feed._modes.TryGetValue(eventSender, out var m)) return;
            m.Width = width;
            m.Height = height;
        }

        protected override void Refresh(ZwlrOutputModeV1 eventSender, int refresh)
        {
            if (feed._modes.TryGetValue(eventSender, out var m)) m.Refresh = refresh;
        }

        protected override void Preferred(ZwlrOutputModeV1 eventSender)
        {
            if (feed._modes.TryGetValue(eventSender, out var m)) m.Preferred = true;
        }

        protected override void Finished(ZwlrOutputModeV1 eventSender)
        {
            feed._modes.Remove(eventSender);
            if (feed._managerVersion >= 3)
                eventSender.Release();
        }
    }

    private sealed class ExtensionListener(WaylandOutputFeed feed, HeadState head) : WlrixOutputColorHeadV1.Listener
    {
        protected override void HdrSupported(WlrixOutputColorHeadV1 eventSender, uint supported) =>
            head.HdrSupported = supported != 0;

        protected override void Hdr(WlrixOutputColorHeadV1 eventSender, uint enabled) =>
            head.HdrEnabled = enabled != 0;

        protected override void SdrWhiteLevel(WlrixOutputColorHeadV1 eventSender, uint nits) =>
            head.SdrWhiteNits = (int)nits;

        protected override void AdaptiveSyncSupported(WlrixOutputColorHeadV1 eventSender, uint supported) =>
            head.AdaptiveSyncSupported = supported != 0;

        protected override void Done(WlrixOutputColorHeadV1 eventSender)
        {
            // Everything it will ever say has been said.
            eventSender.Destroy();
            feed.OnExtensionDone(head);
        }
    }

    private sealed class ConfigurationListener(WaylandOutputFeed feed, TaskCompletionSource<ApplyResult> result)
        : ZwlrOutputConfigurationV1.Listener
    {
        protected override void Succeeded(ZwlrOutputConfigurationV1 eventSender) =>
            feed.Finish(eventSender, result, ApplyResult.Succeeded);

        protected override void Failed(ZwlrOutputConfigurationV1 eventSender) =>
            feed.Finish(eventSender, result, ApplyResult.Failed);

        protected override void Cancelled(ZwlrOutputConfigurationV1 eventSender) =>
            feed.Finish(eventSender, result, ApplyResult.Cancelled);
    }
}
