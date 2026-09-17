using Wlrix.Files.Core.Operations;
using Wlrix.Files.Core.Testing;
using Xunit;

namespace Wlrix.Files.Core.Tests;

public class OperationQueueTests
{
    private sealed class SingleFileSystemProvider(IFileSystem fs) : IFileSystemProvider
    {
        public Task<IFileSystem> GetAsync(Location location, CancellationToken cancellationToken) =>
            Task.FromResult(fs);
    }

    [Fact]
    public async Task AQueuedOperationRunsAndCompletes()
    {
        var fs = new FakeFileSystem().AddFile("/src/a.txt", "hello").AddDirectory("/dst");
        await using var queue = new OperationQueue(new SingleFileSystemProvider(fs));

        var queued = queue.Enqueue(
            FileOperation.Copy([Location.Parse("/src/a.txt")], Location.Parse("/dst")),
            retries: FixedRetryPolicy.SkipAll);

        var result = await queued.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Succeeded);
        Assert.Equal("hello"u8.ToArray(), fs.ContentOf("/dst/a.txt"));
    }

    [Fact]
    public async Task EnqueueReturnsWithoutWaitingForTheWork()
    {
        // The caller is the UI thread. It must never wait on a disk.
        var fs = new FakeFileSystem { Latency = TimeSpan.FromMilliseconds(50) };
        fs.AddFile("/src/a.txt", "x").AddDirectory("/dst");
        await using var queue = new OperationQueue(new SingleFileSystemProvider(fs));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var queued = queue.Enqueue(
            FileOperation.Copy([Location.Parse("/src/a.txt")], Location.Parse("/dst")),
            retries: FixedRetryPolicy.SkipAll);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 40, $"Enqueue took {stopwatch.ElapsedMilliseconds} ms");
        await queued.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task OperationsOnOneDeviceRunOneAtATime()
    {
        // Two copies to the same disk finish later run together than run in sequence -- the
        // head seeks between them -- so the queue does not overlap them.
        var fs = new FakeFileSystem { Latency = TimeSpan.FromMilliseconds(15) };
        fs.AddDirectory("/dst");
        for (var i = 0; i < 6; i++)
            fs.AddFile($"/src{i}/a.txt", "x");

        await using var queue = new OperationQueue(new SingleFileSystemProvider(fs));

        // Concurrency is counted from the operations' own phase reports, not from when
        // their handles were created: every handle exists the moment it is enqueued, and
        // counting those would measure the queue length rather than the parallelism.
        var gate = new Lock();
        var live = new HashSet<QueuedOperation>();
        var peak = 0;
        var completions = new List<Task>();

        for (var i = 0; i < 6; i++)
        {
            var queued = queue.Enqueue(
                FileOperation.Copy([Location.Parse($"/src{i}/a.txt")], Location.Parse("/dst")),
                conflicts: new AutoRenameResolver(), retries: FixedRetryPolicy.SkipAll);

            queued.Changed += q =>
            {
                lock (gate)
                {
                    if (q.Progress.Phase is OperationPhase.Scanning or OperationPhase.Working)
                        live.Add(q);
                    else
                        live.Remove(q);
                    peak = Math.Max(peak, live.Count);
                }
            };
            completions.Add(queued.Completion);
        }

        await Task.WhenAll(completions).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task CancellingAQueuedOperationStopsIt()
    {
        var fs = new FakeFileSystem { Latency = TimeSpan.FromMilliseconds(20) };
        fs.AddDirectory("/dst");
        for (var i = 0; i < 50; i++)
            fs.AddFile($"/src/f{i}.txt", "x");

        await using var queue = new OperationQueue(new SingleFileSystemProvider(fs));
        var queued = queue.Enqueue(
            FileOperation.Copy([Location.Parse("/src")], Location.Parse("/dst")),
            retries: FixedRetryPolicy.SkipAll);

        await Task.Delay(120);
        queued.Cancel();

        var result = await queued.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(OperationPhase.Canceled, result.Phase);
    }

    [Fact]
    public async Task AnOperationLeavesTheActiveListWhenItFinishes()
    {
        var fs = new FakeFileSystem().AddFile("/src/a.txt", "x").AddDirectory("/dst");
        await using var queue = new OperationQueue(new SingleFileSystemProvider(fs));

        var queued = queue.Enqueue(
            FileOperation.Copy([Location.Parse("/src/a.txt")], Location.Parse("/dst")),
            retries: FixedRetryPolicy.SkipAll);

        await queued.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        // The handle completes before the list is tidied, so allow the finally block to run.
        for (var i = 0; i < 50 && queue.Active.Count > 0; i++)
            await Task.Delay(10);
        Assert.Empty(queue.Active);
    }

    [Fact]
    public async Task DisposingCancelsWhateverIsStillRunning()
    {
        // Closing the application must not wait on a copy to a share that has stopped
        // answering.
        var fs = new FakeFileSystem { Latency = TimeSpan.FromMilliseconds(30) };
        fs.AddDirectory("/dst");
        for (var i = 0; i < 200; i++)
            fs.AddFile($"/src/f{i}.txt", "x");

        var queue = new OperationQueue(new SingleFileSystemProvider(fs));
        var queued = queue.Enqueue(
            FileOperation.Copy([Location.Parse("/src")], Location.Parse("/dst")),
            retries: FixedRetryPolicy.SkipAll);

        await Task.Delay(80);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await queue.DisposeAsync();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(6), $"dispose took {stopwatch.Elapsed}");
        var result = await queued.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(OperationPhase.Completed, result.Phase);
    }

    [Fact]
    public async Task AJobThatThrowsUnexpectedlyStillCompletesItsHandle()
    {
        // Anything reaching the queue's catch is a defect in the job, but the handle must
        // still complete or an awaiter hangs forever.
        await using var queue = new OperationQueue(new ThrowingProvider());
        var queued = queue.Enqueue(FileOperation.Copy([Location.Parse("/a")], Location.Parse("/b")));

        var result = await queued.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(OperationPhase.Failed, result.Phase);
        Assert.NotEmpty(result.Errors);
    }

    private sealed class ThrowingProvider : IFileSystemProvider
    {
        public Task<IFileSystem> GetAsync(Location location, CancellationToken cancellationToken) =>
            throw new InvalidTimeZoneException("a provider misbehaving");
    }
}
