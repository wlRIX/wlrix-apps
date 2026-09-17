using Wlrix.Files.Core.Dnd;
using Wlrix.Files.Core.Filesystems;
using Wlrix.Files.Core.Testing;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// Making remote files draggable. The fake stands in for a share, so this exercises the whole
/// path — scan, guard, copy, the paths handed back — with no network anywhere near it.
/// </summary>
public class DragStagingTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>Serves the fake for smb:// and the real disk for file://, as the app does.</summary>
    private sealed class MixedProvider(FakeFileSystem remote) : IFileSystemProvider
    {
        private readonly LocalFileSystem _local = new();

        public Task<IFileSystem> GetAsync(Location location, CancellationToken cancellationToken) =>
            Task.FromResult<IFileSystem>(location.IsLocal ? _local : remote);
    }

    [Fact]
    public void LocalSourcesNeedNoStagingAtAll()
    {
        Assert.False(DragStaging.NeedsStaging([Location.Parse("/home/vic/a.txt")]));
        Assert.True(DragStaging.NeedsStaging([Location.Parse("smb://host/share/a.txt")]));
        // One remote member is enough: the drag cannot go out until it has a path.
        Assert.True(DragStaging.NeedsStaging(
            [Location.Parse("/home/vic/a.txt"), Location.Parse("sftp://host/b.txt")]));
    }

    [Fact]
    public async Task ALocalSelectionIsHandedBackUntouchedRatherThanCopied()
    {
        using var dir = new TemporaryDirectory();
        var file = dir.File("a.txt", "hello");
        using var staging = new DragStaging(new MixedProvider(new FakeFileSystem("smb://host")),
            Path.Combine(dir.Path, "scratch"));

        var paths = await staging.StageAsync([Location.FromLocalPath(file)], null, None);

        Assert.Equal([file], paths);
        // Nothing was staged, so the scratch directory was never even created.
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "scratch")));
    }

    [Fact]
    public async Task ARemoteFileIsCopiedDownAndItsLocalPathReturned()
    {
        using var dir = new TemporaryDirectory();
        var remote = new FakeFileSystem("smb://host").AddFile("/share/report.pdf", "contents");
        using var staging = new DragStaging(new MixedProvider(remote), Path.Combine(dir.Path, "scratch"));

        var paths = await staging.StageAsync([Location.Parse("smb://host/share/report.pdf")], null, None);

        var staged = Assert.Single(paths);
        Assert.Equal("contents", File.ReadAllText(staged));
        Assert.StartsWith(Path.Combine(dir.Path, "scratch"), staged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARemoteDirectoryComesDownWholeAndIsOfferedAsOneItem()
    {
        // Dragging a folder out must drop a folder, not a flat pile of what was inside it.
        using var dir = new TemporaryDirectory();
        var remote = new FakeFileSystem("smb://host")
            .AddFile("/share/project/main.c", "int main;")
            .AddFile("/share/project/inc/api.h", "#pragma once");
        using var staging = new DragStaging(new MixedProvider(remote), Path.Combine(dir.Path, "scratch"));

        var paths = await staging.StageAsync([Location.Parse("smb://host/share/project")], null, None);

        var staged = Assert.Single(paths);
        Assert.True(Directory.Exists(staged));
        Assert.Equal("int main;", File.ReadAllText(Path.Combine(staged, "main.c")));
        Assert.Equal("#pragma once", File.ReadAllText(Path.Combine(staged, "inc", "api.h")));
    }

    [Fact]
    public async Task AMixedSelectionKeepsItsOrderSoTheCallerNeedNotPairItBackUp()
    {
        using var dir = new TemporaryDirectory();
        var local = dir.File("here.txt", "local");
        var remote = new FakeFileSystem("smb://host").AddFile("/share/there.txt", "remote");
        using var staging = new DragStaging(new MixedProvider(remote), Path.Combine(dir.Path, "scratch"));

        var paths = await staging.StageAsync(
            [Location.Parse("smb://host/share/there.txt"), Location.FromLocalPath(local)], null, None);

        Assert.Equal(2, paths.Count);
        Assert.Equal("remote", File.ReadAllText(paths[0]));
        Assert.Equal(local, paths[1]);
    }

    [Fact]
    public async Task TwoDragsOfTheSameNameDoNotOverwriteEachOther()
    {
        // Otherwise the second drag hands over the first one's file, which looks exactly like
        // the drop having gone to the wrong place.
        using var dir = new TemporaryDirectory();
        var remote = new FakeFileSystem("smb://host").AddFile("/share/a/note.txt", "first");
        using var staging = new DragStaging(new MixedProvider(remote), Path.Combine(dir.Path, "scratch"));

        var first = await staging.StageAsync([Location.Parse("smb://host/share/a/note.txt")], null, None);
        remote.AddFile("/share/b/note.txt", "second");
        var second = await staging.StageAsync([Location.Parse("smb://host/share/b/note.txt")], null, None);

        Assert.NotEqual(first[0], second[0]);
        Assert.Equal("first", File.ReadAllText(first[0]));
        Assert.Equal("second", File.ReadAllText(second[0]));
    }

    [Fact]
    public async Task AnOversizedDragIsRefusedBeforeAnyBytesMove()
    {
        using var dir = new TemporaryDirectory();
        var remote = new FakeFileSystem("smb://host").AddFile("/share/big.iso", new byte[4096]);
        var scratch = Path.Combine(dir.Path, "scratch");
        using var staging = new DragStaging(new MixedProvider(remote), scratch) { MaxBytes = 1024 };

        var error = await Assert.ThrowsAsync<FileOperationException>(() =>
            staging.StageAsync([Location.Parse("smb://host/share/big.iso")], null, None));

        Assert.Equal(FileErrorKind.NoSpace, error.Kind);
        // The point of the guard is that it fires first. A partial download left behind would
        // mean it had already done the thing it exists to prevent.
        Assert.Empty(Directory.GetFiles(scratch, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AFailedStagingThrowsRatherThanOfferingAFileThatIsNotThere()
    {
        using var dir = new TemporaryDirectory();
        var remote = new FakeFileSystem("smb://host").AddFile("/share/a.txt", "hello");
        remote.FailAlways("/share/a.txt", FileErrorKind.ConnectionLost);
        using var staging = new DragStaging(new MixedProvider(remote), Path.Combine(dir.Path, "scratch"));

        await Assert.ThrowsAsync<FileOperationException>(() =>
            staging.StageAsync([Location.Parse("smb://host/share/a.txt")], null, None));
    }

    [Fact]
    public async Task DisposingRemovesEverythingItStaged()
    {
        using var dir = new TemporaryDirectory();
        var scratch = Path.Combine(dir.Path, "scratch");
        var remote = new FakeFileSystem("smb://host").AddFile("/share/a.txt", "hello");

        var staging = new DragStaging(new MixedProvider(remote), scratch);
        await staging.StageAsync([Location.Parse("smb://host/share/a.txt")], null, None);
        Assert.True(Directory.Exists(scratch));

        staging.Dispose();
        Assert.False(Directory.Exists(scratch));
    }
}
