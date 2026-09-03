using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Wlrix.Settings.Schemes.ViewModels;
using Wlrix.Settings.Schemes.Views;
using Wlrix.Theme;

namespace Wlrix.Settings.Schemes;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // This panel follows the session scheme like every other app, which is what makes Apply
        // repaint the window you pressed it in. The sample image is separate: it renders in the
        // scheme being *considered*, from its own resources, and does not wait for a write.
        _ = SessionScheme.FollowAsync(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var model = new ColorSchemeViewModel();
            desktop.MainWindow = new MainWindow { DataContext = model };

            // The window is shown first and filled in when the daemon answers, as in the other
            // settings panels: asking a bus for the settings is not something to block a
            // window's first paint on, and bus activation means the first call may have to
            // start the daemon.
            _ = model.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
