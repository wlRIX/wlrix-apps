// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using NWayland;
using NWayland.Protocols.ExtForeignToplevelListV1;
using NWayland.Protocols.Wayland;
using Wlrix.IconCatalog.Models;

namespace Wlrix.IconCatalog.Services;

/// <summary>
/// Which applications are running, and so which magic carpets stand open.
/// </summary>
/// <remarks>
/// Two sources, as <c>wlrix-desktop</c>'s <c>running.rs</c> describes, because neither alone is
/// right:
///
/// <list type="bullet">
/// <item><b>Windows</b>, from <c>ext-foreign-toplevel-list-v1</c>. The honest answer: it counts
/// what is on screen however it was started, and notices an application quitting. But it says
/// nothing in the second or two between a double-click and the first window mapping.</item>
/// <item><b>Processes the catalog launched</b>, which cover exactly that gap, until they exit.
/// Not enough alone: many <c>Exec</c> lines run a wrapper that starts the real program and
/// leaves at once.</item>
/// </list>
///
/// <para>
/// The Wayland half follows Wlrix.Desks' <c>WaylandDeskFeed</c>: a dedicated thread owns a
/// connection of its own, apart from Avalonia's, and polls its event queue. Without the global
/// -- another compositor, or none -- every carpet simply stays closed, and the connection is
/// retried in case the compositor comes back.
/// </para>
/// </remarks>
public sealed class RunningApps : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    // Windows come and go at human speed; a fifth of a second is well inside what anyone would
    // notice a carpet lag by, and keeps an idle catalog nearly idle.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, int> _launched = new(StringComparer.Ordinal);
    private Thread? _thread;

    // Only touched on the Wayland thread.
    private readonly Dictionary<ExtForeignToplevelHandleV1, string> _handles = [];
    private ExtForeignToplevelListV1? _list;

    private volatile IReadOnlySet<string> _open = new HashSet<string>();

    /// <summary>
    /// Raised when the set of running applications may have changed. Arrives on a background
    /// thread; the subscriber marshals it to the UI.
    /// </summary>
    public event Action? Changed;

    /// <summary>The <c>app_id</c>s of every open window, lowercased.</summary>
    public IReadOnlySet<string> OpenAppIds => _open;

    public void Start() => _thread ??= StartThread();

    /// <summary>Whether an icon's application has a window open, or a process the catalog started.</summary>
    public bool IsRunning(CatalogIcon icon) =>
        icon.Entry is not null
        && (RunningIdentities.AnyOpen(icon.Identities, _open) || _launched.ContainsKey(icon.Key));

    /// <summary>Counts <paramref name="process"/> as <paramref name="key"/> running until it exits.</summary>
    public void TrackLaunch(string key, Process process)
    {
        _launched.AddOrUpdate(key, 1, (_, count) => count + 1);
        Changed?.Invoke();

        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                Release(key);
                process.Dispose();
            };
            if (!process.HasExited)
                return;
        }
        catch (InvalidOperationException)
        {
            // Already gone before anyone could listen.
        }

        Release(key);
        process.Dispose();
    }

    private void Release(string key)
    {
        // Tolerates being called twice for one process: Exited can race the HasExited check.
        while (_launched.TryGetValue(key, out var count))
        {
            var removed = count <= 1
                ? _launched.TryRemove(new KeyValuePair<string, int>(key, count))
                : _launched.TryUpdate(key, count - 1, count);
            if (removed)
                break;
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (!_cts.IsCancellationRequested)
            _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(1));
        _cts.Dispose();
    }

    private Thread StartThread()
    {
        var thread = new Thread(() => Run(_cts.Token))
        {
            Name = "wlrix-icon-catalog-toplevels",
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
                // No compositor, or one without the global. Carpets lie flat until it is back.
            }

            Reset();
            if (!Sleep(RetryDelay, ct))
                return;
        }
    }

    private void RunConnection(CancellationToken ct)
    {
        using var display = WlDisplay.Connect(null);
        var queue = display.CreateEventQueue();

        using var registry = display.GetRegistry(new RegistryListener(this), queue);
        // The first roundtrip surfaces the globals, so the list can be bound; the second
        // delivers the toplevels that already exist.
        queue.Roundtrip();
        if (_list is null)
            throw new InvalidOperationException("compositor does not offer ext-foreign-toplevel-list-v1");
        queue.Roundtrip();
        Publish();

        while (!ct.IsCancellationRequested)
        {
            display.Flush();
            queue.Roundtrip();
            if (!Sleep(PollInterval, ct))
                return;
        }
    }

    private void Reset()
    {
        _list = null;
        _handles.Clear();
        Publish();
    }

    // Called on every handle's `done` and `closed`: the set is rebuilt, and only a real change
    // reaches the UI.
    private void Publish()
    {
        var open = new HashSet<string>(_handles.Values.Where(id => id.Length > 0), StringComparer.Ordinal);
        if (open.SetEquals(_open))
            return;
        _open = open;
        Changed?.Invoke();
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

    private sealed class RegistryListener(RunningApps owner) : WlRegistry.Listener
    {
        protected override void Global(WlRegistry sender, uint name, string @interface, uint version)
        {
            if (@interface != ExtForeignToplevelListV1.ProxyType.Interface.Name)
                return;
            var bind = Math.Min(version, (uint)ExtForeignToplevelListV1.ProxyType.Interface.Version);
            owner._list = sender.Bind<ExtForeignToplevelListV1>(name, bind, new ListListener(owner));
        }
    }

    private sealed class ListListener(RunningApps owner) : ExtForeignToplevelListV1.Listener
    {
        protected override void Toplevel(
            ExtForeignToplevelListV1 sender,
            NewId<ExtForeignToplevelHandleV1, ExtForeignToplevelHandleV1.Listener> toplevel)
        {
            var handle = toplevel.GetAndConsume(new HandleListener(owner));
            owner._handles[handle] = string.Empty;
        }

        protected override void Finished(ExtForeignToplevelListV1 sender)
        {
            owner._handles.Clear();
            owner.Publish();
        }
    }

    private sealed class HandleListener(RunningApps owner) : ExtForeignToplevelHandleV1.Listener
    {
        protected override void AppId(ExtForeignToplevelHandleV1 sender, string appId)
        {
            if (owner._handles.ContainsKey(sender))
                owner._handles[sender] = appId.Trim().ToLowerInvariant();
        }

        protected override void Done(ExtForeignToplevelHandleV1 sender) => owner.Publish();

        protected override void Closed(ExtForeignToplevelHandleV1 sender)
        {
            owner._handles.Remove(sender);
            sender.Dispose();
            owner.Publish();
        }
    }
}
