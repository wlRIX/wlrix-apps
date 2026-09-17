using Wlrix.Files.Core.Metadata;
using Wlrix.Files.Core.Testing;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// Adding up a directory, which is the one thing a properties dialog cannot get from stat.
/// </summary>
public class DirectoryUsageTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private sealed class SingleFileSystemProvider(FakeFileSystem fs) : IFileSystemProvider
    {
        public Task<IFileSystem> GetAsync(Location location, CancellationToken cancellationToken) =>
            Task.FromResult<IFileSystem>(fs);
    }

    private static Task<DirectorySize> Measure(
        FakeFileSystem fs, string path, IProgress<DirectorySize>? progress = null, CancellationToken? token = null) =>
        DirectoryUsage.MeasureAsync(
            new SingleFileSystemProvider(fs), [Location.Parse(path)], progress, token ?? None);

    [Fact]
    public async Task AWholeTreeIsCountedAndNotJustItsTopLevel()
    {
        // The reason this exists at all: a directory's own stat reports the size of its
        // index, so a folder holding ten megabytes says 4096 bytes.
        var fs = new FakeFileSystem()
            .AddFile("/photos/a.jpg", new byte[1000])
            .AddFile("/photos/b.jpg", new byte[2000])
            .AddFile("/photos/2019/c.jpg", new byte[3000])
            .AddFile("/photos/2019/trip/d.jpg", new byte[4000])
            .AddDirectory("/photos/empty");

        var size = await Measure(fs, "/photos");

        Assert.True(size.Complete);
        Assert.Equal(4, size.Files);
        Assert.Equal(3, size.Directories);
        Assert.Equal(7, size.Items);
        Assert.Equal(10000, size.Bytes);
        Assert.Equal(0, size.Unreadable);
    }

    [Fact]
    public async Task TheDirectoryAskedAboutIsNotCountedAsOneOfItsOwnContents()
    {
        // "3 items" has to mean three things inside, not four.
        var fs = new FakeFileSystem()
            .AddFile("/d/a", "1")
            .AddFile("/d/b", "2")
            .AddDirectory("/d/c");

        var size = await Measure(fs, "/d");
        Assert.Equal(3, size.Items);
    }

    [Fact]
    public async Task AskingAboutAPlainFileAnswersJustThatFile()
    {
        // The dialog uses one call for whatever is selected, so a single file has to be a
        // legitimate thing to ask about rather than a special case at the call site.
        var fs = new FakeFileSystem().AddFile("/notes.txt", new byte[42]);

        var size = await Measure(fs, "/notes.txt");
        Assert.Equal(1, size.Files);
        Assert.Equal(0, size.Directories);
        Assert.Equal(42, size.Bytes);
    }

    [Fact]
    public async Task AnEmptyDirectoryIsZeroAndComplete()
    {
        var fs = new FakeFileSystem().AddDirectory("/empty");
        var size = await Measure(fs, "/empty");

        Assert.True(size.Complete);
        Assert.Equal(0, size.Items);
        Assert.Equal(0, size.Bytes);
    }

    [Fact]
    public async Task SeveralSelectedThingsAreAddedTogether()
    {
        var fs = new FakeFileSystem()
            .AddFile("/a/one.txt", new byte[100])
            .AddFile("/b/two.txt", new byte[200])
            .AddFile("/loose.txt", new byte[300]);

        var size = await DirectoryUsage.MeasureAsync(
            new SingleFileSystemProvider(fs),
            [Location.Parse("/a"), Location.Parse("/b"), Location.Parse("/loose.txt")],
            null,
            None);

        Assert.Equal(3, size.Files);
        Assert.Equal(600, size.Bytes);
    }

    [Fact]
    public async Task ALinkCountsAsOneEntryAndIsNotFollowed()
    {
        // Following would count the target twice when it is inside the tree, and never
        // finish at all when two directories point at each other.
        var fs = new FakeFileSystem();
        fs.Capabilities |= FileSystemCapabilities.SymLink;
        fs.AddFile("/tree/real/file.txt", new byte[500]);
        await fs.CreateSymlinkAsync(Location.Parse("/tree/loop"), "/tree", None);

        var size = await Measure(fs, "/tree");

        Assert.True(size.Complete);
        Assert.Equal(2, size.Files);          // file.txt and the link itself
        Assert.Equal(1, size.Directories);    // real/
        Assert.Equal(500, size.Bytes);
    }

    [Fact]
    public async Task AnUnreadableDirectoryIsCountedAndSkippedRatherThanAbortingTheWalk()
    {
        // A home directory usually has one corner nobody can read. Reporting a lower bound
        // for the rest is useful; reporting nothing is not.
        var fs = new FakeFileSystem()
            .AddFile("/home/open/a", new byte[10])
            .AddFile("/home/locked/b", new byte[9999]);
        fs.FailAlways("/home/locked", FileErrorKind.AccessDenied);

        var size = await Measure(fs, "/home");

        Assert.True(size.Complete);
        Assert.Equal(1, size.Unreadable);
        Assert.Equal(10, size.Bytes);
        // The locked directory itself was still seen, and counted.
        Assert.Equal(2, size.Directories);
    }

    [Fact]
    public async Task TheRunningTotalIsReportedWhileTheWalkIsStillGoing()
    {
        // What makes the dialog open immediately with a number that climbs, rather than
        // sitting empty until a large tree finishes.
        var fs = new FakeFileSystem();
        for (var i = 0; i < 20; i++)
            fs.AddFile($"/many/f{i}", new byte[10]);

        var reports = new List<DirectorySize>();
        var size = await Measure(fs, "/many", new SynchronousProgress(reports.Add));

        Assert.Equal(20, reports.Count);
        Assert.Equal(200, size.Bytes);

        // Every report is a running total, so the byte count never goes backwards...
        for (var i = 1; i < reports.Count; i++)
            Assert.True(reports[i].Bytes >= reports[i - 1].Bytes);

        // ...and none of them claims to be the answer. Complete is the return value's word
        // alone, so a caller cannot mistake the last report for the total.
        Assert.All(reports, report => Assert.False(report.Complete));
    }

    [Fact]
    public async Task CancelingStopsTheWalkRatherThanFinishingItQuietly()
    {
        var fs = new FakeFileSystem();
        for (var i = 0; i < 200; i++)
            fs.AddFile($"/big/f{i}", new byte[1]);

        using var cts = new CancellationTokenSource();
        var seen = 0;
        var progress = new SynchronousProgress(_ =>
        {
            if (++seen == 5)
                cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Measure(fs, "/big", progress, cts.Token));
    }

    /// <summary>
    /// An <see cref="IProgress{T}"/> that calls back on the reporting thread.
    /// </summary>
    /// <remarks>
    /// <see cref="Progress{T}"/> posts to a synchronization context, so in a test its reports
    /// arrive after the assertion that was waiting for them. This one is the whole reason
    /// the count above is exact rather than "at least one".
    /// </remarks>
    private sealed class SynchronousProgress(Action<DirectorySize> report) : IProgress<DirectorySize>
    {
        public void Report(DirectorySize value) => report(value);
    }
}
