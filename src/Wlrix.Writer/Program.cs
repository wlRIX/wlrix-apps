// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media;
using ReactiveUI.Avalonia;
using SkiaSharp;
using Wlrix.Writer.Services;

namespace Wlrix.Writer;

// wlRIX is a Linux desktop and this program is only ever built for one, which is what the
// platform-compatibility analyzer wants said out loud.
//
// The file dialogs are the portal's: xdg-desktop-portal-wlrix answers FileChooser with
// wlrix-file-picker, the same arrangement Wlrix.ImageView and Wlrix.Archiver use.
[SupportedOSPlatform("linux")]
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // An option this version does not know is a usage error, not a filename. There are no
        // options yet, so anything with a leading double dash is one -- except a bare "--", the
        // usual way to say that everything after it is a path.
        var paths = new List<string>();
        var literal = false;
        foreach (var argument in args)
        {
            if (!literal && argument == "--")
            {
                literal = true;
                continue;
            }

            if (!literal && argument.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"wlrix-writer: unknown argument: {argument}");
                Environment.Exit(2);
            }

            paths.Add(argument);
        }

        // Before Avalonia, deliberately, as in Wlrix.Files. A second launch -- a file
        // manager opening another text file, or a person at a shell -- hands its files to the
        // window that already exists and leaves without ever creating one of its own, so the
        // files arrive as tabs instead of as a second editor.
        var guard = new DBusInstanceGuard();
        if (!guard.TryAcquireAsync().GetAwaiter().GetResult())
        {
            // With no files this is an Activate: the existing window comes forward as it is.
            guard.SendOpenAsync([.. paths.Select(ToUri)]).GetAwaiter().GetResult();
            guard.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return;
        }

        App.Guard = guard;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime([.. paths]);
    }

    /// <summary>
    /// Turns an argument into something the other instance can parse.
    /// </summary>
    /// <remarks>
    /// <c>org.freedesktop.Application.Open</c> takes URIs, and a relative path means nothing
    /// once it has crossed to a process with a different working directory.
    /// </remarks>
    private static string ToUri(string argument) =>
        Uri.TryCreate(argument, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1
            ? uri.ToString()
            : new Uri(Path.GetFullPath(argument)).ToString();

    /// <summary>
    /// A family Avalonia can fall back to as its <c>$Default</c>, chosen from what is installed.
    /// </summary>
    /// <remarks>
    /// AvaloniaEdit builds a typeface from the default family while its editor is still being
    /// constructed, before any style has given it a font. Avalonia takes that default from Skia,
    /// and on a system whose fontconfig names no default (this one: Skia answers with an empty
    /// family name) the lookup throws and takes the window down with it. The other wlRIX apps
    /// never ask for the default and so never meet this. The list is the theme's own
    /// WlrixFontFamily chain, then whatever a Linux system usually has.
    /// </remarks>
    private static string DefaultFontFamily()
    {
        string[] wanted = ["Helvetica", "Arial", "Liberation Sans", "Nimbus Sans", "DejaVu Sans", "Noto Sans"];
        var installed = SKFontManager.Default.FontFamilies.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return wanted.FirstOrDefault(installed.Contains) ?? installed.FirstOrDefault() ?? "sans-serif";
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseWayland()
            .With(new WaylandPlatformOptions { AppId = "com.wlrix.writer" })
            .With(new FontManagerOptions { DefaultFamilyName = DefaultFontFamily() })
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace()
            .UseReactiveUI();
}
