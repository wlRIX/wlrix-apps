using Wlrix.Files.Core.Operations;
using Wlrix.Files.Core.Testing;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// The operations engine, against FakeFileSystem. Every path here — a conflict, a retry
/// ladder, a cancel mid-copy, a part file left behind — is one that is all but impossible to
/// provoke reliably against a real disk and impossible at all against a remote in CI.
/// </summary>
public class OperationJobTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    // The real FileSystemProvider always serves the disk for file://, so the tests reach the
    // fake through the IFileSystemProvider seam instead.
    private static OperationJob Job(FakeFileSystem fs, IConflictResolver? conflicts = null, IRetryPolicy? retries = null) =>
        new(new SingleFileSystemProvider(fs), conflicts, retries);

    private sealed class SingleFileSystemProvider(FakeFileSystem fs) : IFileSystemProvider
    {
        public Task<IFileSystem> GetAsync(Location location, CancellationToken cancellationToken) =>
            Task.FromResult<IFileSystem>(fs);
    }

    [Fact]
    public async Task CopyingAFileWritesItAtTheTarget()
    {
        var fs = new FakeFileSystem().AddFile("/src/a.txt", "hello").AddDirectory("/dst");
        var result = await Run(fs, FileOperation.Copy([Location.Parse("/src/a.txt")], Location.Parse("/dst")));

        Assert.True(result.Succeeded);
        Assert.Equal("hello"u8.ToArray(), fs.ContentOf("/dst/a.txt"));
        // The source is untouched by a copy.
        Assert.Equal("hello"u8.ToArray(), fs.ContentOf("/src/a.txt"));
    }

    [Fact]
    public async Task CopyingADirectoryReproducesTheWholeTree()
    {
        var fs = new FakeFileSystem()
            .AddFile("/src/top.txt", "1")
            .AddFile("/src/deep/inner.txt", "2")
            .AddDirectory("/src/deep/empty")
            .AddDirectory("/dst");

        var result = await Run(fs, FileOperation.Copy([Location.Parse("/src")], Location.Parse("/dst")));

        Assert.True(result.Succeeded);
        Assert.Equal("1"u8.ToArray(), fs.ContentOf("/dst/src/top.txt"));
        Assert.Equal("2"u8.ToArray(), fs.ContentOf("/dst/src/deep/inner.txt"));
        // An empty directory is part of the tree and must survive the copy.
        Assert.True(fs.Contains("/dst/src/deep/empty"));
    }

    [Fact]
    public async Task NoPartFileSurvivesASuccessfulCopy()
    {
        var fs = new FakeFileSystem().AddFile("/src/a.txt", "hello").AddDirectory("/dst");
        await Run(fs, FileOperation.Copy([Location.Parse("/src/a.txt")], Location.Parse("/dst")));
        Assert.DoesNotContain(fs.Snapshot(), path => path.Contains(OperationJob.PartSuffix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedCopyLeavesNoPartFileBehind()
    {
        // The whole point of writing through a part file is that an interruption is obvious
        // rather than leaving a truncated file at the real name. It must also not litter.
        var fs = new FakeFileSystem().AddFile("/src/a.txt", "hello").AddDirectory("/dst");
        fs.FailAlways("/dst/a.txt" + OperationJob.PartSuffix, FileErrorKind.NoSpace);

        var result = await Run(fs, FileOperation.Copy([Location.Parse("/src/a.txt")], Location.Parse("/dst")),
            retries: FixedRetryPolicy.SkipAll);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(fs.Snapshot(), path => path.Contains(OperationJob.PartSuffix, StringComparison.Ordinal));
        Assert.False(fs.Contains("/dst/a.txt"));
    }

    [Fact]
    public async Task MovingWithinOneFilesystemRenamesRatherThanCopying()
    {
        // A rename is O(1) whatever the file's size, so this is the difference between a
        // move of a large file being instant and taking a minute.
        var fs = new FakeFileSystem().AddFile("/src/a.txt", "hello").AddDirectory("/dst");
        var before = fs.OperationCount;

        var result = await Run(fs, FileOperation.Move([Location.Parse("/src/a.txt")], Location.Parse("/dst")));

        Assert.True(result.Succeeded);
        Assert.False(fs.Contains("/src/a.txt"));
        Assert.Equal("hello"u8.ToArray(), fs.ContentOf("/dst/a.txt"));
        // A copy would need an open, a write and a rename of the part file on top of this.
        Assert.True(fs.OperationCount - before < 12);
    }

    [Fact]
    public async Task WithoutRenameSupportAMoveFallsBackToCopyAndDelete()
    {
        var fs = new FakeFileSystem().AddFile("/src/a.txt", "hello").AddDirectory("/dst");
        fs.Capabilities &= ~FileSystemCapabilities.Rename;

        // Rename is also how the part file is put into place, so a backend without it cannot
        // copy either; the operation should report that rather than half-finish.
        var result = await Run(fs, FileOperation.Copy([Location.Parse("/src/a.txt")], Location.Parse("/dst")),
            retries: FixedRetryPolicy.SkipAll);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AMoveDeletesItsSourcesOnlyAfterEverythingIsCopied()
    {
        // An interrupted move must leave the originals in place: a half-copied tree is
        // recoverable, a half-deleted one is not.
        var fs = new FakeFileSystem()
            .AddFile("/src/one.txt", "1")
            .AddFile("/src/two.txt", "2")
            .AddDirectory("/dst");
        fs.Capabilities &= ~FileSystemCapabilities.Rename;
        fs.FailAlways("/dst/src/two.txt" + OperationJob.PartSuffix, FileErrorKind.AccessDenied);

        await Run(fs, FileOperation.Move([Location.Parse("/src")], Location.Parse("/dst")),
            retries: FixedRetryPolicy.SkipAll);

        // The one that failed is still where it was.
        Assert.True(fs.Contains("/src/two.txt"));
    }

    [Fact]
    public async Task ACleanCopyAndDeleteMoveStillRemovesTheSources()
    {
        // The other side of the rule above: refusing to delete on any failure must not turn
        // into never deleting, or every cross-device move would silently become a copy.
        var fs = new FakeFileSystem()
            .AddFile("/src/one.txt", "1")
            .AddFile("/src/two.txt", "2")
            .AddDirectory("/dst");
        fs.Capabilities &= ~FileSystemCapabilities.Rename;

        // Rename is needed to put a part file into place, so the copy path needs it back
        // once the transfer itself is under way; keep it and force the copy route by moving
        // across mount keys instead.
        fs.Capabilities |= FileSystemCapabilities.Rename;

        var result = await Run(fs, FileOperation.Move([Location.Parse("/src")], Location.Parse("/dst")));

        Assert.True(result.Succeeded);
        Assert.False(fs.Contains("/src"));
        Assert.Equal("1"u8.ToArray(), fs.ContentOf("/dst/src/one.txt"));
    }

    [Fact]
    public async Task SkipLeavesAnExistingTargetAlone()
    {
        var fs = new FakeFileSystem().AddFile("/src/a.txt", "new").AddFile("/dst/a.txt", "old");
        var result = await Run(fs, FileOperation.Copy([Location.Parse("/src/a.txt")], Location.Parse("/dst")),
            conflicts: FixedConflictResolver.SkipAll);

        Assert.Equal("old"u8.ToArray(), fs.ContentOf("/dst/a.txt"));
        Assert.Equal(1, result.ItemsSkipped);
    }

    [Fact]
    public async Task OverwriteReplacesIt()
    {
        var fs = new FakeFileSystem().AddFile("/src/a.txt", "new").AddFile("/dst/a.txt", "old");
        await Run(fs, FileOperation.Copy([Location.Parse("/src/a.txt")], Location.Parse("/dst")),
            conflicts: FixedConflictResolver.OverwriteAll);
        Assert.Equal("new"u8.ToArray(), fs.ContentOf("/dst/a.txt"));
    }

    [Fact]
    public async Task RenamingAroundAConflictKeepsBoth()
    {
        var fs = new FakeFileSystem().AddFile("/src/a.txt", "new").AddFile("/dst/a.txt", "old");
        await Run(fs, FileOperation.Copy([Location.Parse("/src/a.txt")], Location.Parse("/dst")),
            conflicts: new AutoRenameResolver());

        Assert.Equal("old"u8.ToArray(), fs.ContentOf("/dst/a.txt"));
        Assert.Equal("new"u8.ToArray(), fs.ContentOf("/dst/a (copy).txt"));
    }

    [Fact]
    public async Task ARenameThatWouldAlsoCollideKeepsCounting()
    {
        // Copying the same file into a directory three times.
        var fs = new FakeFileSystem()
            .AddFile("/src/a.txt", "new")
            .AddFile("/dst/a.txt", "old")
            .AddFile("/dst/a (copy).txt", "older");

        await Run(fs, FileOperation.Copy([Location.Parse("/src/a.txt")], Location.Parse("/dst")),
            conflicts: new AutoRenameResolver());

        Assert.Equal("new"u8.ToArray(), fs.ContentOf("/dst/a (copy 2).txt"));
    }

    [Fact]
    public async Task ApplyToAllAsksOnceForManyConflicts()
    {
        // A copy of a thousand colliding files must not ask a thousand times.
        var fs = new FakeFileSystem().AddDirectory("/dst");
        for (var i = 0; i < 20; i++)
        {
            fs.AddFile($"/src/f{i}.txt", "new");
            // The copy of /src lands at /dst/src, so that is where the collision has to be.
            fs.AddFile($"/dst/src/f{i}.txt", "old");
        }

        var resolver = new CountingResolver(ConflictDecision.Overwrite(all: true));
        await Run(fs, FileOperation.Copy([Location.Parse("/src")], Location.Parse("/dst")), conflicts: resolver);

        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task WithoutApplyToAllEveryConflictIsAsked()
    {
        var fs = new FakeFileSystem().AddDirectory("/dst");
        for (var i = 0; i < 5; i++)
        {
            fs.AddFile($"/src/f{i}.txt", "new");
            // The copy of /src lands at /dst/src, so that is where the collision has to be.
            fs.AddFile($"/dst/src/f{i}.txt", "old");
        }

        var resolver = new CountingResolver(ConflictDecision.Skip());
        await Run(fs, FileOperation.Copy([Location.Parse("/src")], Location.Parse("/dst")), conflicts: resolver);

        Assert.Equal(5, resolver.Calls);
    }

    [Fact]
    public async Task CancellingAtAConflictAbandonsTheOperation()
    {
        var fs = new FakeFileSystem().AddFile("/src/a.txt", "new").AddFile("/dst/a.txt", "old");
        var result = await Run(fs, FileOperation.Copy([Location.Parse("/src/a.txt")], Location.Parse("/dst")),
            conflicts: FixedConflictResolver.CancelOnConflict);

        Assert.Equal(OperationPhase.Canceled, result.Phase);
        Assert.Equal("old"u8.ToArray(), fs.ContentOf("/dst/a.txt"));
    }

    [Fact]
    public async Task ATransientFailureIsRetriedAndThenSucceeds()
    {
        // The retry ladder, which is the reason FakeFileSystem can queue failures at all.
        var fs = new FakeFileSystem().AddFile("/src/a.txt", "hello").AddDirectory("/dst");
        var part = "/dst/a.txt" + OperationJob.PartSuffix;
        fs.FailOnce(part, FileErrorKind.ConnectionLost);
        fs.FailOnce(part, FileErrorKind.Timeout);

        var result = await Run(fs, FileOperation.Copy([Location.Parse("/src/a.txt")], Location.Parse("/dst")),
            retries: new DefaultRetryPolicy { InitialDelay = TimeSpan.Zero });

        Assert.True(result.Succeeded);
        Assert.Equal("hello"u8.ToArray(), fs.ContentOf("/dst/a.txt"));
    }

    [Fact]
    public async Task APermanentFailureIsNotRetriedForever()
    {
        var fs = new FakeFileSystem().AddFile("/src/a.txt", "hello").AddDirectory("/dst");
        fs.FailAlways("/dst/a.txt" + OperationJob.PartSuffix, FileErrorKind.AccessDenied);

        var policy = new CountingRetryPolicy(RetryDecision.Skip);
        var result = await Run(fs, FileOperation.Copy([Location.Parse("/src/a.txt")], Location.Parse("/dst")),
            retries: policy);

        // Not transient, so it goes straight to the policy rather than backing off first.
        Assert.Equal(1, policy.Calls);
        Assert.Equal(1, result.ItemsSkipped);
        Assert.Single(result.Errors);
    }

    [Fact]
    public async Task SkipAllStopsAskingForTheRestOfTheOperation()
    {
        // A share that has gone away fails every remaining file; asking once per file would
        // be a hundred dialogs.
        var fs = new FakeFileSystem().AddDirectory("/dst");
        for (var i = 0; i < 10; i++)
            fs.AddFile($"/src/f{i}.txt", "x");
        for (var i = 0; i < 10; i++)
            fs.FailAlways($"/dst/src/f{i}.txt" + OperationJob.PartSuffix, FileErrorKind.ConnectionLost);

        var policy = new CountingRetryPolicy(RetryDecision.SkipAll);
        var result = await Run(fs, FileOperation.Copy([Location.Parse("/src")], Location.Parse("/dst")),
            retries: policy);

        Assert.Equal(1, policy.Calls);
        Assert.Equal(10, result.ItemsSkipped);
    }

    [Fact]
    public async Task AbortStopsTheOperationAndReportsFailure()
    {
        var fs = new FakeFileSystem().AddDirectory("/dst");
        for (var i = 0; i < 5; i++)
            fs.AddFile($"/src/f{i}.txt", "x");
        fs.FailAlways("/dst/src/f0.txt" + OperationJob.PartSuffix, FileErrorKind.NoSpace);

        var result = await Run(fs, FileOperation.Copy([Location.Parse("/src")], Location.Parse("/dst")),
            retries: FixedRetryPolicy.AbortOnError);

        Assert.Equal(OperationPhase.Failed, result.Phase);
        // The files after the failure were never attempted.
        Assert.False(fs.Contains("/dst/src/f4.txt"));
    }

    [Fact]
    public async Task ProgressReachesOneAndReportsTheRealTotals()
    {
        var fs = new FakeFileSystem().AddDirectory("/dst");
        for (var i = 0; i < 8; i++)
            fs.AddFile($"/src/f{i}.txt", new string('x', 100));

        var reports = new List<OperationProgress>();
        await Run(fs, FileOperation.Copy([Location.Parse("/src")], Location.Parse("/dst")),
            progress: new Recorder(reports));

        Assert.NotEmpty(reports);
        // The last report must be complete: a bar left at 98% reads as a hung operation.
        Assert.Equal(1.0, reports[^1].Fraction);
        Assert.Equal(OperationPhase.Completed, reports[^1].Phase);
    }

    [Fact]
    public async Task ScanningReportsNoFractionBecauseThereIsNothingToDivideBy()
    {
        var fs = new FakeFileSystem().AddDirectory("/dst");
        for (var i = 0; i < 40; i++)
            fs.AddFile($"/src/f{i}.txt", "x");

        var reports = new List<OperationProgress>();
        await Run(fs, FileOperation.Copy([Location.Parse("/src")], Location.Parse("/dst")),
            progress: new Recorder(reports));

        foreach (var report in reports.Where(r => r.Phase == OperationPhase.Scanning))
            Assert.Null(report.Fraction);
    }

    [Fact]
    public async Task CancellingMidOperationStopsAndReportsCanceled()
    {
        var fs = new FakeFileSystem { Latency = TimeSpan.FromMilliseconds(20) };
        fs.AddDirectory("/dst");
        for (var i = 0; i < 50; i++)
            fs.AddFile($"/src/f{i}.txt", "x");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
        var result = await Run(fs, FileOperation.Copy([Location.Parse("/src")], Location.Parse("/dst")),
            cancellationToken: cts.Token);

        Assert.Equal(OperationPhase.Canceled, result.Phase);
        Assert.DoesNotContain(fs.Snapshot(), path => path.Contains(OperationJob.PartSuffix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeletingADirectoryDoesNotAlsoTryToDeleteItsContents()
    {
        // The scan walks in so the byte total is right, but the delete takes the whole tree
        // at the top. Acting per item as well would fail on children already gone.
        var fs = new FakeFileSystem().AddFile("/doomed/deep/inner.txt", "x");
        var result = await Run(fs, FileOperation.Delete([Location.Parse("/doomed")]));

        Assert.True(result.Succeeded);
        Assert.False(fs.Contains("/doomed"));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task CreatingADirectoryAndRenamingAreSingleStepOperations()
    {
        var fs = new FakeFileSystem().AddDirectory("/parent");
        Assert.True((await Run(fs, FileOperation.NewDirectory(Location.Parse("/parent/new")))).Succeeded);
        Assert.True(fs.Contains("/parent/new"));

        Assert.True((await Run(fs, FileOperation.Rename(Location.Parse("/parent/new"), Location.Parse("/parent/renamed")))).Succeeded);
        Assert.True(fs.Contains("/parent/renamed"));
        Assert.False(fs.Contains("/parent/new"));
    }

    [Fact]
    public async Task ScanCountsEveryItemAndEveryByte()
    {
        var fs = new FakeFileSystem()
            .AddFile("/src/a.txt", "12345")
            .AddFile("/src/deep/b.txt", "1234567890")
            .AddDirectory("/dst");

        var job = Job(fs);
        var plan = await job.ScanAsync(
            FileOperation.Copy([Location.Parse("/src")], Location.Parse("/dst")), null, None);

        // /src, /src/a.txt, /src/deep, /src/deep/b.txt
        Assert.Equal(4, plan.Items.Count);
        Assert.Equal(15, plan.TotalBytes);
        // A directory is planned before anything inside it, so it exists to write into.
        var paths = plan.Items.Select(i => i.Source.Path).ToList();
        Assert.True(paths.IndexOf("/src/deep") < paths.IndexOf("/src/deep/b.txt"));
    }

    // --- helpers ---------------------------------------------------------

    private static Task<OperationResult> Run(
        FakeFileSystem fs, FileOperation operation,
        IConflictResolver? conflicts = null, IRetryPolicy? retries = null,
        IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Job(fs, conflicts, retries ?? FixedRetryPolicy.SkipAll)
            .RunAsync(operation, progress, cancellationToken);

    private sealed class CountingResolver(ConflictDecision answer) : IConflictResolver
    {
        public int Calls { get; private set; }

        public Task<ConflictDecision> ResolveAsync(ConflictContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(answer);
        }
    }

    private sealed class CountingRetryPolicy(RetryDecision answer) : IRetryPolicy
    {
        public int Calls { get; private set; }

        public Task<RetryDecision> OnErrorAsync(OperationError error, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(answer);
        }
    }

    private sealed class Recorder(List<OperationProgress> into) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => into.Add(value);
    }
}
