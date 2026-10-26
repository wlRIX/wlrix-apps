using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using Wlrix.Files.Core;
using Wlrix.Files.Core.Operations;
using Wlrix.Files.ViewModels;
using Wlrix.Files.Views;

namespace Wlrix.Files.Services;

/// <summary>Follows every running operation and decides when the progress window is up.</summary>
/// <remarks>
/// One for the application, like the queue it watches: an operation outlives the window that
/// started it, so its progress cannot belong to that window either.
///
/// <para>
/// The window opens by itself once an operation has run for <see cref="ShowAfter"/>, so a quick
/// paste never flashes one, and it closes by itself when the last operation ends. Closed by
/// hand while something is still running, it only hides, and stays hidden until the status
/// bar asks for it again: whoever closed it has said they do not want it in the way.
/// </para>
/// </remarks>
public sealed class OperationsMonitor : IDisposable
{
    /// <summary>How long an operation runs before the window comes up on its own.</summary>
    public static readonly TimeSpan ShowAfter = TimeSpan.FromSeconds(1);

    private readonly OperationQueue _queue;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _tick;
    private readonly Dictionary<QueuedOperation, Followed> _followed = [];
    private ProgressWindow? _window;
    private bool _dismissed;

    private sealed record Followed(OperationViewModel Model, TimeSpan Since, Action<QueuedOperation> OnChanged);

    public OperationsMonitor(OperationQueue queue)
    {
        _queue = queue;
        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick());
        queue.ActiveChanged += OnActiveChanged;
    }

    /// <summary>Every operation the window shows, oldest first.</summary>
    public ObservableCollection<OperationViewModel> Operations { get; } = [];

    /// <summary>Raised when a panel's destination link is followed.</summary>
    public event Action<Location>? OpenRequested;

    /// <summary>The window, once it has been made; for the tests, which close it by hand.</summary>
    internal ProgressWindow? CurrentWindow => _window;

    /// <summary>Whether the window is on screen.</summary>
    public bool IsShowing => _window?.IsVisible == true;

    /// <summary>Brings the window up and forward, for the status bar's progress.</summary>
    public void Show()
    {
        _dismissed = false;
        if (Operations.Count == 0)
            return;
        var window = Window();
        window.ShowActivated = true;
        window.Show();
        window.Activate();
    }

    private void OnActiveChanged() => Dispatcher.UIThread.Post(Sync);

    /// <summary>Starts following new operations and lets finished ones go.</summary>
    /// <remarks>
    /// Renames and new folders are left out: they are over before anybody could read a panel,
    /// and a window opening for one would be a flash and nothing more.
    /// </remarks>
    internal void Sync()
    {
        var active = _queue.Active
            .Where(queued => queued.Operation.Kind is not (OperationKind.Rename or OperationKind.CreateDirectory))
            .ToList();

        foreach (var gone in _followed.Keys.Except(active).ToList())
        {
            var followed = _followed[gone];
            gone.Changed -= followed.OnChanged;
            followed.Model.OpenRequested -= Relay;
            Operations.Remove(followed.Model);
            _followed.Remove(gone);
        }

        foreach (var queued in active.Where(queued => !_followed.ContainsKey(queued)))
        {
            var model = new OperationViewModel(queued);
            // Reports arrive on the thread pool, the panel is the UI thread's.
            Action<QueuedOperation> onChanged = _ => Dispatcher.UIThread.Post(model.Refresh);
            queued.Changed += onChanged;
            model.OpenRequested += Relay;
            _followed[queued] = new Followed(model, _clock.Elapsed, onChanged);
            Operations.Add(model);
        }

        if (Operations.Count > 0)
        {
            _tick.Start();
            return;
        }

        // All done. The window goes, and the next operation gets a fresh chance to show it.
        _tick.Stop();
        _dismissed = false;
        _window?.Hide();
    }

    private void Relay(Location location) => OpenRequested?.Invoke(location);

    /// <summary>Once a second: every panel's speed and time left, and whether to show the window.</summary>
    internal void Tick()
    {
        var now = _clock.Elapsed;
        foreach (var followed in _followed.Values)
            followed.Model.Tick(now);

        if (_dismissed || IsShowing)
            return;
        if (_followed.Values.Any(followed => now - followed.Since >= ShowAfter))
        {
            // Not activated: it comes up beside whatever somebody is doing, and taking the
            // keyboard from a window they are typing in would be rude.
            var window = Window();
            window.ShowActivated = false;
            window.Show();
        }
    }

    private ProgressWindow Window()
    {
        if (_window is not null)
            return _window;
        _window = new ProgressWindow { DataContext = this };
        _window.Closing += (_, e) =>
        {
            // Closed by hand mid-operation: hidden, not destroyed, and kept out of the way
            // until asked for.
            if (Operations.Count == 0)
                return;
            e.Cancel = true;
            _dismissed = true;
            _window.Hide();
        };
        return _window;
    }

    public void Dispose()
    {
        _queue.ActiveChanged -= OnActiveChanged;
        _tick.Stop();
        foreach (var (queued, followed) in _followed)
            queued.Changed -= followed.OnChanged;
        _window?.Close();
    }
}
