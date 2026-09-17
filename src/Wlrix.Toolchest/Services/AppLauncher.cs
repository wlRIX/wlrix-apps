using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Diagnostics;
using Wlrix.Common;
using Wlrix.Common.Desktop;
using Wlrix.Toolchest.Localization;
using ZLogger;

namespace Wlrix.Toolchest.Services;

/// <summary>Launches application entries (and a bare terminal for the Desktop stub).</summary>
public interface IAppLauncher
{
    /// <summary>Raised (with a user-facing message) when a launch fails.</summary>
    event Action<string>? LaunchFailed;

    /// <summary>Launches <paramref name="exec"/> (a <c>.desktop</c> Exec value), optionally in a terminal.</summary>
    void Launch(string displayName, string exec, bool terminal);

    /// <summary>
    /// Launches <paramref name="program"/> from the <c>PATH</c> by name, with
    /// <paramref name="args"/>, for the wlRIX apps the menus name outright rather than finding
    /// in a <c>.desktop</c> file.
    /// </summary>
    void Run(string displayName, string program, params string[] args);

    /// <summary>Opens a bare terminal emulator (Desktop → Open Terminal).</summary>
    void OpenTerminal();
}

/// <inheritdoc />
public sealed class AppLauncher(ILogger<AppLauncher> logger) : IAppLauncher
{
    public event Action<string>? LaunchFailed;

    public void Launch(string displayName, string exec, bool terminal)
    {
        if (ExecParser.Parse(exec) is not { } parsed || parsed.File.Length == 0)
        {
            Fail(displayName, Strings.NoRunnableCommand(exec));
            return;
        }

        var (file, args) = parsed;

        if (!terminal)
        {
            Start(displayName, file, args);
            return;
        }

        // Terminal apps: run as `<terminal> -e <program> <args…>` (best-effort; -e is common).
        if (ResolveTerminal() is not { } term)
        {
            Fail(displayName, Strings.NoTerminalForApp);
            return;
        }

        Start(displayName, term, TerminalEmulator.Wrap(file, args));
    }

    public void Run(string displayName, string program, params string[] args)
    {
        // By name off the PATH, which is where `just install-cs` puts the wlRIX apps. Nothing
        // beside this assembly to fall back to: each app publishes into its own directory, so
        // running the Toolchest from a source tree never has a sibling to find.
        if (Executables.Which(program) is { } path)
            Start(displayName, path, args);
        else
            Fail(displayName, Strings.NotInstalled(program));
    }

    public void OpenTerminal()
    {
        if (ResolveTerminal() is { } term)
            Start("Terminal", term, []);
        else
            Fail("Terminal", Strings.NoTerminal);
    }

    private void Start(string displayName, string file, IReadOnlyList<string> args)
    {
        try
        {
            var info = new ProcessStartInfo { FileName = file, UseShellExecute = false };
            foreach (var arg in args)
                info.ArgumentList.Add(arg);

            using var process = Process.Start(info);
            logger.ZLogInformation($"Launched '{displayName}': {file} (pid {process?.Id})");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            logger.ZLogError(ex, $"Failed to launch '{displayName}' ({file}).");
            Fail(displayName, Strings.CouldNotStart(displayName, ex.Message));
        }
    }

    // Shared with the file manager, which opens files with console applications.
    private static string? ResolveTerminal() => TerminalEmulator.Resolve();

    private void Fail(string displayName, string message)
    {
        logger.ZLogWarning($"Launch of '{displayName}' failed: {message}");
        LaunchFailed?.Invoke(message);
    }
}
