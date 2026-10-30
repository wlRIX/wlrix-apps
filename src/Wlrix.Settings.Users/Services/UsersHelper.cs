using System.ComponentModel;
using System.Diagnostics;
using Wlrix.Common;

namespace Wlrix.Settings.Users.Services;

/// <summary>How a request to the helper ended.</summary>
internal enum HelperOutcome
{
    Succeeded,

    /// <summary>The helper ran and refused, or failed; the message says why.</summary>
    Failed,

    /// <summary>The authentication dialog was dismissed, or the password was wrong.</summary>
    NotAuthorized,

    /// <summary>pkexec or the helper is not installed.</summary>
    Unavailable,
}

/// <param name="Message">The helper's own explanation, when there is one.</param>
internal sealed record HelperResult(HelperOutcome Outcome, string? Message);

/// <summary>
/// Runs <c>wlrix-users-helper</c> through <c>pkexec</c>, one process per request, as Login
/// Settings runs its own (<c>GreeterHelperRunner</c>): the exit status and one line on stderr are
/// the whole answer.
/// </summary>
internal static class UsersHelper
{
    /// <summary>The installed helper. An absolute path, because pkexec requires one and the polkit action names it.</summary>
    public const string HelperPath = "/usr/bin/wlrix-users-helper";

    /// <summary>pkexec's own failure codes: 126 "not authorized" (including a dismissed dialog), 127 "could not run it".</summary>
    private const int NotAuthorized = 126;

    private const int NotExecuted = 127;

    public static bool IsAvailable => Executables.Exists("pkexec") && File.Exists(HelperPath);

    /// <summary>Turn the fingerprint on or off for <paramref name="target"/>, for everyone.</summary>
    public static Task<HelperResult> SetFingerprintAsync(PamTarget target, bool on) =>
        RunAsync(["fingerprint", on ? "on" : "off", target == PamTarget.Sudo ? "sudo" : "polkit"]);

    private static async Task<HelperResult> RunAsync(IEnumerable<string> arguments)
    {
        if (!IsAvailable)
            return new HelperResult(HelperOutcome.Unavailable, $"{HelperPath} or pkexec is not installed.");

        var info = Executables.StartInfoFor("pkexec");
        info.UseShellExecute = false;
        info.RedirectStandardError = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardInput = true;
        // Without an agent, pkexec would prompt on a terminal hidden under the compositor and wait
        // for ever; see GreeterHelperRunner.
        info.ArgumentList.Add("--disable-internal-agent");
        info.ArgumentList.Add(HelperPath);
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new HelperResult(HelperOutcome.Unavailable, ex.Message);
        }

        if (process is null)
            return new HelperResult(HelperOutcome.Unavailable, "could not start pkexec");

        using (process)
        {
            process.StandardInput.Close();
            var error = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);
            var message = (await error.ConfigureAwait(false)).Trim();
            return process.ExitCode switch
            {
                0 => new HelperResult(HelperOutcome.Succeeded, null),
                NotAuthorized => new HelperResult(HelperOutcome.NotAuthorized, null),
                NotExecuted => new HelperResult(HelperOutcome.Unavailable, message.Length > 0 ? message : "pkexec could not run the helper."),
                _ => new HelperResult(HelperOutcome.Failed, message.Length > 0 ? message : null),
            };
        }
    }
}
