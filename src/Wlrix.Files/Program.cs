using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Dialogs;
using Avalonia.Wayland;
using ReactiveUI.Avalonia;
using Wlrix.Files.Services;

namespace Wlrix.Files;

[SupportedOSPlatform("linux")]
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // A file manager runs a great deal of work in the background -- directory reads, and
        // before long file operations and thumbnailing. An exception escaping any of those
        // unobserved is rethrown on the finalizer thread and takes the process down, which
        // for this application would mean losing a window mid-copy.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Console.Error.WriteLine($"unobserved task exception: {e.Exception}");
            e.SetObserved();
        };

        // Before the instance guard as well as before Avalonia: this answers a question and
        // exits, so it must neither hand its arguments to a running window nor start one.
        // wlrix-settings-daemon runs it against a candidate files.toml before renaming that
        // file into place, which is what stops a settings panel writing one this program would
        // refuse.
        if (args is [CheckConfigFlag, ..])
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine($"wlrix-files: {CheckConfigFlag} needs a path");
                Environment.Exit(2);
            }

            if (Core.Config.FilesConfig.Check(args[1]) is { } problem)
            {
                Console.Error.WriteLine(problem);
                Environment.Exit(1);
            }

            return;
        }

        // An option this version does not know is a usage error, not a filename. Every other
        // wlRIX component already refuses one, and this is the reason: wlrix-settings-daemon
        // validates a candidate config by running `wlrix-files --check-config <tmp>`, and a
        // build predating that flag treated both words as paths -- it opened the temporary
        // file in a window and exited 0, which the daemon read as "this file is fine". The
        // next flag to be added should fail loudly against an old binary instead.
        //
        // %U in the desktop entry hands over URIs, never a leading dash, so nothing legitimate
        // arrives looking like this. A file genuinely named "--x" comes as file:///...--x.
        if (args.FirstOrDefault(argument =>
                argument.StartsWith("--", StringComparison.Ordinal) && argument != NewWindowFlag) is { } unknown)
        {
            Console.Error.WriteLine($"wlrix-files: unknown argument: {unknown}");
            Environment.Exit(2);
        }

        // Before Avalonia, deliberately. A second invocation hands its arguments to the
        // instance that already exists and leaves without ever creating a window, so there is
        // no flash of a frame that is about to close -- and, more to the point, the Classic
        // window registry stays an in-process dictionary with one process to ask.
        var guard = new DBusInstanceGuard();
        var newWindow = args.Contains(NewWindowFlag, StringComparer.Ordinal);
        var paths = args.Where(argument => argument != NewWindowFlag).ToArray();

        if (!guard.TryAcquireAsync().GetAwaiter().GetResult())
        {
            // The desktop entry's New Window action, arriving at an instance that already
            // exists. Sent as ActivateAction rather than Open, because "another window" is
            // not a URI and there is nothing to open.
            if (newWindow)
                guard.SendActionAsync(NewWindowAction).GetAwaiter().GetResult();
            if (paths.Length > 0 || !newWindow)
                guard.SendOpenAsync([.. paths.Select(ToUri)]).GetAwaiter().GetResult();
            guard.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return;
        }

        App.Guard = guard;
        // The flag is consumed here rather than passed on: a first instance asked for a new
        // window is just a first instance, and the flag would otherwise be parsed as a path.
        App.SuppressRestore = newWindow;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(paths);
    }

    /// <summary>The command line the desktop entry's New Window action runs.</summary>
    private const string NewWindowFlag = "--new-window";

    /// <summary>What the settings daemon calls to have a candidate config file checked.</summary>
    private const string CheckConfigFlag = "--check-config";

    /// <summary>The action name in the entry's <c>[Desktop Action NewWindow]</c> group.</summary>
    public const string NewWindowAction = "NewWindow";

    /// <summary>
    /// Turns an argument into something the other instance can parse.
    /// </summary>
    /// <remarks>
    /// <c>org.freedesktop.Application.Open</c> takes URIs, and the desktop entry's <c>%U</c>
    /// delivers them — but a person typing <c>wlrix-files .</c> at a shell does not, and a
    /// relative path means nothing at all once it has crossed to a process with a different
    /// working directory.
    /// </remarks>
    private static string ToUri(string argument) =>
        Uri.TryCreate(argument, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1
            ? uri.ToString()
            : new Uri(System.IO.Path.GetFullPath(argument)).ToString();

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseWayland()
            .With(new WaylandPlatformOptions { AppId = "com.wlrix.files" })
            // Avalonia's in-process picker, which Wlrix.Avalonia themes. Without this the
            // Wayland backend reaches for the xdg-desktop-portal FileChooser and the user
            // gets a GTK dialog in the middle of an IRIX desktop -- or, with no portal
            // frontend running, an untemplated blank window.
            .UseManagedSystemDialogs()
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace()
            .UseReactiveUI();
}
