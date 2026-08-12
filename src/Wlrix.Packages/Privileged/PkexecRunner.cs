using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Wlrix.Common;
using Wlrix.Packages.Processes;
using ZLogger;

namespace Wlrix.Packages.Privileged;

/// <summary>How a privileged transaction ended.</summary>
public enum TransactionOutcome
{
    /// <summary>Still running.</summary>
    Running,

    /// <summary>The package manager finished successfully.</summary>
    Succeeded,

    /// <summary>The package manager ran and failed.</summary>
    Failed,

    /// <summary>
    /// The user dismissed the authentication dialog, or was not allowed to authenticate. Worth
    /// telling apart from a failure: nothing was attempted, so there is nothing to investigate
    /// and no half-finished state to worry about.
    /// </summary>
    NotAuthorized,

    /// <summary>The user pressed Stop.</summary>
    Canceled,

    /// <summary>pkexec or the helper could not be started at all — usually a broken install.</summary>
    Unavailable,
}

/// <summary>Something that happened while a transaction ran.</summary>
public abstract record TransactionEvent
{
    /// <summary>The exact command the helper is about to run, for the Command pane.</summary>
    public sealed record Command(string Text) : TransactionEvent;

    /// <summary>A line of the package manager's output.</summary>
    public sealed record Log(string Text, bool IsError) : TransactionEvent;

    /// <summary>The transaction ended. Always the last event.</summary>
    public sealed record Completed(TransactionOutcome Outcome, string? Message) : TransactionEvent;
}

