using Wlrix.Files.Core.Search;
using Wlrix.Files.Core.Testing;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>Looking for a file by name, across a tree.</summary>
public class FileSearchTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private sealed class SingleFileSystemProvider(FakeFileSystem fs) : IFileSystemProvider
    {
        public Task<IFileSystem> GetAsync(Location location, CancellationToken cancellationToken) =>
            Task.FromResult<IFileSystem>(fs);
    }

    private static async Task<List<string>> Search(
        FakeFileSystem fs, string root, SearchQuery query, CancellationToken? token = null)
    {
        var names = new List<string>();
        await foreach (var entry in FileSearch.RunAsync(
                           new SingleFileSystemProvider(fs), Location.Parse(root), query, null, token ?? None))
        {
            names.Add(entry.Name);
        }

        return names;
    }

    /// <summary>The full paths, for the tests where which file matters more than its name.</summary>
    private static async Task<List<string>> SearchPaths(FakeFileSystem fs, string root, SearchQuery query)
    {
        var paths = new List<string>();
        await foreach (var entry in FileSearch.RunAsync(
                           new SingleFileSystemProvider(fs), Location.Parse(root), query, null, None))
        {
            paths.Add(entry.Location.Path);
        }

        return paths;
    }

    // --- matching -----------------------------------------------------------

    [Theory]
    [InlineData("report", "quarterly report.txt", true)]
    [InlineData("REPORT", "quarterly report.txt", true)]
    [InlineData("missing", "quarterly report.txt", false)]
    public void PlainTextMatchesAnywhereInTheNameAndIgnoresCase(string text, string name, bool expected) =>
        Assert.Equal(expected, new SearchQuery(text).Matches(name));

    [Theory]
    [InlineData("*.cs", "Program.cs", true)]
    [InlineData("*.cs", "Program.csproj", false)]
    [InlineData("p*.cs", "Program.cs", true)]
    [InlineData("?ain.cs", "Main.cs", true)]
    public void AWildcardMakesItAGlobAndAGlobIsAnchored(string text, string name, bool expected)
    {
        // The half that matters is the false one: *.cs must not match Program.csproj. A glob
        // means on a Unix system what it means everywhere else, and quietly matching loosely
        // would be lying about a familiar notation.
        Assert.True(new SearchQuery(text).IsGlob);
        Assert.Equal(expected, new SearchQuery(text).Matches(name));
    }

    [Fact]
    public void CaseCanBeMadeToMatter()
    {
        Assert.False(new SearchQuery("readme", MatchCase: true).Matches("README"));
        Assert.True(new SearchQuery("README", MatchCase: true).Matches("README.md"));
    }

    [Fact]
    public void AnEmptyQueryIsRefusedRatherThanMatchingEverything()
    {
        Assert.False(new SearchQuery("").IsUsable);
        Assert.False(new SearchQuery("   ").IsUsable);
    }

    // --- walking ------------------------------------------------------------

    [Fact]
    public async Task AMatchIsFoundAtAnyDepth()
    {
        var fs = new FakeFileSystem()
            .AddFile("/work/notes.txt")
            .AddFile("/work/a/notes.txt")
            .AddFile("/work/a/b/c/notes.txt")
            .AddFile("/work/a/other.md");

        var found = await Search(fs, "/work", new SearchQuery("notes"));
        Assert.Equal(3, found.Count);
    }

    [Fact]
    public async Task AResultKeepsTheRealNameOfTheFile()
    {
        // Tempting to rewrite the name here to show where the file was found, and wrong: the
        // name is what a rename dialog fills in and what the type resolves from, and
        // Location.Child refuses one with a separator. Showing the path is the view's job.
        var fs = new FakeFileSystem().AddFile("/work/a/b/notes.txt");

        await foreach (var entry in FileSearch.RunAsync(
                           new SingleFileSystemProvider(fs), Location.Parse("/work"), new SearchQuery("notes"), null, None))
        {
            Assert.Equal("notes.txt", entry.Name);
            Assert.Equal("/work/a/b/notes.txt", entry.Location.Path);
        }
    }

    [Fact]
    public async Task ShallowMatchesComeFirst()
    {
        // Breadth first, and the reason is impatience: a depth-first walk vanishes into the
        // first subdirectory it meets and can spend a long time deep inside something nobody
        // asked about before it looks at the file sitting beside it.
        var fs = new FakeFileSystem()
            .AddFile("/work/deep/deeper/deepest/target.txt")
            .AddFile("/work/target.txt");

        var found = await SearchPaths(fs, "/work", new SearchQuery("target"));
        Assert.Equal(["/work/target.txt", "/work/deep/deeper/deepest/target.txt"], found);
    }

    [Fact]
    public async Task DirectoriesMatchToo()
    {
        var fs = new FakeFileSystem().AddDirectory("/work/reports").AddFile("/work/a/reports/x.txt");

        var found = await SearchPaths(fs, "/work", new SearchQuery("reports"));
        Assert.Equal(["/work/reports", "/work/a/reports"], found);
    }

    [Fact]
    public async Task HiddenThingsAreSkippedUnlessAskedFor()
    {
        var fs = new FakeFileSystem()
            .AddFile("/work/.hidden-notes.txt")
            .AddFile("/work/.cache/notes.txt")
            .AddFile("/work/notes.txt");

        Assert.Equal(["notes.txt"], await Search(fs, "/work", new SearchQuery("notes")));

        var all = await Search(fs, "/work", new SearchQuery("notes", IncludeHidden: true));
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task ALinkedDirectoryIsNotWalkedInto()
    {
        // A link to its own ancestor makes the walk endless. This test finishing is the
        // assertion; the count is a bonus.
        var fs = new FakeFileSystem();
        fs.Capabilities |= FileSystemCapabilities.SymLink;
        fs.AddFile("/work/real/notes.txt");
        await fs.CreateSymlinkAsync(Location.Parse("/work/loop"), "/work", None);

        var found = await SearchPaths(fs, "/work", new SearchQuery("notes"));
        Assert.Equal(["/work/real/notes.txt"], found);
    }

    [Fact]
    public async Task AnUnreadableDirectoryIsSkippedAndTheRestIsStillSearched()
    {
        // A home directory usually has at least one. Stopping there would throw away every
        // result after it.
        var fs = new FakeFileSystem()
            .AddFile("/work/locked/notes.txt")
            .AddFile("/work/open/notes.txt");
        fs.FailAlways("/work/locked", FileErrorKind.AccessDenied);

        var outcomes = new List<SearchOutcome>();
        var found = new List<string>();
        await foreach (var entry in FileSearch.RunAsync(
                           new SingleFileSystemProvider(fs), Location.Parse("/work"),
                           new SearchQuery("notes"), outcomes.Add, None))
        {
            found.Add(entry.Location.Path);
        }

        Assert.Equal(["/work/open/notes.txt"], found);
        Assert.True(outcomes[^1].Unreadable > 0);
    }

    [Fact]
    public async Task ASearchStopsAtItsLimitAndSaysThatIsWhyItStopped()
    {
        // "e" from the root of a filesystem matches millions. The honest answer is the first
        // few and a note, not a window that fills memory until it dies.
        var fs = new FakeFileSystem();
        for (var i = 0; i < 50; i++)
            fs.AddFile($"/work/note{i}.txt");

        var outcomes = new List<SearchOutcome>();
        var found = new List<string>();
        await foreach (var entry in FileSearch.RunAsync(
                           new SingleFileSystemProvider(fs), Location.Parse("/work"),
                           new SearchQuery("note", MaxResults: 10), outcomes.Add, None))
        {
            found.Add(entry.Name);
        }

        Assert.Equal(10, found.Count);
        Assert.True(outcomes[^1].Truncated);
    }

    [Fact]
    public async Task CancelingStopsTheWalk()
    {
        var fs = new FakeFileSystem();
        for (var i = 0; i < 200; i++)
            fs.AddFile($"/work/dir{i}/note.txt");

        using var cts = new CancellationTokenSource();
        var seen = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in FileSearch.RunAsync(
                               new SingleFileSystemProvider(fs), Location.Parse("/work"),
                               new SearchQuery("note"), null, cts.Token))
            {
                if (++seen == 3)
                    await cts.CancelAsync();
            }
        });

        Assert.True(seen < 200);
    }

    [Fact]
    public async Task AnEmptyQueryFindsNothingRatherThanEverything()
    {
        var fs = new FakeFileSystem().AddFile("/work/notes.txt");
        Assert.Empty(await Search(fs, "/work", new SearchQuery("  ")));
    }
}
