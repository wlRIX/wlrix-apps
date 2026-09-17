using Wlrix.Files.Core;
using System.Globalization;
using Avalonia.Threading;
using System.Reactive.Linq;
using Wlrix.Files.Core.Icons;
using Wlrix.Files.Core.Metadata;
using Wlrix.Files.Core.Mime;
using Wlrix.Files.Core.Platform;
using Wlrix.Files.Core.Testing;
using Wlrix.Files.Services;
using Wlrix.Files.ViewModels;
using Xunit;

namespace Wlrix.Files.Tests;

/// <summary>
/// The properties window's view model.
/// </summary>
/// <remarks>
/// No Avalonia, and that is why <c>LoadIconAsync</c> is a separate call on the view model:
/// decoding a bitmap needs a rendering platform, and everything the window states as fact
/// does not.
/// </remarks>
public class PropertiesTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <remarks>
    /// The catalog answers in the session's language, and this machine's session is Japanese.
    /// Pinned per test rather than once for the assembly because xunit builds a fresh instance
    /// on the thread that will run the test, and the culture is per-thread.
    /// </remarks>
    public PropertiesTests() => CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

    private sealed class SingleFileSystemProvider(FakeFileSystem fs) : IFileSystemProvider
    {
        public Task<IFileSystem> GetAsync(Location location, CancellationToken cancellationToken) =>
            Task.FromResult<IFileSystem>(fs);
    }

    /// <summary>
    /// The trimmed fixture database, never the machine's.
    /// </summary>
    /// <remarks>
    /// Shared with the Core suite by project reference rather than copied, so the descriptions
    /// asserted here are the ones those tests pin.
    /// </remarks>
    private static SharedMimeDatabase Mime() =>
        SharedMimeDatabase.Load([Path.Combine(AppContext.BaseDirectory, "Fixtures", "mime")]);

    private static IconService Icons() =>
        new(Mime(), new IconLoader(new XdgIconTheme(baseDirectories: [], pixmapDirectories: [])));

    private static PropertiesViewModel Model(FakeFileSystem fs, params FileEntry[] entries) =>
        new(new SingleFileSystemProvider(fs), Icons(), Mime(), MountTable.Parse([]), entries);

    private static FileEntry Entry(
        string path, bool directory = false, long size = 0, int? mode = null, string? link = null)
    {
        var location = Location.Parse(path);
        return new FileEntry
        {
            Location = location,
            Name = location.Name,
            Kind = directory ? FileKind.Directory : link is not null ? FileKind.Symlink : FileKind.File,
            Size = size,
            UnixMode = mode,
            SymlinkTarget = link
        };
    }

    // --- what it says ------------------------------------------------------

    [Fact]
    public void AFileIsDescribedInWordsWithItsTypeBesideIt()
    {
        // The words are for the person reading; the type is what an Open With rule matches on,
        // and someone looking at this window is often trying to find out exactly that.
        var model = Model(new FakeFileSystem(), Entry("/photos/holiday.png"));

        Assert.Equal("holiday.png", model.Name);
        Assert.Equal("PNG image (image/png)", model.KindText);
        Assert.Equal("/photos", model.LocationText);
    }

    [Fact]
    public async Task AFileWithNoUsefulNameIsDescribedOnceItHasBeenRead()
    {
        // The kind line starts with what the name says and sharpens when the bytes arrive, so
        // the window is never blank while a share is read. Here the name says nothing at all.
        var fs = new FakeFileSystem().AddFile("/src/configure", "#!/bin/sh\nexit 0\n");
        var model = Model(fs, Entry("/src/configure", size: 17));

        Assert.Equal("application/octet-stream", model.KindText);

        await model.LoadAsync();
        Assert.Equal("text/x-shellscript", model.KindText);
    }

    [Fact]
    public void ATypeWithNoDescriptionIsShownAsItself()
    {
        var model = Model(new FakeFileSystem(), Entry("/x/thing.unknownext"));
        Assert.Equal("application/octet-stream", model.KindText);
    }

    [Fact]
    public void SeveralSelectedThingsAreCountedRatherThanNamed()
    {
        var model = Model(
            new FakeFileSystem(),
            Entry("/d/a.png"),
            Entry("/d/b.png"),
            Entry("/d/sub", directory: true));

        Assert.Null(model.Single);
        Assert.Equal("3 items", model.Name);
        Assert.Equal("2 files, 1 folder", model.KindText);
    }

    [Fact]
    public void OneOfSomethingIsCountedInTheSingular()
    {
        // The two counts are independent, so a folder with three files and one subfolder in
        // it would read "3 files, 1 folders" if the line were one format string.
        var model = Model(
            new FakeFileSystem(),
            Entry("/d/a.png"),
            Entry("/d/sub", directory: true));

        Assert.Equal("1 file, 1 folder", model.KindText);
    }

    [Fact]
    public void ALinkSaysWhereItPointsAndAnythingElseDoesNotHaveTheLine()
    {
        Assert.True(Model(new FakeFileSystem(), Entry("/d/shortcut", link: "/real/file")).HasLinkTarget);
        Assert.False(Model(new FakeFileSystem(), Entry("/d/plain.txt")).HasLinkTarget);
    }

    // --- the size, which is the part that has to be walked -----------------

    [Fact]
    public async Task ADirectorysSizeIsTheSizeOfWhatIsInItRatherThanOfItsOwnIndex()
    {
        var fs = new FakeFileSystem()
            .AddFile("/photos/a.png", new byte[1500])
            .AddFile("/photos/2019/b.png", new byte[2500]);

        var model = Model(fs, Entry("/photos", directory: true));
        await model.LoadAsync();

        Assert.Equal("3.9 KiB (4,000 bytes)", model.SizeText);
        Assert.True(model.HasContents);
        Assert.Equal("2 files, 1 folder", model.ContentsText);
    }

    [Fact]
    public async Task APlainFileHasNoContentsLine()
    {
        // "1 item, 0 folders" under a file's size says nothing anyone needed.
        var fs = new FakeFileSystem().AddFile("/notes.txt", new byte[42]);
        var model = Model(fs, Entry("/notes.txt", size: 42));
        await model.LoadAsync();

        Assert.Equal("42 B (42 bytes)", model.SizeText);
        Assert.False(model.HasContents);
    }

    [Fact]
    public async Task TheFinishedTotalIsNotPaintedOverByAReportStillInTheQueue()
    {
        // A small directory finishes inside one dispatcher turn, so the last running total is
        // still queued when the final one is set. Left alone it lands second and the window
        // sits there saying "counting" forever, which is what the first screenshot showed.
        var fs = new FakeFileSystem().AddFile("/d/a", new byte[10]).AddFile("/d/b", new byte[20]);
        var model = Model(fs, Entry("/d", directory: true));

        await model.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("30 B (30 bytes)", model.SizeText);
        Assert.Equal("2 files, 0 folders", model.ContentsText);
    }

    [Fact]
    public void TheSizeSaysItIsCountingBeforeTheWalkHasRun()
    {
        // What the window shows in the instant between opening and the first total. It must
        // not be blank, and it must not be a wrong number.
        Assert.Equal("Calculating...", Model(new FakeFileSystem(), Entry("/d", directory: true)).SizeText);
    }

    [Fact]
    public async Task ClosingTheWindowStopsTheWalk()
    {
        // A properties window opened on a home directory by mistake must not keep a disk busy
        // after it is gone.
        var fs = new FakeFileSystem();
        for (var i = 0; i < 500; i++)
            fs.AddFile($"/big/f{i}", new byte[1]);

        var model = Model(fs, Entry("/big", directory: true));
        model.Dispose();

        // Cancellation is swallowed rather than thrown at the window, which has already gone.
        await model.LoadAsync();
        Assert.Equal("Calculating...", model.SizeText);
    }

    // --- permissions -------------------------------------------------------

    [Fact]
    public async Task ThePermissionsComeFromAFreshStatAndNotFromTheListingRow()
    {
        // A listing entry can be minutes old, and this window's whole purpose is to show the
        // current mode and then change it. Writing back a stale one would undo whatever
        // chmod did in the meantime.
        var fs = new FakeFileSystem().AddFile("/f.txt");
        await fs.SetUnixModeAsync(Location.Parse("/f.txt"), 0b110_100_100, None);

        var model = Model(fs, Entry("/f.txt", mode: 0b111_111_111));
        await model.LoadAsync();

        Assert.True(model.HasPermissions);
        Assert.Equal("-rw-r--r--", model.PermissionsText);
        Assert.Equal("0644", model.OctalText);
    }

    [Fact]
    public async Task AFilesystemWithNoModesShowsNoPermissionsSectionAtAll()
    {
        // A share has no mode to report. A grid of disabled checkboxes would only raise the
        // question of how to enable them.
        var fs = new FakeFileSystem("smb://host") { Capabilities = FileSystemCapabilities.Rename };
        fs.AddFile("/share/f.txt");

        var model = Model(fs, Entry("/share/f.txt"));
        await model.LoadAsync();

        Assert.False(model.HasPermissions);
        Assert.False(model.CanEditPermissions);
    }

    [Fact]
    public async Task TheThreeSpellingsOfTheModeMoveTogether()
    {
        // Checkboxes, the octal field and the ls line are three views of twelve bits. The
        // window shows all three at once, so any one of them changing has to move the others.
        var fs = new FakeFileSystem().AddFile("/f.txt");
        await fs.SetUnixModeAsync(Location.Parse("/f.txt"), 0b110_100_100, None);

        var model = Model(fs, Entry("/f.txt"));
        await model.LoadAsync();

        model.OwnerExecute = true;
        Assert.Equal("0744", model.OctalText);
        Assert.Equal("-rwxr--r--", model.PermissionsText);

        model.OctalText = "0755";
        Assert.True(model.GroupExecute);
        Assert.True(model.OtherExecute);
        Assert.False(model.GroupWrite);
        Assert.Equal("-rwxr-xr-x", model.PermissionsText);
    }

    [Fact]
    public async Task HalfTypedTextInTheOctalFieldIsLeftAloneRatherThanSnappedBack()
    {
        // "7" on the way to "755" is not a mode yet. Reverting it mid-keystroke would make
        // the field impossible to type in.
        var fs = new FakeFileSystem().AddFile("/f.txt");
        await fs.SetUnixModeAsync(Location.Parse("/f.txt"), 0b110_100_100, None);

        var model = Model(fs, Entry("/f.txt"));
        await model.LoadAsync();

        model.OctalText = "u+x";
        Assert.Equal("u+x", model.OctalText);
        // ...and the mode it would have written is unchanged.
        Assert.Equal("-rw-r--r--", model.PermissionsText);
    }

    [Fact]
    public async Task ApplyingWritesTheModeAndNothingElseDoes()
    {
        var fs = new FakeFileSystem().AddFile("/f.txt");
        await fs.SetUnixModeAsync(Location.Parse("/f.txt"), 0b110_100_100, None);

        var model = Model(fs, Entry("/f.txt"));
        await model.LoadAsync();
        Assert.False(model.IsDirty);

        model.OwnerExecute = true;
        Assert.True(model.IsDirty);

        // Ticking a box changes nothing on disk until Apply.
        var stat = await fs.StatAsync(Location.Parse("/f.txt"), None);
        Assert.Equal(0b110_100_100, stat.UnixMode);

        await model.Apply.Execute().FirstAsync();

        stat = await fs.StatAsync(Location.Parse("/f.txt"), None);
        Assert.Equal(0b111_100_100, stat.UnixMode);
        Assert.False(model.IsDirty);
    }

    [Fact]
    public async Task AModeThatCannotBeWrittenIsReportedInTheWindowRatherThanThrown()
    {
        var fs = new FakeFileSystem().AddFile("/f.txt");
        await fs.SetUnixModeAsync(Location.Parse("/f.txt"), 0b110_100_100, None);

        var model = Model(fs, Entry("/f.txt"));
        await model.LoadAsync();

        model.OwnerExecute = true;
        fs.FailAlways("/f.txt", FileErrorKind.AccessDenied);
        await model.Apply.Execute().FirstAsync();

        Assert.True(model.HasProblem);
        // Still dirty: the change was not made, and saying otherwise would be a lie.
        Assert.True(model.IsDirty);
    }

    [Fact]
    public async Task ApplyingWithNothingChangedTouchesTheFileAtAll()
    {
        // A no-op chmod is a modification time change on some filesystems, and this window is
        // opened far more often to read than to edit.
        var fs = new FakeFileSystem().AddFile("/f.txt");
        await fs.SetUnixModeAsync(Location.Parse("/f.txt"), 0b110_100_100, None);

        var model = Model(fs, Entry("/f.txt"));
        await model.LoadAsync();

        var before = fs.OperationCount;
        await model.Apply.Execute().FirstAsync();
        Assert.Equal(before, fs.OperationCount);
    }
}
