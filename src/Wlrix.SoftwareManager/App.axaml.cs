using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Wlrix.Common;
using Wlrix.Common.Localization;
using Wlrix.Packages;
using Wlrix.Packages.Privileged;
using Wlrix.Packages.Processes;
using Wlrix.Packages.UserSoftware;
using Wlrix.SoftwareManager.Localization;
using Wlrix.SoftwareManager.Services;
using Wlrix.SoftwareManager.ViewModels;
using Wlrix.SoftwareManager.Views;

namespace Wlrix.SoftwareManager;

public partial class App : Application
{
    private ServiceProvider? _services;

    /// <summary>
    /// The container, for the windows a view model asks the view to open. A dialog's view model
    /// has its own dependencies, and the alternative -- threading every one of them through the
    /// main window's view model so it can hand them on -- makes it the registry it is not.
    /// </summary>
    internal static IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        // Before any XAML is loaded: {loc:Tr} in the window resolves against this, and a
        // catalog set afterwards would leave every static label showing its own key.
        TrExtension.Catalog = Strings.Catalog;

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection();
            services.AddZLogger("software-manager");
            services.AddSingleton<IProcessRunner, ProcessRunner>();
            services.AddSingleton<IPackageBackendFactory, PackageBackendFactory>();
            services.AddSingleton<IPrivilegedRunner, PkexecRunner>();
            services.AddSingleton<ITransactionService, TransactionService>();
            services.AddSingleton<IPaneLayoutStore, PaneLayoutStore>();
            services.AddSingleton<IDiskSpaceProbe, DiskSpaceProbe>();
            services.AddTransient<MainWindowViewModel>();
            services.AddTransient<RepositoriesViewModel>();
            services.AddSingleton<IUserSoftwareSource, AppImageSource>();
            services.AddTransient<UserSoftwareViewModel>();
            Services = _services = services.BuildServiceProvider();

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
