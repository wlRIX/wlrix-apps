using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Wlrix.Common;
using Wlrix.Toolchest.Desktop;
using ZLogger;

namespace Wlrix.Toolchest.Services;

/// <summary>Launches application entries (and a bare terminal for the Desktop stub).</summary>
public interface IAppLauncher
{
    /// <summary>Raised (with a user-facing message) when a launch fails.</summary>
    event Action<string>? LaunchFailed;

    /// <summary>Launches <paramref name="exec"/> (a <c>.desktop</c> Exec value), optionally in a terminal.</summary>
    void Launch(string displayName, string exec, bool terminal);

    /// <summary>Opens a bare terminal emulator (Desktop → Open Unix Shell).</summary>
    void OpenTerminal();
}

/// <inheritdoc />
public sealed class AppLauncher(ILogger<AppLauncher> logger) : IAppLauncher
{
    private static readonly string[] TerminalCandidates =
        ["alacritty", "foot", "kitty", "wezterm", "konsole", "gnome-terminal", "xterm"];

    public event Action<string>? LaunchFailed;

    public void Launch(string displayName, string exec, bool terminal)
    {
        if (ExecParser.Parse(exec) is not { } parsed || parsed.File.Length == 0)
        {
            Fail(displayName, $"“{exec}” has no runnable command.");
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
            Fail(displayName, "No terminal emulator was found to run this application.");
            return;
        }

        var termArgs = new List<string> { "-e", file };
        termArgs.AddRange(args);
        Start(displayName, term, termArgs);
    }

    public void OpenTerminal()
    {
        if (ResolveTerminal() is { } term)
            Start("Unix Shell", term, []);
        else
            Fail("Unix Shell", "No terminal emulator was found.");
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
            Fail(displayName, $"Could not start “{displayName}”.\n{ex.Message}");
        }
    }

    private static string? ResolveTerminal()
    {
        if (Environment.GetEnvironmentVariable("TERMINAL") is { Length: > 0 } env
            && Executables.Which(env) is { } configured)
            return configured;

        foreach (var candidate in TerminalCandidates)
            if (Executables.Which(candidate) is { } path)
                return path;

        return null;
    }

    private void Fail(string displayName, string message)
    {
        logger.ZLogWarning($"Launch of '{displayName}' failed: {message}");
        LaunchFailed?.Invoke(message);
    }
}
