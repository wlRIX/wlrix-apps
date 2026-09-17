using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Avalonia.Threading;
using Wlrix.Common;
using Wlrix.Common.Desktop;
using Wlrix.Common.Localization;
using Wlrix.Files.Core;
using Wlrix.Files.Core.Dnd;
using Wlrix.Files.Core.Icons;
using Wlrix.Files.Core.Mime;
using Wlrix.Files.Core.Operations;
using Wlrix.Files.Core.Platform;
using Wlrix.Files.Core.Remote;
using Wlrix.Files.Core.Thumbnails;
using Wlrix.Files.Core.State;
using Wlrix.Files.Services;
using Wlrix.Files.Localization;
using Wlrix.Files.ViewModels;
using Wlrix.Files.Views;
using Wlrix.Theme;
using Location = Wlrix.Files.Core.Location;
using ZLogger;

namespace Wlrix.Files;

public class App : Application
{
    private ServiceProvider? _services;
    private ConfigReload? _reload;

    /// <summary>
    /// The bus name this process holds, handed over by <c>Program</c>.
    /// </summary>
    /// <remarks>
    /// A static because it is acquired before Avalonia exists — which is the point of it: a
    /// second invocation must be able to bow out without ever building an application.
    /// </remarks>
    public static IInstanceGuard? Guard { get; set; }

    /// <summary>
    /// Whether to skip restoring the previous session and just open a window.
    /// </summary>
    /// <remarks>
    /// Set by the New Window action. Somebody asking for one window should get one, not the
    /// five they had open yesterday.
    /// </remarks>
    public static bool SuppressRestore { get; set; }

