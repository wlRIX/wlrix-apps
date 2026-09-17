using Wlrix.Files.Core.State;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// What the file manager remembers between runs. Every one of these degrades rather than
/// throws, so the tests care as much about what happens to a bad file as to a good one.
/// </summary>
public class FilesStateStoreTests
{
    private static readonly Location Home = Location.Parse("/home/vic");
    private static readonly Location Downloads = Location.Parse("/home/vic/Downloads");

    [Fact]
    public void AFirstRunFindsNothingAndUsesTheDefaults()
    {
        using var dir = new TemporaryDirectory();
        var store = new FilesStateStore(dir.Path);

        Assert.Empty(store.Bookmarks);
        Assert.Empty(store.Shares);
        Assert.Empty(store.Session);
        Assert.True(store.Preferences.ThumbnailsEnabled);
        Assert.False(store.Preferences.ShowHidden);
    }

    [Fact]
    public void AFileWrittenBeforeTheSettingsMovedIsStillRead()
    {
        // NavigationMode and IconTheme were here until they were promoted into files.toml.
        // A preferences.json written before that still carries them, and they are dropped
        // rather than migrated: migrating would mean writing a config file from here, which is
        // the one thing wlrix-settings-daemon exists to stop.
        using var dir = new TemporaryDirectory();
        File.WriteAllText(
            Path.Combine(dir.Path, "preferences.json"),
            $$"""{"Version": {{FilesStateStore.CurrentVersion}}, "NavigationMode": "Classic", "IconTheme": "Papirus", "ShowHidden": true}""");

        var warnings = new List<string>();
        var store = new FilesStateStore(dir.Path, warnings.Add);

        Assert.True(store.Preferences.ShowHidden);
        Assert.Empty(warnings);
    }

    [Fact]
    public void PreferencesSurviveARestart()
    {
        using var dir = new TemporaryDirectory();
        new FilesStateStore(dir.Path).Preferences = new FilesPreferences
        {
            ShowHidden = true,
            SingleClickOpen = true
        };

        var reopened = new FilesStateStore(dir.Path);
        Assert.True(reopened.Preferences.ShowHidden);
        Assert.True(reopened.Preferences.SingleClickOpen);
    }

    [Fact]
    public void BookmarksKeepTheOrderTheyWereGiven()
    {
        // The rail is arranged by hand, so this is an ordered list and not a set that happens
        // to be rendered in some order.
        using var dir = new TemporaryDirectory();
        var store = new FilesStateStore(dir.Path);
        store.AddBookmark(Downloads, "Downloads");
        store.AddBookmark(Home, "Home");
        store.AddBookmark(Location.Parse("/etc"), "Etc");

        Assert.Equal(["Downloads", "Home", "Etc"],
            new FilesStateStore(dir.Path).Bookmarks.Select(b => b.Label));
    }

    [Fact]
    public void TheSameLocationIsNotBookmarkedTwice()
    {
        using var dir = new TemporaryDirectory();
        var store = new FilesStateStore(dir.Path);

        Assert.True(store.AddBookmark(Downloads));
        Assert.False(store.AddBookmark(Downloads, "a different label"));
        Assert.Single(store.Bookmarks);
    }

    [Fact]
    public void ABookmarkCanBeRemovedAndAMissingOneSaysSo()
    {
        using var dir = new TemporaryDirectory();
        var store = new FilesStateStore(dir.Path);
        store.AddBookmark(Downloads);

        Assert.True(store.RemoveBookmark(Downloads));
        Assert.Empty(store.Bookmarks);
        Assert.False(store.RemoveBookmark(Downloads));
    }

    [Fact]
    public void BookmarksCanBeReorderedAndAnImpossibleMoveDoesNothing()
    {
        using var dir = new TemporaryDirectory();
        var store = new FilesStateStore(dir.Path);
        store.AddBookmark(Location.Parse("/a"), "a");
        store.AddBookmark(Location.Parse("/b"), "b");
        store.AddBookmark(Location.Parse("/c"), "c");

        Assert.True(store.MoveBookmark(2, 0));
        Assert.Equal(["c", "a", "b"], store.Bookmarks.Select(b => b.Label));

        // Driven by a drag, so a drop that lands nowhere sensible is a no-op rather than a throw.
        Assert.False(store.MoveBookmark(0, 0));
        Assert.False(store.MoveBookmark(-1, 1));
        Assert.False(store.MoveBookmark(1, 99));
        Assert.Equal(["c", "a", "b"], store.Bookmarks.Select(b => b.Label));
    }

