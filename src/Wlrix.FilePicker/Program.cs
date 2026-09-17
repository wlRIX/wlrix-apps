using System.Text.Json;
using Avalonia;
using ReactiveUI.Avalonia;
using Wlrix.Files.Core.Portal;
using Wlrix.FilePicker.Services;

namespace Wlrix.FilePicker;

/// <summary>
/// The wlRIX file chooser, run by <c>xdg-desktop-portal-wlrix</c> for one question.
/// </summary>
/// <remarks>
/// <para>
/// The same two-process arrangement as <c>wlrix-source-picker</c> and <c>wlrix-screenshot</c>,
/// and for the same reason: the backend is a calloop program with a Wayland connection and
/// PipeWire streams to service, and putting a toolkit inside it would mean two event loops in
/// one process. Here it also means the dialog can be written in the language the rest of the
/// desktop is, and can reuse the file manager's own listing machinery.
/// </para>
/// <para>
/// The contract, which <c>xdg-desktop-portal-wlrix/src/filechooser.rs</c> is the other half of:
/// a JSON request on stdin closed immediately, the answer as JSON on stdout, and an exit code
/// saying which of accepted, canceled and failed happened. Both signals are checked on the
/// other side, so a picker that dies mid-answer can never be read as a selection.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>Accepted: the answer is on stdout.</summary>
    private const int ExitAccepted = 0;

    /// <summary>The user said no. Not an error, and applications must not report it as one.</summary>
    private const int ExitCanceled = 1;

    /// <summary>Something went wrong before the user could answer.</summary>
    private const int ExitFailed = 2;

    [STAThread]
    public static int Main(string[] args)
    {
        // An option this version does not know is a usage error rather than something to
        // ignore, for the reason the file manager's own argument handling spells out: a
        // backend that grew a flag against a picker that predates it must fail loudly, not
        // quietly put the wrong dialog on screen.
        if (args.Length > 0)
        {
            Console.Error.WriteLine($"wlrix-file-picker: unexpected argument: {args[0]}");
            Console.Error.WriteLine("this program is run by xdg-desktop-portal-wlrix, not by hand");
            return ExitFailed;
        }

        FileChooserRequest request;
        try
        {
            request = RequestReader.Read(Console.OpenStandardInput());
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
        {
            // stderr, never stdout: stdout is the wire, and anything written there that is not
            // the answer corrupts it.
            Console.Error.WriteLine($"wlrix-file-picker: {e.Message}");
            return ExitFailed;
        }

        // The toolkit's own exit code is deliberately dropped. `desktop.Shutdown(code)` from
        // inside `Window.Closed` does not decide it: the lifetime is already shutting down by
        // then because the last window closed, and the code it settled on is 0 -- so a canceled
        // dialog exited "accepted" with nothing on stdout, which the portal reads as a helper
        // that died mid-answer. The answer is taken from what the window left behind instead,
        // once the toolkit has gone, which also means nothing is writing to stdout while a UI
        // is still alive on it.
        BuildAvaloniaApp(request).StartWithClassicDesktopLifetime([]);
        return Answer(Result);
    }

    /// <summary>What the dialog decided, or null for a cancel.</summary>
    /// <remarks>
    /// Static because it has to outlive the toolkit: it is read after
    /// <c>StartWithClassicDesktopLifetime</c> has returned and everything else is gone.
    /// </remarks>
    internal static FileChooserResult? Result { get; set; }

    /// <remarks>
    /// No <c>LogToTrace</c>, unlike the other wlRIX applications. This program's stdout carries
    /// the answer back to the portal, so nothing else may be allowed near it.
    /// </remarks>
    public static AppBuilder BuildAvaloniaApp(FileChooserRequest request)
        => AppBuilder.Configure(() => new App(request))
            .UsePlatformDetect()
            .UseWayland()
            .With(new WaylandPlatformOptions { AppId = "com.wlrix.filepicker" })
#if DEBUG
            .WithDeveloperTools()
#endif
            .UseReactiveUI();

    /// <summary>The designer needs a parameterless builder, and only the designer calls this.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => BuildAvaloniaApp(new FileChooserRequest());

    /// <summary>Write the answer and say how it ended.</summary>
    internal static int Answer(FileChooserResult? result)
    {
        // An empty list is not a selection. Reported as a cancel rather than an error: the
        // user ended up choosing nothing, which is what canceling means, and an application
        // should not show them a failure for it.
        if (result is null || result.Uris.Count == 0)
            return ExitCanceled;

        Console.Out.Write(JsonSerializer.Serialize(result, FileChooserJson.Default.FileChooserResult));
        Console.Out.Flush();
        return ExitAccepted;
    }
}
