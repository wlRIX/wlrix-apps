using Microsoft.Extensions.Logging.Abstractions;
using Wlrix.Files.Core;
using Wlrix.Files.ViewModels;
using Xunit;

namespace Wlrix.Files.Tests;

/// <summary>
/// A tab holding two panes, and which of them the window acts on.
/// </summary>
/// <remarks>
/// The tab and its panes, without the window: <c>TabViewModel</c> takes a window but only to
/// hand it to the listing, and none of the behaviour here goes near one. What matters is that
/// "the pane" the rest of the application reads is the one that was last clicked in, because
/// Paste and Delete both read it.
/// </remarks>
public sealed class SplitPaneTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "wlrix-split", Guid.NewGuid().ToString("N"));

    private readonly FileSystemProvider _provider = new();

    public SplitPaneTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "left"));
        Directory.CreateDirectory(Path.Combine(_root, "right"));
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

    private PaneViewModel NewPane(Location at) =>
        new(_provider, NullLogger<PaneViewModel>.Instance, at);

    private TabViewModel Tab(out PaneViewModel first)
    {
        first = NewPane(Location.FromLocalPath(_root));
        return new TabViewModel(null!, first);
    }

    [Fact]
    public void ATabStartsWithOnePane()
    {
        var tab = Tab(out var pane);

        Assert.False(tab.IsSplit);
        Assert.Single(tab.Panes);
        Assert.Same(pane, tab.ActivePane);
        Assert.Same(pane, tab.Pane);
    }

    [Fact]
    public void SplittingAddsASecondPaneWhereTheFirstOneIs()
    {
        // A split is nearly always the first half of "copy this somewhere else", so starting
        // at the same directory means one navigation rather than two.
        var tab = Tab(out var first);
        var second = tab.Split(NewPane);

        Assert.NotNull(second);
        Assert.True(tab.IsSplit);
        Assert.Equal(first.Location, second!.Location);

        // The new one is active, because it is the one about to be pointed somewhere.
        Assert.Same(second, tab.ActivePane);
    }

    [Fact]
    public void ATabWillNotSplitTwice()
    {
        // Two is the limit. A third pane adds no answer to "where is this being copied to" and
        // turns "which pane does Paste use" from a glance into a hunt.
        var tab = Tab(out _);
        Assert.NotNull(tab.Split(NewPane));
        Assert.Null(tab.Split(NewPane));
        Assert.Equal(2, tab.Panes.Count);
    }

    [Fact]
    public void TheWindowActsOnWhicheverPaneWasClickedIn()
    {
        var tab = Tab(out var first);
        var second = tab.Split(NewPane)!;

        first.Activate();
        Assert.Same(first, tab.Pane);

        second.Activate();
        Assert.Same(second, tab.Pane);
    }

    [Fact]
    public void APaneFromAnotherTabCannotBecomeActive()
    {
        // Otherwise the window ends up acting on a directory nobody is looking at.
        var tab = Tab(out var mine);
        var stranger = NewPane(Location.FromLocalPath(_root));

        tab.SetActivePane(stranger);
        Assert.Same(mine, tab.ActivePane);
    }

    [Fact]
    public void ClosingTheSplitKeepsThePaneBeingWorkedIn()
    {
        // Not "the second one goes". Closing the split while working on the right and being
        // returned to the left is the kind of surprise that loses somebody their place.
        var tab = Tab(out var first);
        var second = tab.Split(NewPane)!;
        second.Activate();

        var closed = tab.Unsplit();

        Assert.Same(first, closed);
        Assert.Same(second, tab.ActivePane);
        Assert.False(tab.IsSplit);
        Assert.Single(tab.Panes);
    }

    [Fact]
    public void TheOutlineIsOnlyDrawnWhileSplit()
    {
        // With one pane the answer is never in doubt, and an outline around the only listing
        // in the window is decoration that has to be explained.
        var tab = Tab(out var first);
        Assert.True(first.IsActive);
        Assert.False(first.ShowActiveOutline);

        var second = tab.Split(NewPane)!;
        Assert.True(second.ShowActiveOutline);
        Assert.False(first.ShowActiveOutline);

        tab.Unsplit();
        Assert.False(second.ShowActiveOutline);
    }

    [Fact]
    public void EachPaneRemembersItsOwnSelection()
    {
        // The TabControl rebuilds the listing on every switch, so a selection that lived on
        // the tab would be one selection for two listings.
        var tab = Tab(out var first);
        var second = tab.Split(NewPane)!;

        var a = new FileEntryViewModel(new FileEntry
        {
            Location = Location.Parse("/x/a"), Name = "a", Kind = FileKind.File
        });
        var b = new FileEntryViewModel(new FileEntry
        {
            Location = Location.Parse("/x/b"), Name = "b", Kind = FileKind.File
        });

        first.Selection = [a];
        second.Selection = [b];

        first.Activate();
        Assert.Equal([a], tab.Selection);
        second.Activate();
        Assert.Equal([b], tab.Selection);
    }

    [Fact]
    public void TheTitleFollowsWhicheverPaneIsActive()
    {
        var tab = Tab(out var first);
        var second = tab.Split(NewPane)!;

        second.ResetTo(Location.FromLocalPath(Path.Combine(_root, "right")));
        Assert.Equal("right", tab.Title);

        first.Activate();
        Assert.Equal(Path.GetFileName(_root), tab.Title);
    }
}
