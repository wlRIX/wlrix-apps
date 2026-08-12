using Microsoft.Extensions.Logging;
using Wlrix.Packages;
using Wlrix.Packages.Backends.Parsing;
using Wlrix.Packages.Privileged;
using ZLogger;

namespace Wlrix.SoftwareManager.Services;

/// <summary>What a finished transaction did.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Message">Why, when the outcome alone does not say.</param>
public sealed record TransactionResult(TransactionOutcome Outcome, string? Message);

/// <summary>What the window wants to know while a transaction runs.</summary>
public interface ITransactionObserver
{
    /// <summary>The exact command that is running.</summary>
    void OnCommand(string command);

    /// <summary>One line of output, for the Log pane.</summary>
    void OnLog(string line, bool isError);

    /// <summary>How far along the package manager appears to be.</summary>
    void OnProgress(TransactionPhase phase, double? percent);
}

/// <summary>Runs one privileged transaction and reports it to the window.</summary>
public interface ITransactionService
{
    /// <summary>Whether a transaction could be run at all on this system.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Carries out <paramref name="request"/>, calling <paramref name="observer"/> as it goes.
    /// Every call to the observer happens on the UI thread.
    /// </summary>
    Task<TransactionResult> RunAsync(HelperRequest request, ITransactionObserver observer,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The join between <see cref="IPrivilegedRunner"/>, which knows how to run something as root,
/// and the window, which knows what to do with each thing it says.
///
/// The one piece of real work here is marshalling: the runner produces events on a background
/// thread, and every one of them ends up setting a bound property. Doing that on the UI thread
/// once, here, is what keeps every view model on the other side of it free of dispatcher calls.
/// </summary>
public sealed class TransactionService(
    IPrivilegedRunner runner,
    IPackageBackendFactory backendFactory,
    ILogger<TransactionService> logger) : ITransactionService
{
    public bool IsAvailable => runner.IsAvailable && backendFactory.Resolve().IsSupported;

    public async Task<TransactionResult> RunAsync(HelperRequest request,
        ITransactionObserver observer, CancellationToken cancellationToken = default)
    {
        var backend = request.Backend;
        var result = new TransactionResult(TransactionOutcome.Failed,
            "The transaction ended without saying how.");

        await foreach (var @event in runner.RunAsync(request, cancellationToken).ConfigureAwait(true))
        {
            switch (@event)
            {
                case TransactionEvent.Command command:
                    logger.ZLogInformation($"Running: {command.Text}");
                    observer.OnCommand(command.Text);
                    break;

                case TransactionEvent.Log log:
                    observer.OnLog(log.Text, log.IsError);

                    // Progress is inferred from the same lines, rather than from a second
                    // stream: there is no second stream, and a phase read off the log is at
                    // least guaranteed to agree with what the user can see in it.
                    if (TransactionProgress.Read(backend, log.Text) is { } progress)
                        observer.OnProgress(progress.Phase, progress.Percent);
                    break;

                case TransactionEvent.Completed completed:
                    result = new TransactionResult(completed.Outcome, completed.Message);
                    break;
            }
        }

        logger.ZLogInformation($"Transaction {request.Verb} finished: {result.Outcome}");
        return result;
    }
}
