using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Wlrix.Common;
using Wlrix.Common.Localization;
using Wlrix.Theme;
using Wlrix.Toolchest.Applications;
using Wlrix.Toolchest.Localization;
using Wlrix.Toolchest.Services;
using Wlrix.Toolchest.ViewModels;
using Wlrix.Toolchest.Views;

namespace Wlrix.Toolchest;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize()
    {
        // Before any XAML is loaded. This window's labels come through Strings.X rather than
        // {loc:Tr}, so nothing depends on it yet — but an unset catalog makes the first
        // {loc:Tr} anybody adds render as its own key, silently.
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
            var services = new ServiceCollection();
            services.AddZLogger("toolchest");
            services.AddSingleton<IApplicationCatalog, ApplicationCatalog>();
            services.AddSingleton<IAppLauncher, AppLauncher>();
            services.AddSingleton<ISessionService, SessionService>();
            services.AddTransient<MainWindowViewModel>();
            _services = services.BuildServiceProvider();

            desktop.MainWindow = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainWindowViewModel>()
            };

            // Dispose the provider on shutdown so ZLogger flushes its buffered output.
            desktop.ShutdownRequested += (_, _) => _services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
