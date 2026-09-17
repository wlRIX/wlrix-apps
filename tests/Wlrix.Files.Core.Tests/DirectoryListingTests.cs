using Wlrix.Files.Core.Filesystems;
using Wlrix.Files.Core.Listing;
using Wlrix.Files.Core.Testing;
using Xunit;

namespace Wlrix.Files.Core.Tests;

public class DirectoryListingTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task ALargeDirectoryArrivesAsManyBatchesRatherThanOneLump()
    {
        // The whole point of the pipeline: the first screenful must be renderable long
        // before the last entry is read.
        var fs = new FakeFileSystem().AddFiles("/big", 5000);
        var listing = new DirectoryListing(fs, Location.Parse("/big"));

        var batches = new List<ListingBatch>();
        await foreach (var batch in listing.ReadAsync(None))
            batches.Add(batch);

        Assert.True(batches.Count >= 5000 / DirectoryListing.BatchSize,
            $"expected at least {5000 / DirectoryListing.BatchSize} batches, got {batches.Count}");
        Assert.Equal(5000, batches.Sum(b => b.Entries.Count));
        Assert.Equal(5000, batches[^1].TotalSoFar);
        Assert.Equal(ListingState.Complete, listing.State);
    }

    [Fact]
    public async Task NoBatchExceedsTheBatchSize()
    {
        var fs = new FakeFileSystem().AddFiles("/big", 2000);
        var listing = new DirectoryListing(fs, Location.Parse("/big"));
        await foreach (var batch in listing.ReadAsync(None))
            Assert.True(batch.Entries.Count <= DirectoryListing.BatchSize);
    }

    [Fact]
    public async Task TotalSoFarIsCumulativeSoAStatusBarCanCountUp()
    {
        var fs = new FakeFileSystem().AddFiles("/big", 1500);
        var listing = new DirectoryListing(fs, Location.Parse("/big"));

        var running = 0;
        await foreach (var batch in listing.ReadAsync(None))
        {
            running += batch.Entries.Count;
            Assert.Equal(running, batch.TotalSoFar);
        }
    }

    [Fact]
    public async Task ASmallDirectoryStillArrives()
    {
        // Below the batch size there is nothing to flush on count, so this only works
        // because the producer flushes what is left when the enumeration ends.
        var fs = new FakeFileSystem().AddFile("/small/one.txt").AddFile("/small/two.txt");
        var entries = await new DirectoryListing(fs, Location.Parse("/small")).ReadAllAsync(None);
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task AnEmptyDirectoryCompletesWithNoBatches()
    {
        var fs = new FakeFileSystem().AddDirectory("/empty");
        var listing = new DirectoryListing(fs, Location.Parse("/empty"));
        var batches = 0;
        await foreach (var _ in listing.ReadAsync(None))
            batches++;
        Assert.Equal(0, batches);
        Assert.Equal(ListingState.Complete, listing.State);
    }

    [Fact]
    public async Task AFailureIsRecordedRatherThanThrownOutOfTheStream()
    {
        // A listing that vanishes on its last entry is worse than one that shows what
        // it read with the error beside it.
        var fs = new FakeFileSystem();
        fs.FailAlways("/denied", FileErrorKind.AccessDenied);
        fs.AddDirectory("/denied");
        var listing = new DirectoryListing(fs, Location.Parse("/denied"));

        await foreach (var _ in listing.ReadAsync(None))
        {
        }

        Assert.Equal(ListingState.Failed, listing.State);
        Assert.NotNull(listing.Error);
        Assert.Equal(FileErrorKind.AccessDenied, listing.Error!.Kind);
    }

    [Fact]
    public async Task ReadAllAsyncRethrowsWhereTheStreamingFormRecords()
    {
        var fs = new FakeFileSystem();
        fs.FailAlways("/denied", FileErrorKind.AccessDenied);
        fs.AddDirectory("/denied");
        var listing = new DirectoryListing(fs, Location.Parse("/denied"));
        var ex = await Assert.ThrowsAsync<FileOperationException>(() => listing.ReadAllAsync(None));
        Assert.Equal(FileErrorKind.AccessDenied, ex.Kind);
    }

    [Fact]
    public async Task CancellingMidReadKeepsWhatArrivedAndReportsCanceled()
    {
        var fs = new FakeFileSystem().AddFiles("/big", 20000);
        var listing = new DirectoryListing(fs, Location.Parse("/big"));
        using var cts = new CancellationTokenSource();

        var seen = 0;
        await foreach (var batch in listing.ReadAsync(cts.Token))
        {
            seen += batch.Entries.Count;
            if (seen >= DirectoryListing.BatchSize * 2)
                await cts.CancelAsync();
        }

        Assert.Equal(ListingState.Canceled, listing.State);
        Assert.Null(listing.Error);
        Assert.True(seen < 20000, "cancellation should have stopped the read short");
    }

    [Fact]
    public async Task AnExceptionTheBackendShouldNotHaveThrownIsWrappedNotPropagated()
    {
        // IFileSystem says only FileOperationException escapes. A backend that breaks
        // that must not take the app down, but the original has to stay visible.
        var listing = new DirectoryListing(new MisbehavingFileSystem(), Location.Parse("/x"));
        await foreach (var _ in listing.ReadAsync(None))
        {
        }
        Assert.Equal(ListingState.Failed, listing.State);
        Assert.IsType<InvalidTimeZoneException>(listing.Error!.InnerException);
    }

    [Fact]
    public async Task TheRealFilesystemStreamsThroughTheSamePipeline()
    {
        using var dir = new TemporaryDirectory();
        for (var i = 0; i < 1200; i++)
            dir.File($"f{i:D5}", "");

        var listing = new DirectoryListing(new LocalFileSystem(), dir.Location);
        var entries = await listing.ReadAllAsync(None);
        Assert.Equal(1200, entries.Count);
        Assert.Equal(ListingState.Complete, listing.State);
    }

    private sealed class MisbehavingFileSystem : IFileSystem
    {
        public string MountKey => "file://";
        public FileSystemCapabilities Capabilities => FileSystemCapabilities.None;

        public async IAsyncEnumerable<FileEntry> EnumerateAsync(
            Location location, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new InvalidTimeZoneException("a backend leaking its own exception type");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public Task<FileStat> StatAsync(Location l, CancellationToken c) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(Location l, CancellationToken c) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(Location l, CancellationToken c) => throw new NotSupportedException();
        public Task<Stream> OpenWriteAsync(Location l, WriteMode m, long? n, CancellationToken c) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(Location l, CancellationToken c) => throw new NotSupportedException();
        public Task DeleteAsync(Location l, bool r, CancellationToken c) => throw new NotSupportedException();
        public Task RenameAsync(Location f, Location t, CancellationToken c) => throw new NotSupportedException();
        public Task CreateSymlinkAsync(Location l, string t, CancellationToken c) => throw new NotSupportedException();
        public Task SetModifiedAsync(Location l, DateTimeOffset m, CancellationToken c) => throw new NotSupportedException();
        public Task SetUnixModeAsync(Location l, int m, CancellationToken c) => throw new NotSupportedException();
        public Task<FreeSpace?> GetFreeSpaceAsync(Location l, CancellationToken c) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
