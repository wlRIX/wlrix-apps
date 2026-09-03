using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Wlrix.Archiver.Localization;
using Wlrix.Archiver.Services;
using Wlrix.Archiver.Services.Archives;
using Wlrix.Archiver.Services.Encodings;
using Wlrix.Archiver.ViewModels;
using Wlrix.Archiver.Views;
using Wlrix.Common;
using Wlrix.Common.Localization;
using Wlrix.Theme;

namespace Wlrix.Archiver;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize()
    {
        // Before any XAML is loaded: {loc:Tr} in the window resolves against this, and a
        // catalog set afterwards would leave every static label showing its own key.
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
            services.AddZLogger("archiver");

            // One decoder shared by everything: it holds the encoding the user picked and the
            // one detection settled on, and a second instance would silently disagree with the
            // first about how to read a name.
            services.AddSingleton<FilenameDecoder>();
            services.AddSingleton<IProcessRunner, ProcessRunner>();
            services.AddSingleton<SharpCompressBackend>();
            services.AddSingleton<SevenZipCliBackend>();
            services.AddSingleton<IArchiveBackendRegistry>(provider =>
                // Order is preference: the managed backend first, so 7z only reaches the
                // external tool for the operations SharpCompress cannot do at all.
                new ArchiveBackendRegistry([
                    provider.GetRequiredService<SharpCompressBackend>(),
                    provider.GetRequiredService<SevenZipCliBackend>(),
                ]));
            services.AddSingleton<DragStaging>();
            services.AddTransient<MainWindowViewModel>();
            _services = services.BuildServiceProvider();

            var window = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainWindowViewModel>(),
            };
            window.SevenZipAvailable = _services.GetRequiredService<SevenZipCliBackend>().IsAvailable;

            // An archive named on the command line opens straight away, so the app can be a
            // handler for one from a file manager.
            if (desktop.Args is [var path, ..] && File.Exists(path))
                window.OpenOnStartup(path);

            desktop.MainWindow = window;

            // Dispose the provider on shutdown so ZLogger flushes its buffered output and the
            // drag scratch directory is cleared.
            desktop.ShutdownRequested += (_, _) => _services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
