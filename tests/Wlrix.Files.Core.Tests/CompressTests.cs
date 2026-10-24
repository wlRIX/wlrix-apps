using System.Formats.Tar;
using System.IO.Compression;
using Wlrix.Files.Core.Operations;
using Wlrix.Files.Core.Platform;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>Compress To, against a real disk, read back with the runtime's own readers.</summary>
public class CompressTests
{
    private static OperationJob Job() =>
        new(new FileSystemProvider(), null, FixedRetryPolicy.SkipAll, MountTable.Read());

    private static Location At(TemporaryDirectory temp, string relative) =>
        Location.FromLocalPath(Path.Combine(temp.Path, relative));

    private static TemporaryDirectory Tree()
    {
        var temp = new TemporaryDirectory();
        temp.File("docs/one.txt", "first");
        temp.File("docs/deep/two.txt", "second");
        temp.Directory_("docs/empty");
        // Inside a directory, where the listing that finds it does not carry its mode.
        temp.File("docs/bin/run.sh", "#!/bin/sh\n");
        File.SetUnixFileMode(Path.Combine(temp.Path, "docs/bin/run.sh"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.CreateSymbolicLink(Path.Combine(temp.Path, "docs/link"), "one.txt");
        return temp;
    }

    [Fact]
    public async Task ATarGzHoldsTheTreeWithItsModesAndLinks()
    {
        using var temp = Tree();
        var archive = At(temp, "out.tar.gz");

        var result = await Job().RunAsync(FileOperation.Compress(
            [At(temp, "docs")], archive, ArchiveFormat.TarGz));

        Assert.True(result.Succeeded);
        var entries = new Dictionary<string, (TarEntryType Type, string Text, UnixFileMode Mode, string Link)>();
        await using (var file = File.OpenRead(archive.Path))
        await using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        {
            var reader = new TarReader(gzip);
            while (await reader.GetNextEntryAsync() is { } entry)
            {
                var text = entry.DataStream is { } data ? await new StreamReader(data).ReadToEndAsync() : "";
                entries[entry.Name] = (entry.EntryType, text, entry.Mode, entry.LinkName);
            }
        }

        Assert.Equal("first", entries["docs/one.txt"].Text);
        Assert.Equal("second", entries["docs/deep/two.txt"].Text);
        Assert.Equal(TarEntryType.Directory, entries["docs/empty/"].Type);
        Assert.Equal(TarEntryType.SymbolicLink, entries["docs/link"].Type);
        Assert.Equal("one.txt", entries["docs/link"].Link);
        Assert.True(entries["docs/bin/run.sh"].Mode.HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task AZipHoldsTheTreeWithItsModesAndLinks()
    {
        using var temp = Tree();
        var archive = At(temp, "out.zip");

        var result = await Job().RunAsync(FileOperation.Compress(
            [At(temp, "docs")], archive, ArchiveFormat.Zip));

        Assert.True(result.Succeeded);
        using var zip = ZipFile.OpenRead(archive.Path);
        async Task<string> Read(string name)
        {
            await using var stream = await zip.GetEntry(name)!.OpenAsync();
            return await new StreamReader(stream).ReadToEndAsync();
        }

        Assert.Equal("first", await Read("docs/one.txt"));
        Assert.Equal("second", await Read("docs/deep/two.txt"));
        Assert.NotNull(zip.GetEntry("docs/empty/"));
        Assert.Equal("one.txt", await Read("docs/link"));
        Assert.Equal(0xA000, (zip.GetEntry("docs/link")!.ExternalAttributes >> 16) & 0xF000);
        Assert.NotEqual(0, (zip.GetEntry("docs/bin/run.sh")!.ExternalAttributes >> 16) & 0b001_000_000);
    }

    [Fact]
    public async Task AnArchiveNeverReplacesAFileThatAppearedAtItsName()
    {
        using var temp = Tree();
        temp.File("taken.zip", "somebody else's");

        var result = await Job().RunAsync(FileOperation.Compress(
            [At(temp, "docs")], At(temp, "taken.zip"), ArchiveFormat.Zip));

        Assert.False(result.Succeeded);
        Assert.Equal("somebody else's", await File.ReadAllTextAsync(Path.Combine(temp.Path, "taken.zip")));
        Assert.Empty(Directory.GetFiles(temp.Path, "*" + OperationJob.PartSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public void MembersAreNamedFromTheDirectoryTheArchiveIsMadeIn()
    {
        // A search result keeps the subdirectory it was found in.
        var home = Location.FromLocalPath("/data");
        var found = Location.FromLocalPath("/data/a/b/hit.txt");
        Assert.Equal("a/b/hit.txt", OperationJob.MemberName(found, [found], home));

        // Anything from elsewhere is named from its own parent.
        var elsewhere = Location.FromLocalPath("/other/dir/inner.txt");
        Assert.Equal("dir/inner.txt",
            OperationJob.MemberName(elsewhere, [Location.FromLocalPath("/other/dir")], home));
    }

    [Theory]
    [InlineData(new[] { "/home/u/report.pdf" }, false, "report")]
    [InlineData(new[] { "/home/u/photos" }, true, "photos")]
    [InlineData(new[] { "/home/u/.bashrc" }, false, ".bashrc")]
    [InlineData(new[] { "/home/u/a.txt", "/home/u/b.txt" }, false, "u")]
    [InlineData(new[] { "/a.txt", "/b.txt" }, false, "Archive")]
    public void TheArchiveIsNamedAfterWhatGoesIntoIt(string[] paths, bool directory, string stem)
    {
        var sources = paths.Select(Location.FromLocalPath).ToList();
        Assert.Equal(stem, ArchiveNaming.Stem(sources, directory, sources[0].Parent!, "Archive"));
    }

    [Fact]
    public void ACollisionNumbersTheStemAndKeepsTheWholeExtension()
    {
        Assert.Equal(["x.tar.gz", "x (2).tar.gz", "x (3).tar.gz"],
            ArchiveNaming.Candidates("x", ArchiveFormat.TarGz).Take(3));
    }
}
