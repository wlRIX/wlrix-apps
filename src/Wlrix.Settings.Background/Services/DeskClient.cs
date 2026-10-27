using System.Collections.Concurrent;
using NWayland;
using NWayland.Protocols.Wayland;
using NWayland.Protocols.WlrixDesksV1;

namespace Wlrix.Settings.Background.Services;

/// <summary>A desk's own background, as <c>wlrix-desks</c> carries it: <c>background.toml</c>'s three values.</summary>
public sealed record DeskBackground(string Image, string Mode, string Color);

/// <summary>One ordinary desk, as the compositor last described it.</summary>
/// <param name="Background">Its own background, or null when it shows the shared one.</param>
public sealed record DeskEntry(int Id, string Name, bool Active, DeskBackground? Background);

/// <summary>
/// The desks, over the compositor's <c>wlrix-desks</c> protocol: which there are, which has a
/// background of its own, and setting or clearing one.
/// </summary>
/// <remarks>
/// <para>
/// A desk's own background is the compositor's to keep, with the desk, rather than a key in
/// <c>background.toml</c>: desk ids are handed out fresh each session and names are the user's
/// to change, so nothing in a file could name a desk and go on meaning the same one. The
/// compositor saves it with the desk and relays it to <c>wlrix-bg</c>, which shows it.
/// </para>
/// <para>
/// The same arrangement as Desks' feed, cut down to what this panel needs: a thread of its own
/// on its own connection, commands queued onto it, and a list raised after each complete
/// batch. Desks before version 4 have no backgrounds, so a compositor older than that raises
/// nothing and the panel keeps to the shared background.
/// </para>
/// </remarks>
public sealed class DeskClient : IDisposable
{
    /// <summary>The version that gave desks backgrounds.</summary>
    private const uint BackgroundSince = 4;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<WlrixDeskV1, DeskState> _desks = new();
    private readonly List<WlrixDeskV1> _order = [];
    private WlrixDesksManagerV1? _manager;
    private Thread? _thread;

    /// <summary>The ordinary desks, raised on the client's own thread after every batch.</summary>
    public event Action<IReadOnlyList<DeskEntry>>? Changed;

    public void Start()
    {
        _thread ??= new Thread(() => Run(_cts.Token)) { Name = "wlrix-desks-background", IsBackground = true };
        if (!_thread.IsAlive)
            _thread.Start();
    }

    public void SetBackground(int deskId, DeskBackground background) =>
        _commands.Enqueue(() => Desk(deskId)?.SetBackground(background.Image, background.Mode, background.Color));

    public void ClearBackground(int deskId) => _commands.Enqueue(() => Desk(deskId)?.ClearBackground());

    public void Dispose()
    {
        if (!_cts.IsCancellationRequested)
            _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(1));
        _cts.Dispose();
    }

    private WlrixDeskV1? Desk(int id) => _desks.FirstOrDefault(pair => pair.Value.Id == id).Key;

    private void Run(CancellationToken ct)
    {
        try
        {
            using var display = WlDisplay.Connect(null);
            var queue = display.CreateEventQueue();
            using var registry = display.GetRegistry(new RegistryListener(this), queue);
            queue.Roundtrip();
            if (_manager is null)
                return;

            while (!ct.IsCancellationRequested)
            {
                while (_commands.TryDequeue(out var command))
                    command();
                display.Flush();
                queue.Roundtrip();
                if (ct.WaitHandle.WaitOne(PollInterval))
                    return;
            }
        }
        catch (Exception)
        {
            // No compositor, or one that went away: the panel keeps to the shared background,
            // which is all it could do before desks had backgrounds.
        }
    }

    private void Emit() => Changed?.Invoke(
    [
        .. _order
            .Where(_desks.ContainsKey)
            .Select(proxy => _desks[proxy])
            .Where(desk => !desk.Global)
            .Select(desk => new DeskEntry((int)desk.Id, desk.Name, desk.Active, desk.Background)),
    ]);

    private sealed class DeskState
    {
        public uint Id;
        public string Name = string.Empty;
        public bool Global;
        public bool Active;
        public DeskBackground? Background;
    }

    private sealed class RegistryListener(DeskClient client) : WlRegistry.Listener
    {
        protected override void Global(WlRegistry sender, uint name, string @interface, uint version)
        {
            if (@interface != WlrixDesksManagerV1.ProxyType.Interface.Name || version < BackgroundSince)
                return;
            var bind = Math.Min(version, (uint)WlrixDesksManagerV1.ProxyType.Interface.Version);
            client._manager = sender.Bind<WlrixDesksManagerV1>(name, bind, new ManagerListener(client));
        }
    }

    private sealed class ManagerListener(DeskClient client) : WlrixDesksManagerV1.Listener
    {
        protected override void Desk(WlrixDesksManagerV1 sender, NewId<WlrixDeskV1, WlrixDeskV1.Listener> desk)
        {
            var proxy = desk.GetAndConsume(new DeskListener(client));
            client._desks[proxy] = new DeskState();
            client._order.Add(proxy);
        }

        // Windows come with the manager whether they are wanted or not; this panel has no use for
        // them, so each is taken and left to report into nothing.
        protected override void Toplevel(
            WlrixDesksManagerV1 sender, NewId<WlrixToplevelV1, WlrixToplevelV1.Listener> toplevel) =>
            toplevel.GetAndConsume(new ToplevelListener());

        protected override void Done(WlrixDesksManagerV1 sender) => client.Emit();
    }

    private sealed class DeskListener(DeskClient client) : WlrixDeskV1.Listener
    {
        private DeskState? State(WlrixDeskV1 sender) => client._desks.GetValueOrDefault(sender);

        protected override void Id(WlrixDeskV1 sender, uint id)
        {
            if (State(sender) is { } desk) desk.Id = id;
        }

        protected override void Name(WlrixDeskV1 sender, string name)
        {
            if (State(sender) is { } desk) desk.Name = name;
        }

        protected override void Global(WlrixDeskV1 sender)
        {
            if (State(sender) is { } desk) desk.Global = true;
        }

        protected override void Activated(WlrixDeskV1 sender)
        {
            if (State(sender) is { } desk) desk.Active = true;
        }

        protected override void Deactivated(WlrixDeskV1 sender)
        {
            if (State(sender) is { } desk) desk.Active = false;
        }

        protected override void Background(WlrixDeskV1 sender, string image, string mode, string color)
        {
            if (State(sender) is { } desk) desk.Background = new DeskBackground(image, mode, color);
        }

        protected override void SharedBackground(WlrixDeskV1 sender)
        {
            if (State(sender) is { } desk) desk.Background = null;
        }

        protected override void Removed(WlrixDeskV1 sender)
        {
            client._desks.Remove(sender);
            client._order.Remove(sender);
        }
    }

    private sealed class ToplevelListener : WlrixToplevelV1.Listener;
}
