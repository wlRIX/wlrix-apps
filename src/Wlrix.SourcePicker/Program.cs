using System.Text.Json;
using Avalonia;
using ReactiveUI.Avalonia;
using Wlrix.SourcePicker.Models;
using Wlrix.SourcePicker.Services;

namespace Wlrix.SourcePicker;

internal sealed class Program
{
    /// <summary>Accepted: the selection is on stdout.</summary>
    private const int ExitAccepted = 0;
    /// <summary>The user said no. Not an error, and applications must not report it as one.</summary>
    private const int ExitCanceled = 1;
    /// <summary>Something went wrong before the user could answer.</summary>
    private const int ExitFailed = 2;

    /// <remarks>
    /// The manifest is read before Avalonia starts. There is nothing to show until it has been:
    /// a window with no sources in it would be a dialog asking the user to choose from nothing.
    /// </remarks>
    [STAThread]
    public static int Main(string[] args)
    {
        Manifest manifest;
        try
        {
            manifest = ManifestReader.Read(Console.OpenStandardInput());
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
        {
            // stderr, never stdout: stdout is the wire, and anything written there that is not
            // the answer corrupts it.
            Console.Error.WriteLine($"wlrix-source-picker: {e.Message}");
            return ExitFailed;
        }

        // The toolkit's own exit code is deliberately dropped. `desktop.Shutdown(code)` from
        // inside `Window.Closed` does not decide it: the lifetime is already shutting down by
        // then because the last window closed, and the code it settled on is 0 -- so a canceled
        // dialog exited "accepted" with nothing on stdout, which the portal reads as a picker
        // that died mid-answer and reports to the application as a failure rather than as the
        // cancel it was. The answer is taken from what the window left behind instead, once the
        // toolkit has gone, which also means nothing is writing to stdout while a UI is still
        // alive on it.
        BuildAvaloniaApp(manifest).StartWithClassicDesktopLifetime(args);
        return Answer(Result);
    }

    /// <summary>What the dialog chose, or null for a cancel.</summary>
    /// <remarks>
    /// Static because it has to outlive the toolkit: it is read after
    /// <c>StartWithClassicDesktopLifetime</c> has returned and everything else is gone.
    /// </remarks>
    internal static IReadOnlyList<string>? Result { get; set; }

    /// <remarks>
    /// No <c>LogToTrace</c>, unlike the other wlRIX apps. This program's stdout carries the
    /// answer back to the portal, so nothing else may be allowed near it.
    /// </remarks>
    public static AppBuilder BuildAvaloniaApp(Manifest manifest)
        => AppBuilder.Configure(() => new App(manifest))
            .UsePlatformDetect()
            .UseWayland()
            .With(new WaylandPlatformOptions { AppId = "com.wlrix.sourcepicker" })
#if DEBUG
            .WithDeveloperTools()
#endif
            .UseReactiveUI();

    /// <summary>
    /// The designer needs a parameterless builder, and only the designer calls this.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => BuildAvaloniaApp(new Manifest());

    /// <summary>Write the answer and say how it ended.</summary>
    internal static int Answer(IReadOnlyList<string>? chosen)
    {
        if (chosen is null || chosen.Count == 0)
        {
            return ExitCanceled;
        }

        var json = JsonSerializer.Serialize(
            new Selection { Sources = chosen }, PickerJsonContext.Default.Selection);
        Console.Out.Write(json);
        Console.Out.Flush();
        return ExitAccepted;
    }
}
