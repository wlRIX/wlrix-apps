using System.Collections.Concurrent;

namespace Wlrix.Files.Core.Mtp;

/// <summary>
/// The one thread that talks to one device.
/// </summary>
/// <remarks>
/// libmtp is not thread-safe, and the protocol is strictly one request at a time: a transfer
/// holds the device until its last byte. So rather than a lock around each call — which would
/// still let two transfers interleave their callbacks on different threads — every call is a
/// work item on this queue, run in order on a thread that does nothing else.
///
/// <para>
/// A dedicated thread rather than the pool, because a transfer blocks for as long as the file
/// takes to move, and parking a pool thread for a ten-minute video starves everything else.
/// </para>
/// </remarks>
public sealed class MtpDeviceThread : IDisposable
{
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;

    public MtpDeviceThread(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    /// <summary>Runs <paramref name="work"/> on the device thread, in turn.</summary>
    /// <remarks>
    /// Cancellation only stops work that has not started. Work already running polls the token
    /// itself, where it can: a transfer checks it between blocks, and anything shorter is not
    /// worth abandoning halfway through a request the device has already accepted.
    /// </remarks>
    public Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _work.Add(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }
                try
                {
                    completion.TrySetResult(work());
                }
                catch (OperationCanceledException)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // Added after Dispose: the device is gone.
            completion.TrySetException(new MtpException(FileErrorKind.ConnectionLost, "the device has been released"));
        }
        return completion.Task;
    }

    public Task RunAsync(Action work, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            work();
            return true;
        }, cancellationToken);

    private void Run()
    {
        foreach (var work in _work.GetConsumingEnumerable())
            work();
    }

    /// <summary>Lets queued work finish, then ends the thread.</summary>
    /// <remarks>
    /// The wait is bounded. A transfer stuck on a device that has stopped answering can block
    /// inside libusb for as long as its own timeout, and an unplug should not hang the window
    /// that asked for it; the thread is a background one and goes when the process does.
    /// </remarks>
    public void Dispose()
    {
        _work.CompleteAdding();
        if (Thread.CurrentThread != _thread && _thread.Join(TimeSpan.FromSeconds(5)))
            _work.Dispose();
    }
}
