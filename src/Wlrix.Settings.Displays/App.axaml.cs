using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Wlrix.Common.Localization;
using Wlrix.Settings.Displays.Localization;
using Wlrix.Settings.Displays.Services;
using Wlrix.Settings.Displays.ViewModels;
using Wlrix.Settings.Displays.Views;
using Wlrix.Theme;

namespace Wlrix.Settings.Displays;

public partial class App : Application
{
    public override void Initialize()
    {
        // Before any XAML is loaded: {loc:Tr} in the window resolves against this, and a
        // catalog set afterwards would leave every static label showing its own key.
        TrExtension.Catalog = Strings.Catalog;

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Draw in the session's color scheme, and follow it when it changes. Not awaited: a
        // window's first paint does not wait on a color.
        _ = SessionScheme.FollowAsync(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // --demo shows made-up displays, for working on the panel somewhere that has no
            // wlr-output-management -- or only one monitor.
            IOutputFeed feed = desktop.Args?.Contains("--demo") == true
                ? new SampleOutputFeed()
                : new WaylandOutputFeed();
            var model = new MainWindowViewModel(feed);
            desktop.MainWindow = new MainWindow { DataContext = model };

            // Shown first and filled in when the compositor answers.
            model.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