    [Fact]
    public void AViewIsRememberedPerDirectory()
    {
        using var dir = new TemporaryDirectory();
        var store = new FilesStateStore(dir.Path);
        store.RememberViewState(Downloads, new DirectoryViewState
        {
            ViewMode = ViewMode.Details,
            SortKey = SortKey.Modified,
            SortDescending = true,
            ColumnWidths = [200, 90]
        });
        store.Flush();

        var reopened = new FilesStateStore(dir.Path);
        var state = reopened.ViewStateFor(Downloads);
        Assert.NotNull(state);
        Assert.Equal(ViewMode.Details, state.ViewMode);
        Assert.Equal(SortKey.Modified, state.SortKey);
        Assert.True(state.SortDescending);
        Assert.Equal([200d, 90d], state.ColumnWidths);
        // A directory never visited has no remembered view, which is what makes the defaults
        // apply rather than the last directory's settings leaking into it.
        Assert.Null(reopened.ViewStateFor(Home));
    }

    [Fact]
    public void TheViewStateFileIsBoundedAndEvictsTheLeastRecentlyUsed()
    {
        // Unbounded, this is the file that grows forever: browsing a source tree visits tens
        // of thousands of directories and every one of them would be kept.
        using var dir = new TemporaryDirectory();
        var store = new FilesStateStore(dir.Path);

        for (var i = 0; i < FilesStateStore.ViewStateCapacity + 50; i++)
            store.RememberViewState(Location.Parse($"/dir{i}"), new DirectoryViewState());

        Assert.Equal(FilesStateStore.ViewStateCapacity, store.RememberedDirectories);
        Assert.Null(store.ViewStateFor(Location.Parse("/dir0")));
        Assert.NotNull(store.ViewStateFor(Location.Parse($"/dir{FilesStateStore.ViewStateCapacity + 49}")));
    }

    [Fact]
    public void ReadingADirectoryCountsAsUsingItSoAFavoriteIsNotEvicted()
    {
        using var dir = new TemporaryDirectory();
        var store = new FilesStateStore(dir.Path);
        store.RememberViewState(Home, new DirectoryViewState { ViewMode = ViewMode.Details });

        for (var i = 0; i < FilesStateStore.ViewStateCapacity - 1; i++)
        {
            store.RememberViewState(Location.Parse($"/dir{i}"), new DirectoryViewState());
            // Visited on every pass, as a home directory would be.
            store.ViewStateFor(Home);
        }
        // The eviction that would drop the oldest write.
        store.RememberViewState(Location.Parse("/one-more"), new DirectoryViewState());

        Assert.NotNull(store.ViewStateFor(Home));
        Assert.Null(store.ViewStateFor(Location.Parse("/dir0")));
    }

    [Fact]
    public void RememberingAViewDoesNotWriteTheFileUntilItIsFlushed()
    {
        // The file is touched on every navigation. Writing two thousand entries each time a
        // folder is clicked, on the UI thread, is the cost this defers.
        using var dir = new TemporaryDirectory();
        var store = new FilesStateStore(dir.Path);
        store.RememberViewState(Downloads, new DirectoryViewState { ViewMode = ViewMode.Details });

        Assert.False(File.Exists(store.ViewStatePath));
        store.Flush();
        Assert.True(File.Exists(store.ViewStatePath));

        // And a second flush with nothing to say does not rewrite it.
        var written = File.GetLastWriteTimeUtc(store.ViewStatePath);
        store.Flush();
        Assert.Equal(written, File.GetLastWriteTimeUtc(store.ViewStatePath));
    }

    [Fact]
    public void TheSessionRecordsWindowsAndTabsButNotAPosition()
    {
        // Wayland gives a client no window position at all, so a saved one would be a number
        // written and never read.
        using var dir = new TemporaryDirectory();
        new FilesStateStore(dir.Path).SaveSession(
        [
            new WindowSession
            {
                Width = 900, Height = 600, Maximized = true, SidebarWidth = 170, ActiveTab = 1,
                Tabs = [new TabSession(Home.ToUriString()), new TabSession(Downloads.ToUriString())]
            }
        ]);

        var window = Assert.Single(new FilesStateStore(dir.Path).Session);
        Assert.Equal(900, window.Width);
        Assert.True(window.Maximized);
        Assert.Equal(1, window.ActiveTab);
        Assert.Equal([Home.ToUriString(), Downloads.ToUriString()], window.Tabs.Select(t => t.Uri));
    }

