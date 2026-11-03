// The iconbox's view of the compositor's windows, over the wlrix-desks Wayland protocol.
//
// The same arrangement as Wlrix.Desks' WaylandDeskFeed, which explains it at length: a
// background thread owns a separate wl_display connection, binds wlrix_desks_manager_v1, and
// dispatches its queue; listeners accumulate state per protocol object, and each `done` raises
// one snapshot. Commands from the UI thread run on the Wayland thread through an action queue,
// since libwayland proxies are not safe to use across threads. Unreachable means Unavailable and
// a retry.
//
// Only what the iconbox needs is kept: which desks are showing, and each window's app id,
// title, state flags, desk and capabilities. Geometry and stacking are not looked at.

using System.Collections.Concurrent;
using NWayland;
using NWayland.Protocols.Wayland;
using NWayland.Protocols.WlrixDesksV1;
using Wlrix.Iconbox.Models;

namespace Wlrix.Iconbox.Services;

public sealed class ToplevelFeed : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    // Titles and state, not live geometry, so a slower poll than the Desks overview's is plenty
    // for a click to show up as a change of tile.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    // The version that added `capabilities` and `show_menu`.
    private const uint CapabilitiesSince = 5;

    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private bool _notifiedUnavailable;

    // Accumulated state, only touched on the Wayland thread.
    private readonly Dictionary<WlrixDeskV1, DeskState> _desks = new();
    private readonly Dictionary<WlrixToplevelV1, ToplevelState> _toplevels = new();
    private long _nextWindowId = 1;

    /// <summary>Every window the compositor reports, after each batch. Raised on the Wayland thread.</summary>
    public event Action<IReadOnlyList<ToplevelInfo>>? SnapshotReceived;

    /// <summary>The compositor or its wlrix-desks global could not be reached. Raised on the Wayland thread.</summary>
    public event Action? Unavailable;

    /// <summary>
    /// A connection that worked has gone: the compositor has exited. Raised on the Wayland
    /// thread, once, after which the feed stops trying.
    /// </summary>
    public event Action? Lost;

    public void Start() => _thread ??= StartThread();

    /// <summary>Minimize the window, if it is still there.</summary>
    public void Minimize(long id) => Post(() => Toplevel(id)?.Minimize());

    /// <summary>Restore the window, which also focuses and raises it.</summary>
    public void Restore(long id) => Post(() => Toplevel(id)?.Restore());

    /// <summary>
    /// Open the compositor's window menu for the window, at the pointer. Nothing on a
    /// compositor older than version 5, which has no way to be asked.
    /// </summary>
    public void ShowMenu(long id) => Post(() =>
    {
        if (Toplevel(id) is { } toplevel && toplevel.Version >= CapabilitiesSince)
            toplevel.ShowMenu();
    });

    public void Dispose()
    {
        if (!_cts.IsCancellationRequested)
            _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(1));
        _cts.Dispose();
    }

    private WlrixDesksManagerV1? Manager { get; set; }

    private void Post(Action command) => _commands.Enqueue(command);

    private WlrixToplevelV1? Toplevel(long id) =>
        _toplevels.FirstOrDefault(kv => kv.Value.WindowId == id).Key;

    private Thread StartThread()
    {
        var thread = new Thread(() => Run(_cts.Token))
        {
            Name = "wlrix-iconbox-wayland",
            IsBackground = true,
        };
        thread.Start();
        return thread;
    }

    private void Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var connected = false;
            try
            {
                RunConnection(ct, () => connected = true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // The compositor started this client, so one that was there and is gone is not
                // coming back on this socket; a retry would only keep the process alive, and an
                // Avalonia window outlives its display.
                if (connected)
                {
                    Lost?.Invoke();
                    return;
                }
                NotifyUnavailable();
            }

            Reset();
            if (ct.IsCancellationRequested || !Sleep(RetryDelay, ct))
                return;
        }
    }

    private void RunConnection(CancellationToken ct, Action connected)
    {
        using var display = WlDisplay.Connect(null);
        var queue = display.CreateEventQueue();

        var registry = display.GetRegistry(new RegistryListener(this), queue);
        queue.Roundtrip();
        if (Manager is null)
            throw new InvalidOperationException("compositor does not offer wlrix-desks");

        _notifiedUnavailable = false;
        connected();

        while (!ct.IsCancellationRequested)
        {
            while (_commands.TryDequeue(out var command))
                command();
            display.Flush();
            // -1 rather than an exception is how NWayland reports a connection that has
            // failed, which is what the compositor exiting looks like from here.
            if (queue.Roundtrip() < 0)
                throw new IOException("lost the connection to the compositor");
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

    private void Emit()
    {
        var windows = _toplevels.Values
            .OrderBy(t => t.WindowId)
            .Select(t => new ToplevelInfo(
                t.WindowId,
                t.AppId,
                t.Title,
                t.Minimized,
                t.Activated,
                t.Desk is { } desk && _desks.TryGetValue(desk, out var d) && (d.Global || d.Active),
                t.Minimizable))
            .ToList();
        SnapshotReceived?.Invoke(windows);
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
        public bool Global;
        public bool Active;
    }

    private sealed class ToplevelState
    {
        public long WindowId;
        public string AppId = string.Empty;
        public string Title = string.Empty;
        public bool Minimized;
        public bool Activated;
        // Until a version-5 compositor says otherwise; an older one never will.
        public bool Minimizable = true;
        public WlrixDeskV1? Desk;
    }

    // ── Listeners ───────────────────────────────────────────────────────────────────────

    private sealed class RegistryListener(ToplevelFeed feed) : WlRegistry.Listener
    {
        protected override void Global(WlRegistry sender, uint name, string @interface, uint version)
        {
            if (@interface != WlrixDesksManagerV1.ProxyType.Interface.Name)
                return;
            var bind = Math.Min(version, (uint)WlrixDesksManagerV1.ProxyType.Interface.Version);
            feed.Manager = sender.Bind<WlrixDesksManagerV1>(name, bind, new ManagerListener(feed));
        }
    }

    private sealed class ManagerListener(ToplevelFeed feed) : WlrixDesksManagerV1.Listener
    {
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

    private sealed class DeskListener(ToplevelFeed feed) : WlrixDeskV1.Listener
    {
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

    private sealed class ToplevelListener(ToplevelFeed feed) : WlrixToplevelV1.Listener
    {
        protected override void AppId(WlrixToplevelV1 sender, string appId)
        {
            if (feed._toplevels.TryGetValue(sender, out var t)) t.AppId = appId;
        }

        protected override void Title(WlrixToplevelV1 sender, string title)
        {
            if (feed._toplevels.TryGetValue(sender, out var t)) t.Title = title;
        }

        protected override void State(WlrixToplevelV1 sender, ReadOnlySpan<byte> state)
        {
            if (!feed._toplevels.TryGetValue(sender, out var t))
                return;
            // A sequence of u32 state flags; see WaylandDeskFeed.
            bool minimized = false, activated = false;
            for (var i = 0; i + 4 <= state.Length; i += 4)
            {
                var flag = BitConverter.ToUInt32(state.Slice(i, 4));
                if (flag == (uint)WlrixToplevelV1.StateEnum.Minimized) minimized = true;
                else if (flag == (uint)WlrixToplevelV1.StateEnum.Activated) activated = true;
            }
            t.Minimized = minimized;
            t.Activated = activated;
        }

        protected override void Desk(WlrixToplevelV1 sender, WlrixDeskV1? desk)
        {
            if (feed._toplevels.TryGetValue(sender, out var t)) t.Desk = desk;
        }

        protected override void Capabilities(WlrixToplevelV1 sender, WlrixToplevelV1.CapabilityEnum capabilities)
        {
            if (feed._toplevels.TryGetValue(sender, out var t))
                t.Minimizable = capabilities.HasFlag(WlrixToplevelV1.CapabilityEnum.Minimize);
        }

        protected override void Closed(WlrixToplevelV1 sender) => feed._toplevels.Remove(sender);
    }
}
