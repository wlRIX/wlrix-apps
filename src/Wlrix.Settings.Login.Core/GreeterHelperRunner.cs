using System.ComponentModel;
using System.Diagnostics;
using Wlrix.Common;

namespace Wlrix.Settings.Login.Core;

/// <summary>How a request to the helper ended.</summary>
public enum HelperOutcome
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
public sealed record HelperResult(HelperOutcome Outcome, string? Message);

/// <summary>
/// Runs <c>wlrix-login-helper</c> through <c>pkexec</c>, one process per request, the way the
/// Software Manager runs its helper. The helper says nothing on success and one line on stderr
/// when it refuses, so there is no frame protocol to read: the exit status and that line are the
/// whole answer.
/// </summary>
public sealed class GreeterHelperRunner
{
    /// <summary>The installed helper. An absolute path, because pkexec requires one.</summary>
    public const string HelperPath = "/usr/bin/wlrix-login-helper";

    /// <summary>
    /// pkexec's own failure codes: 126 is "not authorized", which includes the dialog being
    /// dismissed, and 127 "could not run it". The helper exits 1 or 2, never these.
    /// </summary>
    private const int NotAuthorized = 126;

    private const int NotExecuted = 127;

    /// <summary>What pkexec says on stderr when no polkit authentication agent is running.</summary>
    private const string NoAgentMessage = "No authentication agent found";

    private const string NoAgentExplanation =
        "No polkit authentication agent is running in this session, so there is nothing to ask for the "
        + "administrator's password. wlrix-session starts wlrix-polkit-agent; start it (or another agent) and try again.";

    public static bool IsAvailable => Executables.Exists("pkexec") && File.Exists(ResolveHelper());

    public async Task<HelperResult> RunAsync(GreeterRequest request, CancellationToken cancellationToken = default)
    {
        if (!Executables.Exists("pkexec"))
            return new HelperResult(HelperOutcome.Unavailable, "pkexec is not installed.");

        var helper = ResolveHelper();
        if (!File.Exists(helper))
            return new HelperResult(HelperOutcome.Unavailable, $"{helper} is not installed.");

        var info = new ProcessStartInfo("pkexec")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
        };
        // Without a polkit agent in the session, pkexec falls back to a text prompt on its
        // controlling terminal. A session greetd started has the VT as that terminal, so the prompt
        // lands on a console hidden under the compositor and this waits for ever. Refuse instead.
        info.ArgumentList.Add("--disable-internal-agent");
        info.ArgumentList.Add(helper);
        foreach (var argument in request.ToArguments())
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
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var message = (await error.ConfigureAwait(false)).Trim();

            return process.ExitCode switch
            {
                0 => new HelperResult(HelperOutcome.Succeeded, null),
                NotAuthorized => new HelperResult(HelperOutcome.NotAuthorized, null),
                NotExecuted => new HelperResult(HelperOutcome.Unavailable,
                    message.Contains(NoAgentMessage, StringComparison.Ordinal)
                        ? NoAgentExplanation
                        : "pkexec could not run the helper."),
                _ => new HelperResult(HelperOutcome.Failed, message.Length > 0 ? message : null),
            };
        }
    }

    /// <summary>
    /// Where the helper is: the installed path, unless one sits beside this assembly, which is
    /// what makes running from a source tree work. Same rule as the Software Manager's.
    /// </summary>
    private static string ResolveHelper()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "wlrix-login-helper");
        return File.Exists(beside) ? beside : HelperPath;
    }
}