    [Fact]
    public void ASavedShareCarriesNoPassword()
    {
        // Asserted on the serialized bytes, not on the type: this is the one file where a
        // field added by accident would be a real disclosure.
        using var dir = new TemporaryDirectory();
        var store = new FilesStateStore(dir.Path);
        store.SaveShare(new SavedShare
        {
            Scheme = "smb", Host = "nas", Share = "media", Username = "vic", Label = "Media"
        });

        var json = File.ReadAllText(store.SharesPath);
        Assert.DoesNotContain("assword", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nas", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SavingAShareTwiceReplacesItRatherThanDuplicatingIt()
    {
        using var dir = new TemporaryDirectory();
        var store = new FilesStateStore(dir.Path);
        var share = new SavedShare { Scheme = "smb", Host = "nas", Share = "media", Username = "vic" };
        store.SaveShare(share);
        store.SaveShare(share with { Label = "renamed" });

        Assert.Equal("renamed", Assert.Single(store.Shares).Label);
    }

    [Fact]
    public void TwoUsersOnOneHostAreTwoShares()
    {
        // Folding these together would silently throw away one of two saved connections.
        using var dir = new TemporaryDirectory();
        var store = new FilesStateStore(dir.Path);
        store.SaveShare(new SavedShare { Scheme = "smb", Host = "nas", Share = "media", Username = "vic" });
        store.SaveShare(new SavedShare { Scheme = "smb", Host = "nas", Share = "media", Username = "guest" });

        Assert.Equal(2, store.Shares.Count);
    }

    [Fact]
    public void AFileThatWillNotParseDegradesToTheDefaultsAndSaysSo()
    {
        using var dir = new TemporaryDirectory();
        Directory.CreateDirectory(dir.Path);
        File.WriteAllText(Path.Combine(dir.Path, "bookmarks.json"), "{ this is not json");

        var warnings = new List<string>();
        var store = new FilesStateStore(dir.Path, warnings.Add);

        Assert.Empty(store.Bookmarks);
        Assert.Single(warnings);
        // And it is still usable: the next write replaces the bad file.
        Assert.True(store.AddBookmark(Home));
        Assert.Single(new FilesStateStore(dir.Path).Bookmarks);
    }

    [Fact]
    public void ADocumentFromANewerBuildIsIgnoredRatherThanGuessedAt()
    {
        using var dir = new TemporaryDirectory();
        Directory.CreateDirectory(dir.Path);
        File.WriteAllText(Path.Combine(dir.Path, "preferences.json"),
            $$"""{"Version": {{FilesStateStore.CurrentVersion + 1}}, "ShowHidden": true}""");

        var warnings = new List<string>();
        var store = new FilesStateStore(dir.Path, warnings.Add);

        Assert.False(store.Preferences.ShowHidden);
        Assert.Single(warnings);
    }

    [Fact]
    public void AVersionlessDocumentIsIgnoredToo()
    {
        // Version 0 is what a document written before the field existed reads as, and there is
        // no such document -- but a hand-edited file that dropped the line is exactly as
        // unreadable, and answering with the defaults is the same right answer.
        using var dir = new TemporaryDirectory();
        Directory.CreateDirectory(dir.Path);
        File.WriteAllText(Path.Combine(dir.Path, "preferences.json"), """{"ShowHidden": true}""");

        Assert.False(new FilesStateStore(dir.Path).Preferences.ShowHidden);
    }

    [Fact]
    public void EveryWriteIsAtomicAndLeavesNoPartFileBehind()
    {
        using var dir = new TemporaryDirectory();
        var store = new FilesStateStore(dir.Path);
        store.AddBookmark(Home);
        store.Preferences = new FilesPreferences { ShowHidden = true };
        store.SaveSession([new WindowSession { Width = 800, Height = 600 }]);
        store.RememberViewState(Home, new DirectoryViewState());
        store.Flush();

        Assert.Empty(Directory.GetFiles(dir.Path, "*.wlrix-new"));
        Assert.True(File.Exists(store.BookmarksPath));
    }

    [Fact]
    public void AReadOnlyDirectoryDoesNotStopTheApplication()
    {
        // Every one of these is a convenience. Refusing to run because the home directory is
        // read-only would be the wrong trade by a wide margin.
        using var dir = new TemporaryDirectory();
        var root = Path.Combine(dir.Path, "state");
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var warnings = new List<string>();
            var store = new FilesStateStore(root, warnings.Add);
            store.AddBookmark(Home);

            // It reports the failure and carries on with the change held in memory.
            Assert.Single(warnings);
            Assert.Single(store.Bookmarks);
        }
        finally
        {
            File.SetUnixFileMode(root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
