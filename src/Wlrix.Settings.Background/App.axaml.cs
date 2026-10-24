using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Wlrix.Common.Localization;
using Wlrix.Settings.Background.Localization;
using Wlrix.Settings.Background.ViewModels;
using Wlrix.Settings.Background.Views;
using Wlrix.Theme;

namespace Wlrix.Settings.Background;

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
        _ = SessionScheme.FollowAsync(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var model = new BackgroundViewModel();
            desktop.MainWindow = new MainWindow { DataContext = model };

            // The window is shown first and filled in when the daemon answers, as in the other
            // settings panels: bus activation means the first call may have to start the daemon.
            _ = model.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
