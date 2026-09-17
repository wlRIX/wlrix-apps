using System.Collections.Concurrent;
using System.Threading.Channels;
using Wlrix.Files.Core.Platform;

namespace Wlrix.Files.Core.Operations;

/// <summary>One operation's public handle: what it is, how it is going, how to stop it.</summary>
public sealed class QueuedOperation
{
    private readonly CancellationTokenSource _cancellation;
    private readonly TaskCompletionSource<OperationResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal QueuedOperation(FileOperation operation, CancellationTokenSource cancellation)
    {
        Operation = operation;
        _cancellation = cancellation;
    }

    public FileOperation Operation { get; }

    /// <summary>The latest progress report.</summary>
    public OperationProgress Progress { get; private set; } = new(OperationPhase.Waiting);

    /// <summary>Completes when the operation finishes, one way or another.</summary>
    public Task<OperationResult> Completion => _completion.Task;

    /// <summary>Raised on each progress report, from a worker thread.</summary>
    /// <remarks>
    /// Subscribers marshal to their own thread. A UI subscriber posts to the dispatcher; the
    /// alternative — this class capturing a synchronization context — would make Core aware
    /// of a UI it must not know about.
    /// </remarks>
    public event Action<QueuedOperation>? Changed;

    /// <summary>Asks the operation to stop at the next item boundary.</summary>
    public void Cancel() => _cancellation.Cancel();

    internal void Report(OperationProgress progress)
    {
        Progress = progress;
        Changed?.Invoke(this);
    }

    internal void Complete(OperationResult result)
    {
        Progress = Progress with { Phase = result.Phase };
        Changed?.Invoke(this);
        _completion.TrySetResult(result);
    }
}

/// <summary>
/// Runs file operations one device at a time, in the background.
/// </summary>
/// <remarks>
/// <b>One worker per device, not one per operation.</b> Two copies to the same disk finish
/// later than the same two run in sequence — the head seeks between them — while two to
/// different disks genuinely overlap. Grouping by device gets both right without a
/// configuration knob.
///
/// <para>
/// Operations survive the window that started them: the queue belongs to the application, so
/// closing a window mid-copy does not abandon the copy.
/// </para>
/// </remarks>
public sealed class OperationQueue : IAsyncDisposable
{
    private readonly IFileSystemProvider _provider;
    private readonly MountTable? _mounts;
    private readonly CancellationTokenSource _shutdown = new();

    // One channel and one pump per device. A device here is a mount's major:minor for local
    // paths, or the mount key for a remote.
    private readonly ConcurrentDictionary<string, Channel<Func<CancellationToken, Task>>> _lanes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _pumps = new(StringComparer.Ordinal);
    private readonly List<QueuedOperation> _active = [];
    private readonly Lock _activeGate = new();

    public OperationQueue(IFileSystemProvider provider, MountTable? mounts = null)
    {
        _provider = provider;
        _mounts = mounts;
    }

    /// <summary>The operations that have not finished yet.</summary>
    public IReadOnlyList<QueuedOperation> Active
    {
        get
        {
            lock (_activeGate)
                return [.. _active];
        }
    }

    /// <summary>Raised when an operation is added or removed, from a worker thread.</summary>
    public event Action? ActiveChanged;

    /// <summary>Queues an operation and returns immediately.</summary>
    public QueuedOperation Enqueue(
        FileOperation operation,
        IConflictResolver? conflicts = null,
        IRetryPolicy? retries = null)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var queued = new QueuedOperation(operation, cancellation);

        lock (_activeGate)
            _active.Add(queued);
        ActiveChanged?.Invoke();

        var lane = LaneFor(operation);
        var writer = _lanes.GetOrAdd(lane, StartLane).Writer;

        // The channel is unbounded, so queueing never blocks the caller -- which is the UI
        // thread, and which must not wait on a disk that is busy.
        if (!writer.TryWrite(token => RunAsync(queued, cancellation, conflicts, retries, token)))
            queued.Complete(new OperationResult(OperationPhase.Failed, 0, 0, []));

        return queued;
    }

    private async Task RunAsync(
        QueuedOperation queued, CancellationTokenSource cancellation,
        IConflictResolver? conflicts, IRetryPolicy? retries, CancellationToken laneToken)
    {
        using (cancellation)
        {
            try
            {
                var job = new OperationJob(_provider, conflicts, retries, _mounts);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, laneToken);
                var progress = new Progress<OperationProgress>(queued.Report);
                var result = await job.RunAsync(queued.Operation, progress, linked.Token).ConfigureAwait(false);
                queued.Complete(result);
            }
            catch (Exception ex)
            {
                // A job should handle its own failures; anything reaching here is a defect,
                // and it must still complete the handle or an awaiter hangs forever.
                queued.Complete(new OperationResult(
                    OperationPhase.Failed, 0, 0,
                    [ex as FileOperationException ?? new FileOperationException(
                        FileErrorKind.Unknown, queued.Operation.Sources.FirstOrDefault() ?? Location.FromLocalPath("/"),
                        ex.Message, ex)]));
            }
            finally
            {
                lock (_activeGate)
                    _active.Remove(queued);
                ActiveChanged?.Invoke();
            }
        }
    }

    /// <summary>Which lane an operation runs in.</summary>
    private string LaneFor(FileOperation operation)
    {
        var probe = operation.Target ?? operation.Sources.FirstOrDefault();
        if (probe is null)
            return "default";

        // The target is what the lane is chosen by: a copy is bounded by where it is writing.
        if (probe.IsLocal && _mounts?.Find(probe) is { } mount)
            return mount.DeviceId;
        return probe.MountKey;
    }

    private Channel<Func<CancellationToken, Task>> StartLane(string lane)
    {
        var channel = Channel.CreateUnbounded<Func<CancellationToken, Task>>(
            new UnboundedChannelOptions { SingleReader = true });

        _pumps[lane] = Task.Run(async () =>
        {
            await foreach (var work in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                if (_shutdown.IsCancellationRequested)
                    break;
                await work(_shutdown.Token).ConfigureAwait(false);
            }
        });

        return channel;
    }

    /// <summary>Cancels everything in flight and waits for the workers to stop.</summary>
    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var channel in _lanes.Values)
            channel.Writer.TryComplete();

        try
        {
            await Task.WhenAll(_pumps.Values).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // Shutdown is no time to hang on a remote that has stopped answering.
        }

        _shutdown.Dispose();
    }
}
