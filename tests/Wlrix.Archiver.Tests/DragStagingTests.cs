using Wlrix.Archiver.Models;
using Wlrix.Archiver.Services;
using Wlrix.Archiver.Services.Archives;
using Wlrix.Archiver.Services.Encodings;
using Xunit;

namespace Wlrix.Archiver.Tests;

public sealed class DragStagingTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "wlrix-archiver-drag-" + Guid.NewGuid().ToString("N"));

    private readonly DragStaging _staging;
    private readonly SharpCompressBackend _backend = new(new FilenameDecoder());

    public DragStagingTests() =>
        _staging = new DragStaging(new ArchiveBackendRegistry([_backend]), _root);

    public void Dispose() => _staging.Dispose();

    private Task<OpenArchive> Open() =>
        _backend.OpenAsync(Fixture.Path("no-dir-entries.zip"), ArchiveFormat.Zip);

    [Fact]
    public async Task ANestedFileIsHandedOverOnItsOwnNotInsideItsParents()
    {
        // Dragging a/b/c.txt out should drop c.txt, not an `a` folder the user has to dig into.
        var staged = await _staging.StageAsync(await Open(), ["a/b/c.txt"]);

        var file = Assert.Single(staged);
        Assert.Equal("c.txt", Path.GetFileName(file));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task ADirectoryIsHandedOverWithEverythingUnderIt()
    {
        var staged = await _staging.StageAsync(await Open(), ["a/b"]);

        var directory = Assert.Single(staged);
        Assert.Equal("b", Path.GetFileName(directory));
        Assert.True(File.Exists(Path.Combine(directory, "c.txt")));
        Assert.True(File.Exists(Path.Combine(directory, "d.txt")));
    }

    [Fact]
    public async Task AnEntryInsideASelectedDirectoryIsNotOfferedTwice()
    {
        var staged = await _staging.StageAsync(await Open(), ["a/b", "a/b/c.txt", "a/e.txt"]);

        Assert.Equal(["b", "e.txt"], staged.Select(path => Path.GetFileName(path)).Order());
    }
}
