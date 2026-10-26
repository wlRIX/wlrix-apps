using Wlrix.Files.Core.Operations;
using Wlrix.Files.Core.Testing;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// Progress through a single large file, and the rate the status line shows beside it.
/// </summary>
/// <remarks>
/// A 3 GiB image off a share is one item for minutes, and progress used to be reported only
/// between items: the bar sat at nothing until the copy was over. These run one file over a
/// slow fake source and look for reports from partway through it.
/// </remarks>
public class TransferProgressTests
{
    private const int Size = 1024 * 1024;

    private sealed class SingleFileSystemProvider(FakeFileSystem fs) : IFileSystemProvider
    {
        public Task<IFileSystem> GetAsync(Location location, CancellationToken cancellationToken) =>
            Task.FromResult<IFileSystem>(fs);
    }

    /// <summary>Keeps every report, on whatever thread it arrives.</summary>
    private sealed class Reports : IProgress<OperationProgress>
    {
        private readonly List<OperationProgress> _seen = [];

        public void Report(OperationProgress value)
        {
            lock (_seen)
                _seen.Add(value);
        }

        /// <summary>The reports from partway through the file: some bytes, not all of them.</summary>
        public List<OperationProgress> Partway()
        {
            lock (_seen)
                return [.. _seen.Where(p => p is { Phase: OperationPhase.Working, BytesDone: > 0 } && p.BytesDone < p.BytesTotal)];
        }
    }

    /// <summary>One large file on a source that hands it over a chunk at a time, slowly.</summary>
    private static FakeFileSystem SlowShare() =>
        new FakeFileSystem { ReadDelay = TimeSpan.FromMilliseconds(30) }
            .AddFile("/share/big.iso", new byte[Size])
            .AddDirectory("/disk");

    [Fact]
    public async Task OneLargeFileReportsProgressWhileItCopies()
    {
        var fs = SlowShare();
        var reports = new Reports();

        var result = await new OperationJob(new SingleFileSystemProvider(fs)).RunAsync(
            FileOperation.Copy([Location.Parse("/share/big.iso")], Location.Parse("/disk")), reports);

        Assert.True(result.Succeeded);
        var partway = reports.Partway();
        Assert.NotEmpty(partway);
        Assert.All(partway, p =>
        {
            Assert.Equal("big.iso", p.Current);
            Assert.Equal(Size, p.BytesTotal);
            Assert.InRange(p.Fraction ?? -1, 0.0, 1.0);
        });
        // Forward only: a bar that jumped back would look like the copy had restarted.
        Assert.Equal(partway.Select(p => p.BytesDone).Order(), partway.Select(p => p.BytesDone));
    }

    [Fact]
    public async Task OneLargeFileReportsProgressWhileItIsCompressed()
    {
        var fs = SlowShare();
        var reports = new Reports();

        var result = await new OperationJob(new SingleFileSystemProvider(fs)).RunAsync(
            FileOperation.Compress([Location.Parse("/share/big.iso")], Location.Parse("/disk/big.zip"), ArchiveFormat.Zip),
            reports);

        Assert.True(result.Succeeded);
        Assert.NotEmpty(reports.Partway());
    }

    [Fact]
    public void TheRateWaitsForASecondOfHistory()
    {
        // A fast first report divides out to gigabytes a second; nothing is better than that.
        var rate = new TransferRate(TimeSpan.FromSeconds(3));
        rate.Add(TimeSpan.Zero, 0);
        rate.Add(TimeSpan.FromMilliseconds(50), 30_000_000);

        Assert.Null(rate.BytesPerSecond);
    }

    [Fact]
    public void TheRateIsOverTheWindowNotSinceTheStart()
    {
        var rate = new TransferRate(TimeSpan.FromSeconds(3));
        // Fast for ten seconds, then slow: the rate shown is the slow one.
        for (var s = 0; s <= 10; s++)
            rate.Add(TimeSpan.FromSeconds(s), s * 100L);
        for (var s = 11; s <= 14; s++)
            rate.Add(TimeSpan.FromSeconds(s), 1000 + (s - 10) * 10L);

        Assert.Equal(10, rate.BytesPerSecond!.Value, precision: 6);
    }

    [Fact]
    public void AStalledTransferFallsToNothing()
    {
        // The timer's samples repeat the last count. Once they fill the window, the rate is zero:
        // that is how a share that has stopped answering shows on the status line.
        var rate = new TransferRate(TimeSpan.FromSeconds(3));
        for (var s = 0; s <= 5; s++)
            rate.Add(TimeSpan.FromSeconds(s), s * 1000L);
        for (var s = 6; s <= 9; s++)
            rate.Add(TimeSpan.FromSeconds(s), 5000);

        Assert.Equal(0, rate.BytesPerSecond);
    }

    [Fact]
    public void ARetryStartingAFileOverStartsTheRateOver()
    {
        var rate = new TransferRate(TimeSpan.FromSeconds(3));
        rate.Add(TimeSpan.Zero, 0);
        rate.Add(TimeSpan.FromSeconds(2), 5000);
        rate.Add(TimeSpan.FromSeconds(3), 0);

        // Not a negative rate from the drop, and not yet a second of the new attempt.
        Assert.Null(rate.BytesPerSecond);
    }
}
