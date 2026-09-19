using Avalonia;
using ReactiveUI.Avalonia;
using Wlrix.Desks.Services;

namespace Wlrix.Desks;

sealed class Program
{
    /// <summary>
    /// The application's identity, and the identity of the one running instance.
    /// </summary>
    /// <remarks>
    /// One constant because three copies of a string that must agree is three chances to
    /// disagree: this is the Wayland app id the compositor sees, the bus name the guard owns,
    /// and what the overview matches its own window against in a desk snapshot.
    /// </remarks>
    internal const string AppId = "com.wlrix.desks";

    /// <summary>
    /// The parsed command line, or null when nothing parsed one.
    /// </summary>
    /// <remarks>
    /// A static rather than <c>desktop.Args</c>, because the guard has to decide whether this
    /// process is going to exist at all before Avalonia starts. Null is a real case: the visual
    /// designer calls <see cref="BuildAvaloniaApp"/> without ever running <see cref="Main"/>,
    /// and the defaults are the right answer for it.
    /// </remarks>
    internal static DesksOptions? Options { get; private set; }

    /// <summary>The instance guard this process holds, or null when it never took one.</summary>
    internal static IInstanceGuard? Guard { get; private set; }

    /// <summary>Whether this process owns the bus name, and so whether it writes the settings.</summary>
    internal static bool IsPrimary { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        var expanded = DesksCommandLine.ExpandAliases(args);
        if (!DesksCommandLine.TryParse(expanded, out var options, out var exitCode))
            return exitCode;

        Options = options;

        // Before Avalonia, deliberately. A second invocation hands the overview that already
        // exists a request to come forward and leaves without ever creating a window, so there
        // is no flash of a frame that is about to close.
        //
        // `--new` still asks for the name, and only declines to bow out when somebody else
        // already has it. Skipping the request altogether would leave the name unowned
        // whenever a `--new` invocation happened to run first, and every ordinary launch after
        // that would open yet another window: runonce silently off for the rest of the session.
        var guard = new DBusInstanceGuard();
        IsPrimary = guard.TryAcquireAsync().GetAwaiter().GetResult();
        if (!IsPrimary && !options.New)
        {
            guard.SendActivateAsync().GetAwaiter().GetResult();
            guard.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return 0;
        }

        Guard = guard;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(expanded);
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
