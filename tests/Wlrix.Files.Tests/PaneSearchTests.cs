using Microsoft.Extensions.Logging.Abstractions;
using Wlrix.Files.Core;
using Wlrix.Files.Core.Search;
using Wlrix.Files.ViewModels;
using Xunit;

namespace Wlrix.Files.Tests;

/// <summary>
/// The pane showing search results instead of a directory.
/// </summary>
/// <remarks>
/// Against a real temporary directory rather than a fake, because <c>PaneViewModel</c> takes
/// the concrete provider — and because the thing most worth pinning here is that results and
/// directories are the same list, which a fake source would make trivially true.
/// </remarks>
public sealed class PaneSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "wlrix-pane-search", Guid.NewGuid().ToString("N"));

    private readonly FileSystemProvider _provider = new();

    public PaneSearchTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "notes"));
        Directory.CreateDirectory(Path.Combine(_root, "deep", "deeper"));
        File.WriteAllText(Path.Combine(_root, "top.txt"), "1");
        File.WriteAllText(Path.Combine(_root, "deep", "buried.txt"), "2");
        File.WriteAllText(Path.Combine(_root, "deep", "deeper", "buried.md"), "3");
        File.WriteAllText(Path.Combine(_root, ".hidden.txt"), "4");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private PaneViewModel Pane() =>
        new(_provider, NullLogger<PaneViewModel>.Instance, Location.FromLocalPath(_root));

    [Fact]
    public async Task ResultsReplaceTheListingAndAreNamedByWhereTheyAre()
    {
        var pane = Pane();
        await pane.SearchAsync(new SearchQuery("buried"));

        Assert.True(pane.IsSearching);

        // The row shows where it was found...
        Assert.Equal(
            ["deep/buried.txt", "deep/deeper/buried.md"],
            pane.Entries.Select(row => row.DisplayName).Order().ToArray());

        // ...and still knows what the file is actually called, which is what a rename needs
        // and what Location.Child would refuse if the path had been written into the name.
        Assert.Equal(
            ["buried.md", "buried.txt"],
            pane.Entries.Select(row => row.Name).Order().ToArray());
    }

    [Fact]
    public async Task AResultStillPointsAtTheFileItself()
    {
        // What makes a result behave like a file: opening, copying and properties all work
        // because the row addresses a real location, whatever the name column shows.
        var pane = Pane();
        await pane.SearchAsync(new SearchQuery("buried.txt"));

        var row = Assert.Single(pane.Entries);
        Assert.Equal(Path.Combine(_root, "deep", "buried.txt"), row.Location.Path);
    }

    [Fact]
    public async Task NavigatingPutsTheDirectoryBack()
    {
        // The failure this prevents is a list that has nothing to do with the path bar above
        // it, which is what stale results would be.
        var pane = Pane();
        await pane.SearchAsync(new SearchQuery("buried"));
        Assert.True(pane.IsSearching);

        await pane.NavigateAsync(Location.FromLocalPath(Path.Combine(_root, "deep")));

        Assert.False(pane.IsSearching);
        Assert.Contains("buried.txt", pane.Entries.Select(row => row.Name));
        Assert.Contains("deeper", pane.Entries.Select(row => row.Name));
        await pane.CloseAsync();
    }

    [Fact]
    public async Task GoingToTheDirectoryAlreadyShownAlsoPutsItBack()
    {
        // Clicking the current directory in the path bar while results are up means "give me
        // the directory back". Nobody clicks it to re-run a search.
        var pane = Pane();
        await pane.SearchAsync(new SearchQuery("buried"));

        await pane.NavigateAsync(Location.FromLocalPath(_root));

        Assert.False(pane.IsSearching);
        Assert.Contains("top.txt", pane.Entries.Select(row => row.Name));
        await pane.CloseAsync();
    }

    [Fact]
    public async Task ShowHiddenGovernsASearchToo()
    {
        // Otherwise the View menu and the query could contradict each other, and the one the
        // user can see is the menu.
        var pane = Pane();
        await pane.SearchAsync(new SearchQuery("hidden"));
        Assert.Empty(pane.Entries);

        pane.ShowHidden = true;
        await pane.SearchAsync(new SearchQuery("hidden"));
        Assert.Contains(".hidden.txt", pane.Entries.Select(row => row.DisplayName));
    }

    [Fact]
    public async Task TheStatusSaysWhatWasFoundAndWhatWasNot()
    {
        var pane = Pane();

        await pane.SearchAsync(new SearchQuery("buried"));
        Assert.Contains("2", pane.Status);
        Assert.Contains("buried", pane.Status);

        await pane.SearchAsync(new SearchQuery("nothing-like-this"));
        Assert.Contains("nothing-like-this", pane.Status);
    }
}
