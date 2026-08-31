using Avalonia;
using ReactiveUI.Avalonia;

namespace Wlrix.Shutdown;

sealed class Program
{
    /// <summary>
    /// Whether the window should open with Restart already checked.
    ///
    /// The Toolchest has two menu items and this app is one window, so which of them was chosen
    /// arrives as a flag. It only sets the checkbox: the choice is still the user's to change
    /// before OK, which is the whole point of showing them a dialog rather than acting on the
    /// menu item.
    /// </summary>
    internal static bool StartWithRestart { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        StartWithRestart = WantsRestart(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Whether <paramref name="args"/> asked for the restart form of the dialog.</summary>
    internal static bool WantsRestart(string[] args) => args.Contains("--restart", StringComparer.Ordinal);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseWayland()
            .With(new WaylandPlatformOptions { AppId = "com.wlrix.shutdown" })
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace()
            .UseReactiveUI();
}
