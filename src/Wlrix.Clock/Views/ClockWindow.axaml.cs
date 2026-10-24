// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Wlrix.Clock.Controls;
using Wlrix.Clock.Localization;

namespace Wlrix.Clock.Views;

public partial class ClockWindow : Window
{
    /// <summary>
    /// How long past the turn of a second the face is redrawn. A timer due exactly on the
    /// second can fire a hair early, read the old second, and leave the hand where it was for
    /// another whole tick.
    /// </summary>
    private static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(15);

    private readonly ClockFace _face;
    private readonly DispatcherTimer _timer;

    /// <summary>
    /// The tooltip's content, kept and rewritten each second rather than replaced, so an open
    /// tooltip keeps counting instead of freezing at the moment it appeared.
    /// </summary>
    private readonly TextBlock _tip = new();

    /// <summary>Watches <c>/etc/localtime</c>; see <see cref="WatchTimeZone"/>.</summary>
    private FileSystemWatcher? _zoneWatcher;

    public ClockWindow()
    {
        InitializeComponent();
        Title = Strings.WindowTitle;

        _face = this.FindControl<ClockFace>("Face")!;
        ToolTip.SetTip(_face, _tip);

        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += (_, _) => Tick();

        // Avalonia closes a tooltip when the pointer leaves, not when the window loses focus,
        // so one shown before an Alt+Tab would stay over whatever came forward.
        Deactivated += (_, _) => ToolTip.SetIsOpen(_face, false);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WatchTimeZone();
        Tick();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _timer.Stop();
        _zoneWatcher?.Dispose();
    }

    /// <summary>
    /// Follow a change of the machine's time zone while running.
    /// </summary>
    /// <remarks>
    /// .NET reads <c>/etc/localtime</c> once and keeps the zone for the life of the process, so a
    /// clock left running across a change -- the International panel's Set System, or
    /// <c>timedatectl set-timezone</c> -- would go on showing the old zone's time until the next
    /// login. timedated replaces the link with a rename, which is what this watches for; the
    /// directory is watched rather than the file, because the file is replaced, not written.
    /// </remarks>
    private void WatchTimeZone()
    {
        try
        {
            _zoneWatcher = new FileSystemWatcher("/etc", "localtime")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            };
            FileSystemEventHandler changed = (_, _) => Dispatcher.UIThread.Post(() =>
            {
                TimeZoneInfo.ClearCachedData();
                Tick();
            });
            _zoneWatcher.Changed += changed;
            _zoneWatcher.Created += changed;
            _zoneWatcher.Renamed += (sender, e) => changed(sender, e);
            _zoneWatcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // No /etc to watch: the clock still runs, it just keeps the zone it started in.
            _zoneWatcher = null;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Show the current time, and come back just after the next second starts.</summary>
    private void Tick()
    {
        var now = DateTimeOffset.Now;
        _face.Time = now.TimeOfDay;
        _tip.Text = ClockText.Format(now);

        // Rescheduled every tick rather than left on a one-second interval: a fixed interval
        // drifts against the wall clock, and the hand would step a visible fraction of a second
        // after the time actually changed -- or, drifting the other way, skip a second outright.
        _timer.Stop();
        _timer.Interval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond - now.Ticks % TimeSpan.TicksPerSecond) + Slack;
        _timer.Start();
    }

    /// <summary>
    /// A left press anywhere on the face starts a move, the way a titlebar would on another
    /// window. The tooltip is closed first: it would otherwise hang where the window was.
    /// </summary>
    private void OnFacePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        ToolTip.SetIsOpen(_face, false);
        BeginMoveDrag(e);
        e.Handled = true;
    }
}
