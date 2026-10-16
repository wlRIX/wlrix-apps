using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using ReactiveUI.Avalonia;

namespace Wlrix.Pinentry;

sealed class Program
{
    /// <summary>What answers when there is no display to put a window on: ssh, a VT.</summary>
    private static readonly string[] TerminalPinentries = ["/usr/bin/pinentry-curses", "/usr/bin/pinentry-tty"];

    /// <summary>
    /// What answers in another desktop's session: the distribution's own chooser, which picks the
    /// Qt, GNOME or GTK pinentry by <c>XDG_CURRENT_DESKTOP</c> exactly as it would without us.
    /// </summary>
    private static readonly string[] DesktopPinentries = ["/usr/bin/pinentry", .. TerminalPinentries];

    [STAThread]
    public static int Main(string[] args)
    {
        // gpg-agent passes WAYLAND_DISPLAY on from whoever asked. Without one the request came from
        // somewhere a window cannot reach, so hand the whole conversation -- stdin and stdout
        // inherited, arguments passed through -- to a terminal pinentry, the way the distribution's
        // /usr/bin/pinentry chooses a backend.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            return RunOther(TerminalPinentries, args);

        // The wlRIX install makes this the system-wide default (/etc/gnupg/gpg-agent.conf), which
        // every desktop on the machine reads. In anyone else's session, step aside.
        if (!IsWlrixSession(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")))
            return RunOther(DesktopPinentries, args);

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>
    /// Whether <c>XDG_CURRENT_DESKTOP</c> (a colon-separated list, by the spec) names wlRIX, as
    /// <c>start-wlrix</c> sets it.
    /// </summary>
    internal static bool IsWlrixSession(string? currentDesktop) =>
        currentDesktop?.Split(':').Contains("wlRIX", StringComparer.OrdinalIgnoreCase) == true;

    private static int RunOther(string[] candidates, string[] args)
    {
        foreach (var program in candidates.Where(IsSomeoneElse))
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

        Console.Error.WriteLine($"wlrix-pinentry: none of {string.Join(", ", candidates)} is installed to hand off to");
        return 1;
    }

    /// <summary>
    /// Whether <paramref name="program"/> exists and is not this program again. Debian's
    /// /usr/bin/pinentry is an alternatives link, and if somebody pointed it here, handing off to it
    /// would start this pinentry over and over.
    /// </summary>
    private static bool IsSomeoneElse(string program)
    {
        var file = new FileInfo(program);
        if (!file.Exists)
            return false;
        var target = file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file.FullName;
        return !Path.GetFileName(target).StartsWith("wlrix-pinentry", StringComparison.Ordinal);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseWayland()
            .With(new WaylandPlatformOptions { AppId = "com.wlrix.pinentry" })
            .UseReactiveUI();
}
