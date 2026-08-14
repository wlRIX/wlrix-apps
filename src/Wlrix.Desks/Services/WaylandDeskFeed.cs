// The live desk feed: talks the bespoke wlrix-desks Wayland protocol to the compositor.
//
// The C# bindings (NWayland.Protocols.WlrixDesksV1.*) are generated at build time by
// NWayland's source generator from the compositor's wlrix-desks.xml; see Wlrix.Desks.csproj.
//
// A dedicated background thread owns a *separate* wl_display connection (independent of
// Avalonia's rendering one), binds wlrix_desks_manager_v1, and dispatches its event queue.
// Listeners accumulate desk/window state keyed by protocol object; each `done` batch builds a
// DeskSnapshot and raises SnapshotReceived. Commands from the UI thread are marshaled onto
// the Wayland thread through an action queue (libwayland is not safe for cross-thread proxy
// use). If the compositor or the global is unreachable, Unavailable is raised and the thread
// retries — matching the old IPC client's contract.

using System.Collections.Concurrent;
using NWayland;
using NWayland.Protocols.Wayland;
using NWayland.Protocols.WlrixDesksV1;
using Wlrix.Desks.Models;

namespace Wlrix.Desks.Services;

public sealed class WaylandDeskFeed : IDeskFeed
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    // Poll cadence for the dispatch loop. A roundtrip drains all pending events (including
    // live geometry), so ~30 Hz keeps drags smooth without a busy spin.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(33);

    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private bool _notifiedUnavailable;

    // Accumulated state, only touched on the Wayland thread.
    private readonly Dictionary<WlrixDeskV1, DeskState> _desks = new();
    private readonly Dictionary<WlrixToplevelV1, ToplevelState> _toplevels = new();
    private long _nextWindowId = 1;

    public event Action<DeskSnapshot>? SnapshotReceived;
    public event Action? Unavailable;

    public void Start() => _thread ??= StartThread();

    // Commands are fire-and-forget: the protocol has no command replies, so there is nothing
    // to await or reject. They run on the Wayland thread via the action queue.
    public Task SwitchAsync(int id)
    {
        Post(() => Desk(id)?.Activate());
        return Task.CompletedTask;
    }

    public Task CreateAsync(string name)
    {
        // The compositor names new desks itself ("Desk N"); the requested name is advisory and
        // could be applied with set_name once the new desk arrives, if desired.
        Post(() => Manager?.CreateDesk());
        return Task.CompletedTask;
    }

    public Task RemoveAsync(int id)
    {
        Post(() => Desk(id)?.Remove());
        return Task.CompletedTask;
    }

    public Task RenameAsync(int id, string name)
    {
        // The compositor answers with a fresh `name` event, so the tile picks the rename up
        // through the normal snapshot path rather than from this call.
        Post(() => Desk(id)?.SetName(name));
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (!_cts.IsCancellationRequested)
            _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(1));
        _cts.Dispose();
    }

    private WlrixDesksManagerV1? Manager { get; set; }

    private void Post(Action command) => _commands.Enqueue(command);

    private WlrixDeskV1? Desk(int id) =>
        _desks.FirstOrDefault(kv => kv.Value.Id == id).Key;

    private Thread StartThread()
    {
        var thread = new Thread(() => Run(_cts.Token))
        {
            Name = "wlrix-desks-wayland",
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

        var registry = display.GetRegistry(new RegistryListener(this), queue);
        // First roundtrip surfaces the existing globals so we can bind the manager.
        queue.Roundtrip();
        if (Manager is null)
            throw new InvalidOperationException("compositor does not offer wlrix-desks");

        _notifiedUnavailable = false;

        while (!ct.IsCancellationRequested)
        {
            while (_commands.TryDequeue(out var command))
                command();
            display.Flush();
            // Roundtrip drains all pending events for our queue (desk/window/geometry), whose
            // listeners raise SnapshotReceived on each `done`.
            queue.Roundtrip();
            if (!Sleep(PollInterval, ct))
                return;
        }
    }

    private void Reset()
    {
        Manager = null;
        _desks.Clear();
        _toplevels.Clear();
    }

    // Build and raise a snapshot from the accumulated state (called on each manager `done`).
    private void Emit()
    {
        var desks = _desks.Values
            .Select(d => new DeskInfo((int)d.Id, d.Active, d.Name))
            .ToList();

        var windows = _toplevels.Values
            .Select(t => new WindowInfo(
                t.WindowId,
                t.Desk is { } deskObj && _desks.TryGetValue(deskObj, out var d) ? (int)d.Id : 0,
                t.X, t.Y, t.Width, t.Height,
                t.Minimized, t.AppId, t.Title))
            .ToList();

        SnapshotReceived?.Invoke(new DeskSnapshot(desks, windows));
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

    private sealed class DeskState
    {
        public uint Id;
        public string Name = string.Empty;
        public bool Global;
        public bool Active;
    }

    private sealed class ToplevelState
    {
        public long WindowId;
        public string AppId = string.Empty;
        public string Title = string.Empty;
        public double X, Y, Width, Height;
        public bool Minimized;
        public bool Maximized;
        public bool Activated;
        public WlrixDeskV1? Desk;
    }

    // ── Listeners ───────────────────────────────────────────────────────────────────────

    private sealed class RegistryListener(WaylandDeskFeed feed) : WlRegistry.Listener
    {
        protected override void Global(WlRegistry sender, uint name, string @interface, uint version)
        {
            if (@interface != WlrixDesksManagerV1.ProxyType.Interface.Name)
                return;
            var bind = Math.Min(version, (uint)WlrixDesksManagerV1.ProxyType.Interface.Version);
            feed.Manager = sender.Bind<WlrixDesksManagerV1>(name, bind, new ManagerListener(feed));
        }
    }

    private sealed class ManagerListener(WaylandDeskFeed feed) : WlrixDesksManagerV1.Listener
    {
        // The manager announces children as NewIds: realize each into a proxy with its listener.
        protected override void Desk(
            WlrixDesksManagerV1 sender, NewId<WlrixDeskV1, WlrixDeskV1.Listener> desk)
        {
            var proxy = desk.GetAndConsume(new DeskListener(feed));
            feed._desks[proxy] = new DeskState();
        }

        protected override void Toplevel(
            WlrixDesksManagerV1 sender, NewId<WlrixToplevelV1, WlrixToplevelV1.Listener> toplevel)
        {
            var proxy = toplevel.GetAndConsume(new ToplevelListener(feed));
            feed._toplevels[proxy] = new ToplevelState { WindowId = feed._nextWindowId++ };
        }

        protected override void Done(WlrixDesksManagerV1 sender) => feed.Emit();

        protected override void Finished(WlrixDesksManagerV1 sender) => feed.Reset();
    }

    private sealed class DeskListener(WaylandDeskFeed feed) : WlrixDeskV1.Listener
    {
        protected override void Id(WlrixDeskV1 sender, uint id)
        {
            if (feed._desks.TryGetValue(sender, out var d)) d.Id = id;
        }

        protected override void Name(WlrixDeskV1 sender, string name)
        {
            if (feed._desks.TryGetValue(sender, out var d)) d.Name = name;
        }

        protected override void Global(WlrixDeskV1 sender)
        {
            if (feed._desks.TryGetValue(sender, out var d)) d.Global = true;
        }

        protected override void Activated(WlrixDeskV1 sender)
        {
            if (feed._desks.TryGetValue(sender, out var d)) d.Active = true;
        }

        protected override void Deactivated(WlrixDeskV1 sender)
        {
            if (feed._desks.TryGetValue(sender, out var d)) d.Active = false;
        }

        protected override void Removed(WlrixDeskV1 sender) => feed._desks.Remove(sender);
    }

    private sealed class ToplevelListener(WaylandDeskFeed feed) : WlrixToplevelV1.Listener
    {
        protected override void AppId(WlrixToplevelV1 sender, string appId)
        {
            if (feed._toplevels.TryGetValue(sender, out var t)) t.AppId = appId;
        }

        protected override void Title(WlrixToplevelV1 sender, string title)
        {
            if (feed._toplevels.TryGetValue(sender, out var t)) t.Title = title;
        }

        protected override void Geometry(WlrixToplevelV1 sender, int x, int y, int width, int height)
        {
            if (feed._toplevels.TryGetValue(sender, out var t))
            {
                t.X = x;
                t.Y = y;
                t.Width = width;
                t.Height = height;
            }
        }

        protected override void State(WlrixToplevelV1 sender, ReadOnlySpan<byte> state)
        {
            if (!feed._toplevels.TryGetValue(sender, out var t))
                return;
            // The array is a sequence of little-endian u32 state flags. Can't close over a
            // ref-struct span in a helper, so scan it inline.
            bool minimized = false, maximized = false, activated = false;
            for (var i = 0; i + 4 <= state.Length; i += 4)
            {
                var flag = BitConverter.ToUInt32(state.Slice(i, 4));
                if (flag == (uint)WlrixToplevelV1.StateEnum.Minimized) minimized = true;
                else if (flag == (uint)WlrixToplevelV1.StateEnum.Maximized) maximized = true;
                else if (flag == (uint)WlrixToplevelV1.StateEnum.Activated) activated = true;
            }
            t.Minimized = minimized;
            t.Maximized = maximized;
            t.Activated = activated;
        }

        protected override void Desk(WlrixToplevelV1 sender, WlrixDeskV1? desk)
        {
            if (feed._toplevels.TryGetValue(sender, out var t)) t.Desk = desk;
        }

        protected override void Closed(WlrixToplevelV1 sender) => feed._toplevels.Remove(sender);
    }
}
