using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using ReactiveUI.Avalonia;

namespace Wlrix.Pinentry;

sealed class Program
{
    /// <summary>What answers when there is no display to put a window on: ssh, a VT.</summary>
    private static readonly string[] TerminalPinentries = ["/usr/bin/pinentry-curses", "/usr/bin/pinentry-tty"];

    [STAThread]
    public static int Main(string[] args)
    {
        // gpg-agent passes WAYLAND_DISPLAY on from whoever asked. Without one the request came from
        // somewhere a window cannot reach, so hand the whole conversation -- stdin and stdout
        // inherited, arguments passed through -- to a terminal pinentry, the way the distribution's
        // /usr/bin/pinentry chooses a backend.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            return RunTerminalPinentry(args);

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    private static int RunTerminalPinentry(string[] args)
    {
        foreach (var program in TerminalPinentries.Where(File.Exists))
        {
            var info = new ProcessStartInfo(program) { UseShellExecute = false };
            foreach (var arg in args)
                info.ArgumentList.Add(arg);
            using var process = Process.Start(info);
            if (process is null)
                continue;
            process.WaitForExit();
            return process.ExitCode;
        }

        Console.Error.WriteLine("wlrix-pinentry: no WAYLAND_DISPLAY, and no pinentry-curses or pinentry-tty to fall back to");
        return 1;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseWayland()
            .With(new WaylandPlatformOptions { AppId = "com.wlrix.pinentry" })
            .UseReactiveUI();
}