/// <summary>Runs a transaction with root privileges.</summary>
public interface IPrivilegedRunner
{
    /// <summary>Whether the machinery to do this at all is present.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Carries out <paramref name="request"/>, yielding what happens as it happens. The sequence
    /// always ends with a <see cref="TransactionEvent.Completed"/>, including when it fails.
    /// </summary>
    IAsyncEnumerable<TransactionEvent> RunAsync(HelperRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs <c>wlrix-pkg-helper</c> through <c>pkexec</c>, one process per transaction.
///
/// One process per transaction, rather than a helper that stays alive taking instructions, is a
/// deliberate choice. A long-lived root process reading commands from a pipe is a much larger
/// thing to get right — and to convince anyone else is right — than a program that is handed a
/// verb, does it, and exits. The cost is one authentication per transaction, and the polkit
/// policy's <c>auth_admin_keep</c> covers a run of them with a single prompt.
/// </summary>
public sealed class PkexecRunner(ILogger<PkexecRunner> logger) : IPrivilegedRunner
{
    /// <summary>The installed helper. An absolute path, because pkexec requires one.</summary>
    private const string HelperPath = "/usr/bin/wlrix-pkg-helper";

    /// <summary>
    /// pkexec's own failure codes, from its manual: 126 is "the authorization could not be
    /// obtained" — which includes the user closing the dialog — and 127 could not be executed.
    /// A package manager can also exit 126 or 127, so these are only read when the helper never
    /// got as far as announcing a command.
    /// </summary>
    private const int NotAuthorized = 126;

    private const int NotExecuted = 127;

    public bool IsAvailable => Executables.Exists("pkexec") && File.Exists(ResolveHelper());

    public async IAsyncEnumerable<TransactionEvent> RunAsync(HelperRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The work happens in an ordinary async method writing to a channel, and this iterator
        // only drains it. An iterator cannot yield from inside a catch block, and every
        // interesting outcome here -- a refused authentication, a missing helper, the user
        // pressing Stop -- arrives as an exception that has to become a final event.
        var channel = Channel.CreateUnbounded<TransactionEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        _ = Task.Run(() => PumpAsync(request, channel.Writer, cancellationToken), CancellationToken.None);

        // Deliberately not passing the cancellation token to the reader. The contract is that
        // the sequence always ends with a Completed, and a cancelled read would throw instead of
        // delivering the Canceled one -- leaving the UI showing a transaction still in flight.
        await foreach (var @event in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            yield return @event;
    }

    private async Task PumpAsync(HelperRequest request, ChannelWriter<TransactionEvent> writer,
        CancellationToken cancellationToken)
    {
        var sawCommand = false;
        string? failure = null;
        int? exitCode = null;

        try
        {
            if (Executables.Which("pkexec") is not { } pkexec)
            {
                writer.TryWrite(Unavailable("pkexec is not installed, so nothing can be run as root."));
                return;
            }

            var helper = ResolveHelper();
            if (!File.Exists(helper))
            {
                writer.TryWrite(Unavailable(
                    $"{helper} is missing. The Software Manager is not fully installed."));
                return;
            }

            var arguments = new List<string> { helper };
            arguments.AddRange(request.ToArguments());
            logger.ZLogInformation($"pkexec {helper} {string.Join(' ', request.ToArguments())}");

            var runner = new ProcessRunner();
            await foreach (var line in runner.StreamAsync(pkexec, arguments, cancellationToken)
                               .ConfigureAwait(false))
            {
                // The helper's exit status arrives as a synthetic line from the runner; the
                // package manager's own arrives as a DONE frame. The helper's is the
                // authoritative one for "did pkexec get that far at all".
                if (line is ProcessExited exited)
                {
                    exitCode ??= exited.ExitCode;
                    continue;
                }

                // stderr is not framed: it is pkexec's own complaints, and anything the helper's
                // runtime wrote before the helper got going. Both belong in the log verbatim.
                if (line.IsError)
                {
                    writer.TryWrite(new TransactionEvent.Log(line.Text, IsError: true));
                    continue;
                }

                var frame = HelperFrame.Parse(line.Text);
                switch (frame.Kind)
                {
                    case FrameKind.Command:
                        sawCommand = true;
                        writer.TryWrite(new TransactionEvent.Command(frame.Text));
                        break;

                    case FrameKind.Log:
                        writer.TryWrite(new TransactionEvent.Log(frame.Text, IsError: false));
                        break;

                    case FrameKind.Error:
                        writer.TryWrite(new TransactionEvent.Log(frame.Text, IsError: true));
                        break;

                    case FrameKind.Done:
                        exitCode = frame.ExitCode ?? exitCode;
                        break;

                    case FrameKind.Failed:
                        failure = frame.Text;
                        writer.TryWrite(new TransactionEvent.Log(frame.Text, IsError: true));
                        break;
                }
            }

            writer.TryWrite(Finish(sawCommand, failure, exitCode));
        }
        catch (OperationCanceledException)
        {
            writer.TryWrite(new TransactionEvent.Completed(TransactionOutcome.Canceled, null));
        }
        catch (PackageProcessException ex)
        {
            writer.TryWrite(Unavailable(ex.Message));
        }
        catch (Exception ex)
        {
            logger.ZLogError(ex, $"The privileged transaction failed unexpectedly.");
            writer.TryWrite(new TransactionEvent.Completed(TransactionOutcome.Failed, ex.Message));
        }
        finally
        {
            writer.TryComplete();
        }
    }

    /// <summary>
    /// What the outcome was, given what came back.
    ///
    /// <paramref name="sawCommand"/> is what tells a refused authentication apart from a package
    /// manager that happened to exit 126. The helper announces its command before running
    /// anything, so if no command was ever announced, the helper never started — and at that
    /// point pkexec's own exit codes are the ones being read.
    /// </summary>
    private TransactionEvent.Completed Finish(bool sawCommand, string? failure, int? exitCode)
    {
        if (failure is not null)
            return new TransactionEvent.Completed(TransactionOutcome.Failed, failure);

        if (!sawCommand)
        {
            return exitCode switch
            {
                NotAuthorized => new TransactionEvent.Completed(TransactionOutcome.NotAuthorized, null),
                NotExecuted => new TransactionEvent.Completed(TransactionOutcome.Unavailable,
                    "pkexec could not run the helper."),
                _ => new TransactionEvent.Completed(TransactionOutcome.Failed,
                    $"The helper exited {exitCode?.ToString() ?? "without a status"} without doing anything."),
            };
        }

        return exitCode is 0
            ? new TransactionEvent.Completed(TransactionOutcome.Succeeded, null)
            : new TransactionEvent.Completed(TransactionOutcome.Failed, null);
    }

    private TransactionEvent.Completed Unavailable(string message)
    {
        logger.ZLogWarning($"Cannot run a privileged transaction: {message}");
        return new TransactionEvent.Completed(TransactionOutcome.Unavailable, message);
    }

    /// <summary>
    /// Where the helper is. The installed path normally, but a helper sitting beside this
    /// assembly wins — that is what makes `dotnet run` from a source tree work without a
    /// system install, and it is checked first for exactly one reason: the developer running
    /// from source is the only person who has one there.
    /// </summary>
    private static string ResolveHelper()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "wlrix-pkg-helper");
        return File.Exists(beside) ? beside : HelperPath;
    }
}
