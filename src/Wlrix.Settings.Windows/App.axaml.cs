using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Wlrix.Settings.Windows.ViewModels;
using Wlrix.Settings.Windows.Views;
using Wlrix.Theme;

namespace Wlrix.Settings.Windows;

public partial class App : Application
{
    public override void Initialize()
    {
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
            var model = new WindowSettingsViewModel();
            desktop.MainWindow = new MainWindow { DataContext = model };

            // The window is shown first and filled in when the daemon answers, as in
            // Wlrix.Settings.Keyboard: asking a bus for the settings is not something to block a
            // window's first paint on, and bus activation means the very first call may also
            // have to start the daemon.
            _ = model.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
