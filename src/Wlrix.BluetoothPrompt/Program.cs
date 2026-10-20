using Avalonia;
using Avalonia.Controls;
using ReactiveUI.Avalonia;

namespace Wlrix.BluetoothPrompt;

/// <summary>
/// The pairing dialog <c>wlrix-bluetooth</c> puts up: one request on stdin, one answer on
/// stdout, then exit. See <see cref="Protocol.PairingRequest"/> for the exchange.
/// </summary>
sealed class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Nothing to draw on. The daemon reads a non-zero exit as "no answer", which is the truth.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            Console.Error.WriteLine("wlrix-bluetooth-prompt: no WAYLAND_DISPLAY to put a window on");
            return 2;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseWayland()
            .With(new WaylandPlatformOptions { AppId = "com.wlrix.bluetoothprompt" })
            .UseReactiveUI();
}
