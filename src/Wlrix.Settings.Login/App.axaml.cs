using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Wlrix.Common.Localization;
using Wlrix.Settings.Login.Localization;
using Wlrix.Settings.Login.ViewModels;
using Wlrix.Settings.Login.Views;
using Wlrix.Settings.Outputs;
using Wlrix.Theme;

namespace Wlrix.Settings.Login;

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
            // --demo shows made-up displays, as the Displays panel's does, for working on the
            // panel somewhere with one monitor or no wlr-output-management.
            IOutputFeed feed = desktop.Args?.Contains("--demo") == true
                ? new SampleOutputFeed()
                : new WaylandOutputFeed();
            var model = new LoginSettingsViewModel(feed);
            desktop.MainWindow = new MainWindow { DataContext = model };

            // Shown first and filled in from the file and the compositor.
            model.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
