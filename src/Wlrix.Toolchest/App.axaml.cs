using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Wlrix.Common;
using Wlrix.Toolchest.Applications;
using Wlrix.Toolchest.Services;
using Wlrix.Toolchest.ViewModels;
using Wlrix.Toolchest.Views;

namespace Wlrix.Toolchest;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void OnFrameworkInitializationCompleted()
    {
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
