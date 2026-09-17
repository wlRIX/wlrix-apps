using Wlrix.Files.Core.Filesystems;
using Wlrix.Files.Core.Testing;
using Wlrix.Files.Core.Watching;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// Noticing that a directory has moved on.
/// </summary>
/// <remarks>
/// The comparison is the whole of it and is shared by both watchers, so most of this is about
/// the diff rather than about timers or inotify — which is the point of making the events a
/// trigger to re-read rather than a description of what changed.
/// </remarks>
public class DirectoryWatcherTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Location Dir = Location.Parse("/watched");

    private static FileEntry Entry(string name, long size = 10, string modified = "2026-01-01", bool directory = false) =>
        new()
        {
            Location = Dir.Child(name),
            Name = name,
            Kind = directory ? FileKind.Directory : FileKind.File,
            Size = size,
            Modified = DateTimeOffset.Parse(modified, System.Globalization.CultureInfo.InvariantCulture)
        };

    // --- the comparison ---------------------------------------------------

    [Fact]
    public void AnEmptySnapshotReportsEverythingAsNew()
    {
        var changes = DirectorySnapshot.Empty.DiffTo(Dir, [Entry("a"), Entry("b")], out var after);

        Assert.Equal(2, changes.Count);
        Assert.All(changes, change => Assert.Equal(DirectoryChangeKind.Added, change.Kind));
        Assert.Equal(2, after.Count);
    }

    [Fact]
    public void AnUnchangedDirectoryReportsNothingAtAll()
    {
        // The common case by a wide margin: a poll every ten seconds against a share nobody is
        // touching must produce no work and no notification.
        var entries = new[] { Entry("a"), Entry("b"), Entry("c") };
        var snapshot = DirectorySnapshot.Of(entries);

        Assert.Empty(snapshot.DiffTo(Dir, entries, out _));
    }

    [Fact]
    public void AddedRemovedAndChangedAreEachReportedOnce()
    {
        var snapshot = DirectorySnapshot.Of([Entry("keep"), Entry("gone"), Entry("grows", size: 10)]);
        var changes = snapshot.DiffTo(Dir,
            [Entry("keep"), Entry("grows", size: 4096), Entry("new")], out _);

        Assert.Equal(3, changes.Count);
        Assert.Contains(changes, c => c.Kind == DirectoryChangeKind.Added && c.Location.Name == "new");
        Assert.Contains(changes, c => c.Kind == DirectoryChangeKind.Removed && c.Location.Name == "gone");
        Assert.Contains(changes, c => c.Kind == DirectoryChangeKind.Changed && c.Location.Name == "grows");
    }

    [Fact]
    public void ARemovedEntryStillGetsTheRightLocation()
    {
        // It is by definition not in the new listing, so its location cannot be read out of
        // one. Reconstructing it from a surviving neighbor would break on the case below.
        var snapshot = DirectorySnapshot.Of([Entry("only")]);
        var changes = snapshot.DiffTo(Dir, [], out var after);

        var removed = Assert.Single(changes);
        Assert.Equal(DirectoryChangeKind.Removed, removed.Kind);
        Assert.Equal("/watched/only", removed.Location.Path);
        Assert.Equal(0, after.Count);
    }

    [Fact]
    public void ATouchedFileIsAChangeEvenAtTheSameSize()
    {
        var snapshot = DirectorySnapshot.Of([Entry("a", size: 10, modified: "2026-01-01")]);
        var changes = snapshot.DiffTo(Dir, [Entry("a", size: 10, modified: "2026-06-01")], out _);

        Assert.Equal(DirectoryChangeKind.Changed, Assert.Single(changes).Kind);
    }

    [Fact]
    public void SomethingReplacedByADirectoryOfTheSameNameIsAChange()
    {
        // Same name, same size, same timestamp, different thing entirely. Comparing only size
        // and time would show a file where there is now a folder.
        var snapshot = DirectorySnapshot.Of([Entry("thing", size: 0)]);
        var changes = snapshot.DiffTo(Dir, [Entry("thing", size: 0, directory: true)], out _);

        Assert.Equal(DirectoryChangeKind.Changed, Assert.Single(changes).Kind);
    }

    [Fact]
    public void ARenameIsReportedAsARemovalAndAnAddition()
    {
        // Which is what a listing needs. Stitching the two halves of an inotify rename back
        // together by cookie would produce a tidier event and exactly the same redraw.
        var snapshot = DirectorySnapshot.Of([Entry("before")]);
        var changes = snapshot.DiffTo(Dir, [Entry("after")], out _);

        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.Kind == DirectoryChangeKind.Removed && c.Location.Name == "before");
        Assert.Contains(changes, c => c.Kind == DirectoryChangeKind.Added && c.Location.Name == "after");
    }

    [Fact]
    public void AnAdditionCarriesTheEntryAndARemovalDoesNot()
    {
        var changes = DirectorySnapshot.Of([Entry("gone")]).DiffTo(Dir, [Entry("new")], out _);

        Assert.NotNull(changes.Single(c => c.Kind == DirectoryChangeKind.Added).Entry);
        // There is nothing left to describe.
        Assert.Null(changes.Single(c => c.Kind == DirectoryChangeKind.Removed).Entry);
    }

    // --- the polling watcher ----------------------------------------------

    private static (PollingWatcher, FakeFileSystem, List<DirectoryChange>) Watching(
        Action<FakeFileSystem> build, TimeSpan? interval = null)
    {
        var fs = new FakeFileSystem();
        fs.AddDirectory("/watched");
        build(fs);
        var watcher = new PollingWatcher(fs, Dir, interval);
        var seen = new List<DirectoryChange>();
        watcher.Changed += changes => { lock (seen) seen.AddRange(changes); };
        return (watcher, fs, seen);
    }

    [Fact]
    public async Task StartingAWatcherOnAListingAlreadyReadReportsNothing()
    {
        // Seeded from what the pane read. Without that, opening any directory would
        // immediately report every file in it as newly added.
        var (watcher, fs, seen) = Watching(f => f.AddFile("/watched/a.txt", "x").AddFile("/watched/b.txt", "y"));
        await using var _ = watcher;

        var known = new List<FileEntry>();
        await foreach (var entry in fs.EnumerateAsync(Dir, None))
            known.Add(entry);

        await watcher.StartAsync(known, None);
        await watcher.RefreshAsync(None);

        Assert.Empty(seen);
        Assert.Equal(2, watcher.Known);
    }

    [Fact]
    public async Task AFileAppearingOnTheServerIsNoticed()
    {
        var (watcher, fs, seen) = Watching(f => f.AddFile("/watched/a.txt", "x"));
        await using var _ = watcher;
        await watcher.StartAsync([.. await ListAsync(fs)], None);

        fs.AddFile("/watched/new.txt", "hello");
        await watcher.RefreshAsync(None);

        var change = Assert.Single(seen);
        Assert.Equal(DirectoryChangeKind.Added, change.Kind);
        Assert.Equal("new.txt", change.Location.Name);
    }

    [Fact]
    public async Task TheIntervalDoesTheSameThingWithoutBeingAsked()
    {
        // The whole point for a share: nobody prompts it, and it still catches up.
        var (watcher, fs, seen) = Watching(
            f => f.AddFile("/watched/a.txt", "x"), TimeSpan.FromMilliseconds(20));
        await using var _ = watcher;
        await watcher.StartAsync([.. await ListAsync(fs)], None);

        fs.AddFile("/watched/appeared.txt", "hello");
        await WaitFor(() => { lock (seen) return seen.Count > 0; });

        lock (seen)
            Assert.Contains(seen, c => c.Location.Name == "appeared.txt");
    }

    [Fact]
    public async Task APausedWatcherLeavesTheServerAlone()
    {
        // A window nobody is looking at should not spend the week talking to a NAS.
        var (watcher, fs, seen) = Watching(
            f => f.AddFile("/watched/a.txt", "x"), TimeSpan.FromMilliseconds(20));
        await using var _ = watcher;
        await watcher.StartAsync([.. await ListAsync(fs)], None);

        watcher.Paused = true;
        fs.AddFile("/watched/appeared.txt", "hello");
        await Task.Delay(150);
        lock (seen)
            Assert.Empty(seen);

        // ...and catches up in one scan when the window comes back.
        watcher.Paused = false;
        await WaitFor(() => { lock (seen) return seen.Count > 0; });
    }

    [Fact]
    public async Task RefreshingWorksEvenWhilePaused()
    {
        // What an operation calls when it finishes. A paste onto a share must show up at once
        // rather than waiting for a poll that is suspended.
        var (watcher, fs, seen) = Watching(f => f.AddFile("/watched/a.txt", "x"));
        await using var _ = watcher;
        await watcher.StartAsync([.. await ListAsync(fs)], None);

        watcher.Paused = true;
        fs.AddFile("/watched/pasted.txt", "hello");
        await watcher.RefreshAsync(None);

        lock (seen)
            Assert.Contains(seen, c => c.Location.Name == "pasted.txt");
    }

    [Fact]
    public async Task ADirectoryThatVanishesDoesNotThrowOutOfTheWatcher()
    {
        // It runs on a timer with nobody to catch for it, so a throw here is an unobserved
        // task exception and the process goes down with it.
        var (watcher, fs, seen) = Watching(f => f.AddFile("/watched/a.txt", "x"));
        await using var _ = watcher;
        await watcher.StartAsync([.. await ListAsync(fs)], None);

        await fs.DeleteAsync(Dir, recursive: true, None);
        await watcher.RefreshAsync(None);

        lock (seen)
            Assert.Empty(seen);
    }

    [Fact]
    public async Task AWatcherStopsLookingOnceItIsDisposed()
    {
        var (watcher, fs, seen) = Watching(
            f => f.AddFile("/watched/a.txt", "x"), TimeSpan.FromMilliseconds(20));
        await watcher.StartAsync([.. await ListAsync(fs)], None);
        await watcher.DisposeAsync();

        fs.AddFile("/watched/after.txt", "hello");
        await Task.Delay(150);

        lock (seen)
            Assert.Empty(seen);
    }

    // --- picking one ------------------------------------------------------

    [Fact]
    public void ALocalDirectoryGetsInotifyAndARemoteOneGetsPolling()
    {
        // By capability rather than by scheme, so a backend that grows change notification is
        // picked up without this method learning its name.
        using var dir = new TemporaryDirectory();
        Assert.IsType<InotifyWatcher>(DirectoryWatchers.For(new LocalFileSystem(), dir.Location));

        var remote = new FakeFileSystem("smb://nas");
        Assert.IsType<PollingWatcher>(
            DirectoryWatchers.For(remote, Location.Parse("smb://nas/share")));
    }

    [Fact]
    public void AFilesystemWithoutTheWatchCapabilityPollsEvenWhenItIsLocal()
    {
        var fs = new FakeFileSystem();
        Assert.False(fs.Capabilities.HasFlag(FileSystemCapabilities.Watch));
        Assert.IsType<PollingWatcher>(DirectoryWatchers.For(fs, Dir));
    }

    [Fact]
    public async Task InotifyNoticesARealFileAppearing()
    {
        // The one test that goes near the kernel. Everything above it is the shared diff.
        using var dir = new TemporaryDirectory();
        dir.File("existing.txt", "x");

        var fs = new LocalFileSystem();
        await using var watcher = new InotifyWatcher(fs, dir.Location, TimeSpan.FromMilliseconds(20));
        var seen = new List<DirectoryChange>();
        watcher.Changed += changes => { lock (seen) seen.AddRange(changes); };
        await watcher.StartAsync([.. await ListAsync(fs, dir.Location)], None);

        dir.File("appeared.txt", "hello");
        await WaitFor(() => { lock (seen) return seen.Any(c => c.Location.Name == "appeared.txt"); });
    }

    private static async Task<List<FileEntry>> ListAsync(IFileSystem fs, Location? at = null)
    {
        var entries = new List<FileEntry>();
        await foreach (var entry in fs.EnumerateAsync(at ?? Dir, None))
            entries.Add(entry);
        return entries;
    }

    /// <summary>Waits for a condition, so a timing test is not a fixed sleep.</summary>
    private static async Task WaitFor(Func<bool> condition, int millisecondsTimeout = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(millisecondsTimeout);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }
        Assert.Fail($"the condition was still false after {millisecondsTimeout} ms");
    }
}
