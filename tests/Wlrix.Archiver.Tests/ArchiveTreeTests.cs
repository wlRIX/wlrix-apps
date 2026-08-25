using Wlrix.Archiver.Models;
using Xunit;

namespace Wlrix.Archiver.Tests;

public class ArchiveTreeTests
{
    private static ArchiveEntry File(string path) =>
        new(path, IsDirectory: false, Size: 1, CompressedSize: 1, Modified: null, Mode: null,
            Owner: null, Group: null);

    private static ArchiveEntry Directory(string path) =>
        new(path, IsDirectory: true, Size: null, CompressedSize: null, Modified: null,
            Mode: null, Owner: null, Group: null);

    [Fact]
    public void MissingParentDirectoriesAreSynthesizedFromTheirChildrensPaths()
    {
        // The ordinary case, not an edge one: neither tar nor zip has to record a directory,
        // and plenty of writers do not.
        var roots = ArchiveTree.Build([File("a/b/c.txt")]);

        var a = Assert.Single(roots);
        Assert.Equal("a", a.Entry.Name);
        Assert.True(a.Entry.IsDirectory);
        Assert.True(a.Entry.Synthesized);

        var b = Assert.Single(a.Children);
        Assert.Equal("b", b.Entry.Name);
        var c = Assert.Single(b.Children);
        Assert.Equal("c.txt", c.Entry.Name);
        Assert.False(c.Entry.Synthesized);
    }

    [Fact]
    public void ARealDirectoryEntryReplacesThePlaceholderAChildAlreadyCreated()
    {
        // Order matters here and archives do not guarantee one: the file can precede the
        // directory record that describes it, and the real metadata has to win either way.
        var roots = ArchiveTree.Build([File("a/b.txt"), Directory("a/") with { Mode = 0b111_101_101 }]);

        var a = Assert.Single(roots);
        Assert.False(a.Entry.Synthesized);
        Assert.Equal(0b111_101_101, a.Entry.Mode);
        Assert.Single(a.Children);
    }

    [Fact]
    public void ADirectoryEntryAndItsChildrenShareOneNodeDespiteTheTrailingSlash()
    {
        var roots = ArchiveTree.Build([Directory("src/"), File("src/main.c")]);

        var src = Assert.Single(roots);
        Assert.Equal("src", src.Entry.Path);
        Assert.Single(src.Children);
    }

    [Fact]
    public void DirectoriesSortBeforeFilesAndThenByNameIgnoringCase()
    {
        var roots = ArchiveTree.Build([
            File("zebra.txt"), File("Apple.txt"), Directory("mango/"), Directory("Banana/"),
        ]);

        Assert.Equal(["Banana", "mango", "Apple.txt", "zebra.txt"],
            roots.Select(node => node.Entry.Name));
    }

    [Fact]
    public void BackslashSeparatedPathsFromWindowsWrittenZipsBecomeOneTreeNotOneLongName()
    {
        var roots = ArchiveTree.Build([File(@"docs\readme.txt")]);

        var docs = Assert.Single(roots);
        Assert.Equal("docs", docs.Entry.Name);
        Assert.Equal("readme.txt", Assert.Single(docs.Children).Entry.Name);
    }

    [Theory]
    [InlineData("/absolute/path.txt", "absolute/path.txt")]
    [InlineData("./relative.txt", "relative.txt")]
    [InlineData("double//slash.txt", "double/slash.txt")]
    [InlineData("trailing/", "trailing")]
    public void NormalizeStripsTheDecorationsDifferentWritersAddToAPath(string input,
        string expected)
    {
        Assert.Equal(expected, ArchiveTree.Normalize(input));
    }

    [Fact]
    public void AnEmptyArchiveProducesAnEmptyTreeRatherThanThrowing()
    {
        Assert.Empty(ArchiveTree.Build([]));
    }

    [Fact]
    public void DescendVisitsEveryNodeBeneathAndIncludingItself()
    {
        var roots = ArchiveTree.Build([File("a/b/c.txt"), File("a/d.txt")]);

        // a, a/b, a/b/c.txt, a/d.txt
        Assert.Equal(4, Assert.Single(roots).Descend().Count());
    }
}
