using Wlrix.Files.Core.Metadata;
using Wlrix.Files.Core.Operations;
using Wlrix.Files.Core.Search;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// Every tree walk against the real disk, with a link that points back up the tree.
/// </summary>
/// <remarks>
/// The fake filesystem reports a link to a directory as a link and nothing else, but the local
/// one reports it as a directory that is also a link, because that is how it lists and opens. A
/// walk that was only ever tested against the fake can then pass its own loop test and still
/// follow every link on a real disk. Followed, a link to an ancestor never ends: a copy planned
/// for minutes without writing a byte, until it had taken 80 GB. Each walk here gets a deadline,
/// so a regression fails the test rather than hanging it.
/// </remarks>
public class SymlinkLoopTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private static TemporaryDirectory LoopedTree()
    {
        var dir = new TemporaryDirectory();
        dir.File("tree/real/file.txt", "12345");
        File.CreateSymbolicLink(Path.Combine(dir.Path, "tree", "loop"), Path.Combine(dir.Path, "tree"));
        File.CreateSymbolicLink(Path.Combine(dir.Path, "tree", "real", "up"), "..");
        return dir;
    }

    [Fact]
    public async Task ACopyScanPlansALinkAsOneItemAndDoesNotWalkIntoIt()
    {
        using var dir = LoopedTree();
        dir.Directory_("dst");
        using var deadline = new CancellationTokenSource(Deadline);

        var plan = await new OperationJob(new FileSystemProvider()).ScanAsync(
            FileOperation.Copy([Location.FromLocalPath(Path.Combine(dir.Path, "tree"))],
                Location.FromLocalPath(Path.Combine(dir.Path, "dst"))),
            null, deadline.Token);

        // tree, tree/real, tree/real/file.txt, tree/real/up, tree/loop
        Assert.Equal(5, plan.Items.Count);
        Assert.Equal(5, plan.TotalBytes);
        Assert.Equal(
            ["loop", "up"],
            plan.Items.Where(item => item.LinkTarget is not null).Select(item => item.Source.Name).Order());
    }

    [Fact]
    public async Task ACopyReproducesLinksAsLinks()
    {
        using var dir = LoopedTree();
        dir.Directory_("dst");
        using var deadline = new CancellationTokenSource(Deadline);

        var result = await new OperationJob(new FileSystemProvider()).RunAsync(
            FileOperation.Copy([Location.FromLocalPath(Path.Combine(dir.Path, "tree"))],
                Location.FromLocalPath(Path.Combine(dir.Path, "dst"))),
            null, deadline.Token);

        Assert.True(result.Succeeded);
        Assert.Equal("12345", File.ReadAllText(Path.Combine(dir.Path, "dst", "tree", "real", "file.txt")));
        // The text of each link is kept as it was, relative or absolute.
        Assert.Equal(Path.Combine(dir.Path, "tree"), new FileInfo(Path.Combine(dir.Path, "dst", "tree", "loop")).LinkTarget);
        Assert.Equal("..", new FileInfo(Path.Combine(dir.Path, "dst", "tree", "real", "up")).LinkTarget);
    }

    [Fact]
    public async Task CopyingALinkItselfCopiesTheLink()
    {
        using var dir = LoopedTree();
        dir.Directory_("dst");
        using var deadline = new CancellationTokenSource(Deadline);

        var result = await new OperationJob(new FileSystemProvider()).RunAsync(
            FileOperation.Copy([Location.FromLocalPath(Path.Combine(dir.Path, "tree", "loop"))],
                Location.FromLocalPath(Path.Combine(dir.Path, "dst"))),
            null, deadline.Token);

        Assert.True(result.Succeeded);
        Assert.Equal(Path.Combine(dir.Path, "tree"), new FileInfo(Path.Combine(dir.Path, "dst", "loop")).LinkTarget);
    }

    [Fact]
    public async Task MeasuringATreeDoesNotFollowItsLinks()
    {
        using var dir = LoopedTree();
        using var deadline = new CancellationTokenSource(Deadline);

        var size = await DirectoryUsage.MeasureAsync(
            new FileSystemProvider(), [Location.FromLocalPath(Path.Combine(dir.Path, "tree"))], null, deadline.Token);

        Assert.True(size.Complete);
        Assert.Equal(1, size.Directories);  // real/
        Assert.Equal(3, size.Files);        // file.txt and the two links
    }

    [Fact]
    public async Task SearchingATreeDoesNotFollowItsLinks()
    {
        using var dir = LoopedTree();
        using var deadline = new CancellationTokenSource(Deadline);

        var found = new List<string>();
        await foreach (var entry in FileSearch.RunAsync(
                           new FileSystemProvider(), Location.FromLocalPath(Path.Combine(dir.Path, "tree")),
                           new SearchQuery("file"), null, deadline.Token))
        {
            found.Add(entry.Location.Path);
        }

        Assert.Equal([Path.Combine(dir.Path, "tree", "real", "file.txt")], found);
    }
}
