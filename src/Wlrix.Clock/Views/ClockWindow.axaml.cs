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

    public ClockWindow()
    {
        InitializeComponent();
        Title = Strings.WindowTitle;

        _face = this.FindControl<ClockFace>("Face")!;
        ToolTip.SetTip(_face, _tip);

        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += (_, _) => Tick();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Tick();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _timer.Stop();
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
