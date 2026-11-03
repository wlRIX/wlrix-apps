using Avalonia;
using ReactiveUI.Avalonia;
using Wlrix.Common.Instance;

namespace Wlrix.Iconbox;

sealed class Program
{
    /// <summary>
    /// The application's identity: the Wayland app id, which the compositor frames, places and
    /// leaves out of the session by, the bus name the instance guard owns, and the id the
    /// iconbox leaves out of its own list.
    /// </summary>
    internal const string AppId = "com.wlrix.iconbox";

    /// <summary>The instance guard this process holds, or null when it never took one.</summary>
    internal static DBusInstanceGuard? Guard { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // One iconbox. The compositor starts it, but somebody can also run it by hand; a second
        // would only list the same windows twice, so it leaves before making a window.
        var guard = new DBusInstanceGuard(AppId);
        if (!guard.TryAcquireAsync().GetAwaiter().GetResult())
        {
            guard.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return 0;
        }

        Guard = guard;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseWayland()
            .With(new WaylandPlatformOptions { AppId = AppId })
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace()
            .UseReactiveUI();
}