    /// <summary>
    /// The window a dialog with no obvious owner should hang from.
    /// </summary>
    /// <remarks>
    /// A password prompt is raised by a mount, and a mount has no window: it is created by
    /// whichever navigation reached the server first, on a worker thread. The active window is
    /// the best answer available, and null when the last one has closed.
    /// </remarks>
    private static Window? ActiveWindow() =>
        (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
        .FirstOrDefault(window => window.IsActive)
        ?? (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
        .FirstOrDefault();

    public override void Initialize()
    {
        // Before the XAML loads, and that ordering is not optional: every {loc:Tr} is
        // resolved during the load, and a catalog set afterwards leaves each one rendering
        // as its own resource key -- with no error and no warning.
        TrExtension.Catalog = Strings.Catalog;
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Fire and forget: follows the session color scheme, and fails soft if the settings
        // daemon is not running.
        _ = SessionScheme.FollowAsync(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection();
            services.AddZLogger("files");
            // The credential store, and the honest one. A Secret Service collection needs a
            // prompter to unlock and wlRIX ships none, so nothing here pretends a password
            // will survive a restart -- the connect dialog says so instead.
            services.AddSingleton<ICredentialStore>(_ => new TransientCredentialStore());
            services.AddSingleton<ICredentialPrompt>(_ => new DialogCredentialPrompt(ActiveWindow));
            services.AddSingleton(sp => new FileSystemProvider(
                RemoteFileSystemFactory.Schemes.Select(scheme => new RemoteFileSystemFactory(
                    scheme,
                    sp.GetRequiredService<ICredentialStore>(),
                    sp.GetRequiredService<ICredentialPrompt>()))));
            services.AddSingleton(_ => XdgUserDirs.Read());
            services.AddSingleton(_ => MountTable.Read());
            // Loaded once and shared. Parsing the mime database is a couple of milliseconds
            // and walking an icon theme is cached, so the cost is paid at startup rather
            // than per directory.
            services.AddSingleton(_ => SharedMimeDatabase.Load());
            // files.toml, read here rather than over the settings daemon. Every wlRIX
            // component reads its own file and the daemon is the only writer, so a session
            // with no daemon running is an ordinary session rather than a broken one.
            services.AddSingleton<FilesSettings>();
            services.AddSingleton(sp => sp.GetRequiredService<FilesSettings>().Current);
            services.AddSingleton(sp => new XdgIconTheme
            {
                Theme = sp.GetRequiredService<FilesSettings>().Current.IconTheme
            });
            services.AddSingleton(sp => new IconLoader(sp.GetRequiredService<XdgIconTheme>()));
            services.AddSingleton<IconService>();
            // The shared freedesktop cache, not a private one: a thumbnail made here is one
            // the rest of the desktop does not have to make again.
            services.AddSingleton(_ => new ThumbnailCache());
            // Load rather than Default: the installed .thumbnailer programs are what give
            // video, audio cover art, HEIF and PDF previews, and reading them touches the
            // disk, which is why it is not a static initializer.
            services.AddSingleton(sp => new ThumbnailProvider(
                sp.GetRequiredService<ThumbnailCache>(),
                thumbnailers: CompositeThumbnailer.Load(),
                mounts: sp.GetRequiredService<MountTable>()));
            services.AddSingleton<ThumbnailService>();
            // Open With. The entry catalog is scanned once; the associations behind it are
            // re-read per menu, because another application can change them while this runs.
            services.AddSingleton(sp => new ApplicationHandlers(
                new DesktopEntryScanner().Scan(),
                subclasses: SharedMime.From(sp.GetRequiredService<SharedMimeDatabase>())));
            services.AddSingleton<ApplicationLauncher>();
            // Application-scoped, both of them: an operation outlives the window that
            // started it, and a cut in one window pastes in another.
            services.AddSingleton<IFileSystemProvider>(sp => sp.GetRequiredService<FileSystemProvider>());
            services.AddSingleton(sp => new OperationQueue(
                sp.GetRequiredService<IFileSystemProvider>(), sp.GetRequiredService<MountTable>()));
            services.AddSingleton<FileClipboard>();
            // Also application-scoped: the files it stages have to outlive the drag that
            // made them, because the application receiving the drop may still be reading.
            services.AddSingleton(sp => new DragStaging(sp.GetRequiredService<IFileSystemProvider>()));
            services.AddSingleton(sp => new FilesStateStore(
                warn: message => sp.GetRequiredService<ILoggerFactory>()
                    .CreateLogger<FilesStateStore>().ZLogWarning($"{message}")));
            // The manager is the application's, the windows are not: Classic mode opens one
            // per directory and each gets its own view model.
            // The devices rail. Started once, shared by every window, and never allowed to
            // fail a launch: a machine with no udisks2 gets the disks the mount table knows
            // about and no way to mount anything, which is what the previous version had.
            services.AddSingleton<IStorageDeviceMonitor, UDisks2Devices>();

            services.AddSingleton<WindowManager>();
            services.AddSingleton<IWindowRouting<MainWindowViewModel>>(
                sp => sp.GetRequiredService<WindowManager>());
            services.AddTransient<MainWindowViewModel>();
            _services = services.BuildServiceProvider();

            var windows = _services.GetRequiredService<WindowManager>();
            windows.Start();
            windows.AllWindowsClosed += () => desktop.Shutdown();

            // Every argument is a directory to open, which is what the desktop entry's %U
            // delivers: the first opens the window and the rest become tabs beside it, so
            // selecting several folders and pressing Return opens all of them.
            MainWindowViewModel? first = null;
            foreach (var argument in desktop.Args ?? [])
            {
                if (!Location.TryParse(argument, out var location))
                    continue;
                if (first is null)
                {
                    first = windows.Create(location);
                    windows.Router.Track(first, location);
                    continue;
                }

                // Each further argument is another thing the user asked to see, so the mode
                // decides what that is. Named here rather than left to the default intent,
                // because the default in Modern is "navigate in place" and three arguments
                // would then show only the third.
                windows.Router.Open(location, windows.Router.Mode == NavigationMode.Classic
                    ? OpenIntent.NewWindow
                    : OpenIntent.NewTab, first);
            }

            // Nothing named on the command line means "carry on where I left off". A launch
            // that *does* name a directory is a specific request and is not diluted by
            // reopening five other windows around it.
            if (first is null && (SuppressRestore || !windows.Restore()))
            {
                first = windows.Create(_services.GetRequiredService<XdgUserDirs>().Home);
                windows.Router.Track(first, first.Pane.Location);
            }

            // A second invocation reaches us here rather than starting its own process. The
            // request arrives on a bus thread, so it is posted rather than acted on.
            if (Guard is { } guard)
            {
                guard.OpenRequested += uris => Dispatcher.UIThread.Post(() => windows.OpenAll(uris));
                guard.ActionRequested += action => Dispatcher.UIThread.Post(() =>
                {
                    if (action == Program.NewWindowAction)
                        windows.OpenNewWindow();
                });
                desktop.ShutdownRequested += (_, _) =>
                    guard.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }

            // Not desktop.MainWindow: that window closing would take the application with it,
            // and in Classic mode the first window is no more important than the others.
            desktop.ShutdownMode = global::Avalonia.Controls.ShutdownMode.OnExplicitShutdown;

            // The pidfile and the SIGHUP that make files.toml reloadable. Only the instance
            // holding the bus name gets here — a second invocation handed its arguments over
            // and exited before Avalonia started — so there is exactly one pidfile and it
            // names the process every window belongs to.
            var settings = _services.GetRequiredService<FilesSettings>();
            _reload = ConfigReload.Start(() => Dispatcher.UIThread.Post(() =>
            {
                var before = settings.Current;
                settings.Reload();
                if (settings.Current == before)
                    return;

                // The icon theme is a live swap: the theme object clears its caches and every
                // listing re-asks. The navigation mode needs no more than the menus redrawing.
                _services.GetRequiredService<XdgIconTheme>().Theme = settings.Current.IconTheme;
                _services.GetRequiredService<IconService>().Clear();
                _services.GetRequiredService<WindowManager>().ConfigurationChanged();
            }));

            // Not awaited: reading the disks is a round trip to a daemon on the system bus,
            // and a window should be up before that answers. The rail fills itself in when it
            // does, and stays empty if there is nothing to say.
            _ = _services.GetRequiredService<IStorageDeviceMonitor>().StartAsync(CancellationToken.None);
            // Disposing the provider flushes ZLogger; without it the last lines of a session
            // never reach the file.
            desktop.ShutdownRequested += (_, _) =>
            {
                // Cancels anything still running before the provider it depends on goes.
                _services.GetRequiredService<OperationQueue>().DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(6));
                // Unconditionally, whatever the debounce was in the middle of.
                _services.GetRequiredService<WindowManager>().SaveSession();
                // The last chance to write what is still only in memory.
                _services.GetRequiredService<FilesStateStore>().Flush();
                _reload?.Dispose();
                _services.GetRequiredService<DragStaging>().Dispose();
                _services.GetRequiredService<ThumbnailService>().Dispose();
                _services.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
