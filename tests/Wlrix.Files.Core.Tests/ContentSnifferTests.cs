using Wlrix.Files.Core.Mime;
using Wlrix.Files.Core.Testing;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// Naming one file by reading it — and, more to the point, not reading it when the name
/// already answers.
/// </summary>
public class ContentSnifferTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private sealed class SingleFileSystemProvider(FakeFileSystem fs) : IFileSystemProvider
    {
        public Task<IFileSystem> GetAsync(Location location, CancellationToken cancellationToken) =>
            Task.FromResult<IFileSystem>(fs);
    }

    private static SharedMimeDatabase Mime() =>
        SharedMimeDatabase.Load([Path.Combine(AppContext.BaseDirectory, "Fixtures", "mime")]);

    private static FileEntry Entry(string path, long size, FileKind kind = FileKind.File)
    {
        var location = Location.Parse(path);
        return new FileEntry { Location = location, Name = location.Name, Kind = kind, Size = size };
    }

    private static Task<string> Resolve(FakeFileSystem fs, FileEntry entry) =>
        ContentSniffer.ResolveAsync(Mime(), new SingleFileSystemProvider(fs), entry, None);

    [Fact]
    public async Task AFileWithNoUsefulNameIsNamedByItsContents()
    {
        var fs = new FakeFileSystem().AddFile("/src/configure", "#!/bin/sh\nexit 0\n");
        var entry = Entry("/src/configure", 17);

        Assert.Equal("text/x-shellscript", await Resolve(fs, entry));
    }

    [Fact]
    public async Task AFileWhoseNameAnswersIsNeverRead()
    {
        // The check that keeps this affordable. Counting the operations is the only way to
        // state it: the answer alone would be the same either way.
        var fs = new FakeFileSystem().AddFile("/src/notes.txt", "%PDF-1.7 pretending");
        var before = fs.OperationCount;

        Assert.Equal("text/plain", await Resolve(fs, Entry("/src/notes.txt", 19)));
        Assert.Equal(before, fs.OperationCount);
    }

    [Fact]
    public async Task AnEmptyFileIsAnsweredWithoutOpeningIt()
    {
        var fs = new FakeFileSystem().AddFile("/src/scratch");
        var before = fs.OperationCount;

        Assert.Equal("inode/x-empty", await Resolve(fs, Entry("/src/scratch", 0)));
        Assert.Equal(before, fs.OperationCount);
    }

    [Fact]
    public async Task AnUnreadableFileFallsBackToWhatTheNameSaid()
    {
        // The caller asked what kind of file this is, not whether it could be read. Failing
        // the whole question over a failed guess would be worse than answering it vaguely.
        var fs = new FakeFileSystem().AddFile("/src/locked", "#!/bin/sh\n");
        fs.FailAlways("/src/locked", FileErrorKind.AccessDenied);

        Assert.Equal(SharedMimeDatabase.Default, await Resolve(fs, Entry("/src/locked", 10)));
    }

    [Fact]
    public async Task ADirectoryIsNamedByItsKindAndNotOpened()
    {
        var fs = new FakeFileSystem().AddDirectory("/src/things");
        var before = fs.OperationCount;

        Assert.Equal(SharedMimeDatabase.Directory, await Resolve(fs, Entry("/src/things", 0, FileKind.Directory)));
        Assert.Equal(before, fs.OperationCount);
    }

    [Fact]
    public async Task ContentsThatMatchNothingLeaveTheNamesAnswerStanding()
    {
        var fs = new FakeFileSystem().AddFile("/src/blob", [0x00, 0x01, 0x02, 0xFF]);
        Assert.Equal(SharedMimeDatabase.Default, await Resolve(fs, Entry("/src/blob", 4)));
    }

    [Fact]
    public async Task OnlyTheHeadOfTheFileIsRead()
    {
        // A sniff must not pull a gigabyte over a share to look at its first eight bytes.
        var mime = Mime();
        var big = new byte[mime.SniffLength * 4];
        "%PDF-1.7"u8.CopyTo(big);

        var fs = new FakeFileSystem().AddFile("/src/paper", big);
        Assert.Equal("application/pdf", await Resolve(fs, Entry("/src/paper", big.Length)));
    }
}
