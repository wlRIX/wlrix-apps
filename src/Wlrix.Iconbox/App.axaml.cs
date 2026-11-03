using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Wlrix.Common.Localization;
using Wlrix.Iconbox.Localization;
using Wlrix.Iconbox.Services;
using Wlrix.Iconbox.ViewModels;
using Wlrix.Iconbox.Views;
using Wlrix.Theme;

namespace Wlrix.Iconbox;

public partial class App : Application
{
    public override void Initialize()
    {
        // Before any XAML is loaded: {loc:Tr} in the window resolves against this.
        TrExtension.Catalog = Strings.Catalog;

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Draw in the session's color scheme, and follow it when it changes. The tiles take
        // their colors from it too, so they stay the compositor's tiles when the scheme moves.
        _ = SessionScheme.FollowAsync(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var feed = new ToplevelFeed();
            var model = new MainWindowViewModel(feed, new TileArt());
            desktop.MainWindow = new MainWindow { DataContext = model };
            // Without this the process outlives its compositor, holding the bus name, and the
            // next compositor's iconbox finds it taken and leaves. An exit rather than a
            // Shutdown: the UI thread that would carry one out is stuck on the dead display,
            // and the bus drops the name with the process.
            model.CompositorLost += () => Environment.Exit(0);
            desktop.ShutdownRequested += (_, _) =>
            {
                model.Dispose();
                Program.Guard?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            };
            feed.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
