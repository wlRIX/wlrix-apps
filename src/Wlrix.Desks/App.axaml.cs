using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Wlrix.Common.Localization;
using Wlrix.Desks.Localization;
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
            // The live feed talks the wlrix-desks Wayland protocol; `--demo` runs the
            // fabricated sample layout instead, so the UI works without a compositor.
            var demo = desktop.Args?.Contains("--demo") == true;
            IDeskFeed feed = demo ? new SampleDeskFeed() : new WaylandDeskFeed();

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(feed)
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
