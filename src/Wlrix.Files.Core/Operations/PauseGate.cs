namespace Wlrix.Files.Core.Operations;

/// <summary>Holds a running operation still until it is resumed.</summary>
/// <remarks>
/// Waited on between items and before every read, so a pause takes effect within one buffer
/// rather than at the end of a file that may take minutes. Nothing is torn down while paused:
/// the part file stays open and the share stays connected, so resuming carries on from the
/// next byte. Cancelling still works while paused, because the wait honors the token.
/// </remarks>
public sealed class PauseGate
{
    private readonly Lock _gate = new();
    private TaskCompletionSource? _resumed;

    public bool IsPaused
    {
        get
        {
            lock (_gate)
                return _resumed is not null;
        }
    }

    public void Pause()
    {
        lock (_gate)
            _resumed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Resume()
    {
        TaskCompletionSource? resumed;
        lock (_gate)
        {
            resumed = _resumed;
            _resumed = null;
        }
        resumed?.TrySetResult();
    }

    /// <summary>Returns at once when running; while paused, when resumed or canceled.</summary>
    public Task WaitAsync(CancellationToken cancellationToken)
    {
        Task? resumed;
        lock (_gate)
            resumed = _resumed?.Task;
        return resumed is null ? Task.CompletedTask : resumed.WaitAsync(cancellationToken);
    }

    /// <summary>The same, for a synchronous read on a worker thread.</summary>
    public void Wait(CancellationToken cancellationToken) => WaitAsync(cancellationToken).GetAwaiter().GetResult();
}
