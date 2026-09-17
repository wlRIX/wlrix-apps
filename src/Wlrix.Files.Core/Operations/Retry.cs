namespace Wlrix.Files.Core.Operations;

/// <summary>What to do about a failure.</summary>
public enum RetryAction
{
    /// <summary>Try the same item again.</summary>
    Retry,
    /// <summary>Give up on this item and continue with the rest.</summary>
    Skip,
    /// <summary>Give up on this item and every later failure in this operation.</summary>
    SkipAll,
    /// <summary>Abandon the whole operation.</summary>
    Abort
}

/// <summary>The answer to one failure.</summary>
/// <param name="Action">What to do.</param>
/// <param name="Delay">How long to wait first. Zero retries immediately.</param>
public readonly record struct RetryDecision(RetryAction Action, TimeSpan Delay = default)
{
    public static RetryDecision Retry(TimeSpan delay = default) => new(RetryAction.Retry, delay);
    public static RetryDecision Skip { get; } = new(RetryAction.Skip);
    public static RetryDecision SkipAll { get; } = new(RetryAction.SkipAll);
    public static RetryDecision Abort { get; } = new(RetryAction.Abort);
}

/// <summary>One item's failure, with enough context to decide about it.</summary>
/// <param name="Item">What was being processed.</param>
/// <param name="Error">Why it failed.</param>
/// <param name="Attempt">How many times this item has already been tried, starting at 1.</param>
public sealed record OperationError(PlannedItem Item, FileOperationException Error, int Attempt);

/// <summary>Decides what to do when an item fails.</summary>
public interface IRetryPolicy
{
    Task<RetryDecision> OnErrorAsync(OperationError error, CancellationToken cancellationToken);
}

/// <summary>
/// Retries transient failures a few times with a growing delay, and refers anything else to
/// the user.
/// </summary>
/// <remarks>
/// The distinction is <see cref="FileOperationException.IsTransient"/>: a dropped connection or
/// a timeout may well succeed on the next attempt, where a permission error will fail
/// identically forever and retrying it only delays telling the user.
///
/// <para>
/// The backoff exists for remote shares, where hammering a server that has just dropped the
/// connection is the surest way to keep it dropped.
/// </para>
/// </remarks>
public sealed class DefaultRetryPolicy : IRetryPolicy
{
    /// <summary>How many times a transient failure is retried before the user is asked.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>The first delay. Each attempt doubles it.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Asked when the automatic retries are exhausted, or the failure is not transient.</summary>
    public IRetryPolicy? Fallback { get; init; }

    public async Task<RetryDecision> OnErrorAsync(OperationError error, CancellationToken cancellationToken)
    {
        if (error.Error.IsTransient && error.Attempt < MaxAttempts)
        {
            var delay = InitialDelay * Math.Pow(2, error.Attempt - 1);
            return RetryDecision.Retry(delay);
        }

        if (Fallback is not null)
            return await Fallback.OnErrorAsync(error, cancellationToken).ConfigureAwait(false);

        // With nobody to ask, skipping is the safer default than aborting: a copy of a
        // thousand files should not be abandoned because one of them is unreadable.
        return RetryDecision.Skip;
    }
}

/// <summary>Answers every failure the same way. For tests and for unattended operations.</summary>
public sealed class FixedRetryPolicy(RetryAction action) : IRetryPolicy
{
    public static IRetryPolicy SkipAll { get; } = new FixedRetryPolicy(RetryAction.SkipAll);
    public static IRetryPolicy AbortOnError { get; } = new FixedRetryPolicy(RetryAction.Abort);

    public Task<RetryDecision> OnErrorAsync(OperationError error, CancellationToken cancellationToken) =>
        Task.FromResult(new RetryDecision(action));
}
