using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Wlrix.Common.Localization;
using Wlrix.Desks.Localization;
using Wlrix.Desks.Models;
using Wlrix.Desks.Services;
using Wlrix.Desks.ViewModels;
using Wlrix.Desks.Views;
using Wlrix.Theme;

namespace Wlrix.Desks;

public partial class App : Application
{
    public override void Initialize()
    {
        // Before any XAML is loaded: {loc:Tr} in the window resolves against this, and a
        // catalog set afterwards would leave every menu showing its own key.
        TrExtension.Catalog = Strings.Catalog;

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Draw in the session's color scheme, and follow it when it changes. Not awaited:
        // it reaches the settings daemon over the bus, and a window's first paint does not
        // wait on a color. Nothing here fails if there is no settings service.
        _ = SessionScheme.FollowAsync(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Null when the visual designer got here without running Main; the defaults are
            // what it wants anyway.
            var options = Program.Options ?? new DesksOptions();
            var settings = new DesksSettingsStore(warn: Console.Error.WriteLine);
            var saved = settings.Load();

            // The flags amend what was remembered, and only ever downward: every one of them is
            // a --noX, and there is no --showSnapShots to turn anything back on. Whatever this
            // run ends up showing is what gets written back on close, so a flag is how you ask
            // for something rather than a separate kind of state.
            var initial = saved with
            {
                ShowSnapshots = saved.ShowSnapshots && !options.HideSnapshots,
                ShowGlobalDesk = saved.ShowGlobalDesk && !options.NoGlobalDesk,
                WindowLabel = options.NoWindowName ? WindowLabel.AppId : saved.WindowLabel,
            };

            // The live feed talks the wlrix-desks Wayland protocol; `--demo` runs the
            // fabricated sample layout instead, so the UI works without a compositor.
            IDeskFeed feed = options.Demo ? new SampleDeskFeed() : new WaylandDeskFeed();
            var model = new MainWindowViewModel(feed, initial);

            var window = new MainWindow
            {
                DataContext = model,
                Width = initial.WindowWidth,
                Height = initial.WindowHeight,
                // Only the process holding the bus name writes the settings, so a throwaway
                // `--new` overview cannot overwrite what the real one remembers. Null is how
                // the window is told not to save.
                Settings = Program.IsPrimary ? settings : null,
            };

            desktop.MainWindow = window;

            if (Program.Guard is { } guard)
            {
                guard.ActivateRequested += () => Dispatcher.UIThread.Post(() => Present(window, model));
                desktop.ShutdownRequested += (_, _) =>
                    guard.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Brings the overview forward for a second launch that asked for it.</summary>
    /// <remarks>
    /// Restore first, raise second. Activation reaches the compositor as
    /// <c>xdg_activation_v1</c>, whose handler raises the window's surface and stops there — it
    /// does not take a minimized window out of the icon grid. The two calls travel different
    /// connections and so race, but both are idempotent and both end in "visible and on top",
    /// so the order is a preference rather than a requirement.
    ///
    /// <para>
    /// No <c>Show()</c>: the window is already shown, the overview has nothing that hides it,
    /// and showing a closed one throws. This arrives posted to the UI thread from a bus thread,
    /// so it can land after the window has gone — where <c>Activate</c> is a no-op and the
    /// restore finds no snapshot to act on.
    /// </para>
    /// </remarks>
    private static void Present(Window window, MainWindowViewModel model)
    {
        _ = model.RestoreOwnWindowIfMinimizedAsync();
        window.Activate();
    }
}
