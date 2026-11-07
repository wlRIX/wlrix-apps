using Avalonia;
using Microsoft.Extensions.Logging.Abstractions;
using Wlrix.Files.Core;
using Wlrix.Files.ViewModels;
using Xunit;

namespace Wlrix.Files.Tests;

/// <summary>
/// What a directory looks like when the history brings it back, and when going up arrives in it.
/// </summary>
/// <remarks>
/// No listing control, as with <see cref="PaneSelectionTests"/>: the pane decides what is to be
/// put back, and the listing only carries it out.
/// </remarks>
public sealed class PaneHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "wlrix-pane-history", Guid.NewGuid().ToString("N"));

    private readonly FileSystemProvider _provider = new();

    public PaneHistoryTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "home", "user", "Downloads"));
        Directory.CreateDirectory(Path.Combine(_root, "home", "other"));
        File.WriteAllText(Path.Combine(_root, "home", "user", "a.txt"), "1");
        File.WriteAllText(Path.Combine(_root, "home", "user", "b.txt"), "2");
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

    private Location At(params string[] parts) => Location.FromLocalPath(Path.Combine([_root, .. parts]));

    private async Task<PaneViewModel> PaneAt(Location start)
    {
        var pane = new PaneViewModel(_provider, NullLogger<PaneViewModel>.Instance, start);
        await pane.ReloadAsync();
        return pane;
    }

    [Fact]
    public async Task GoingUpSeveralLevelsSelectsTheDirectoryThatLeadsBack()
    {
        // The path bar's crumb for home, from inside home/user/Downloads: user is the way back.
        var pane = await PaneAt(At("home", "user", "Downloads"));

        await pane.NavigateAsync(At("home"));

        Assert.Equal(At("home", "user"), Assert.Single(pane.Selection).Location);
        await pane.CloseAsync();
    }

    [Fact]
    public async Task GoingUpOneLevelSelectsTheDirectoryJustLeft()
    {
        var pane = await PaneAt(At("home", "user", "Downloads"));
        IReadOnlyList<FileEntryViewModel>? shown = null;
        pane.RestoreRequested += (rows, _) => shown = rows;

        await pane.GoUpAsync();

        Assert.Equal(At("home", "user", "Downloads"), Assert.Single(pane.Selection).Location);
        // Brought on screen as well as selected, since there is no offset to go back to.
        Assert.Equal(At("home", "user", "Downloads"), Assert.Single(shown!).Location);
        await pane.CloseAsync();
    }

    [Fact]
    public async Task GoingSidewaysSelectsNothing()
    {
        var pane = await PaneAt(At("home", "user"));

        await pane.NavigateAsync(At("home", "other"));

        Assert.Empty(pane.Selection);
        await pane.CloseAsync();
    }

    [Fact]
    public async Task BackAndForwardPutTheSelectionAndScrollOffsetBack()
    {
        var pane = await PaneAt(At("home", "user"));
        pane.Selection = [.. pane.Entries.Where(row => row.Name is "a.txt" or "b.txt")];
        pane.ScrollOffset = new Vector(0, 120);
        Vector? restoredTo = null;
        pane.RestoreRequested += (_, offset) => restoredTo = offset;

        await pane.NavigateAsync(At("home", "other"));
        Assert.Equal(default, pane.ScrollOffset);
        pane.ScrollOffset = new Vector(0, 40);

        await pane.GoBackAsync();
        Assert.Equal(["a.txt", "b.txt"], pane.Selection.Select(row => row.Name).Order());
        Assert.Equal(new Vector(0, 120), pane.ScrollOffset);
        Assert.Equal(new Vector(0, 120), restoredTo);

        await pane.GoForwardAsync();
        Assert.Equal(new Vector(0, 40), pane.ScrollOffset);
        await pane.CloseAsync();
    }

    [Fact]
    public async Task BackIntoAParentLeftWithNothingSelectedShowsTheWayDown()
    {
        // Arrived at from the path bar rather than by opening a folder, so nothing was selected
        // when it was left; going back to it is still a step up, and is treated as one.
        var pane = await PaneAt(At("home"));
        await pane.NavigateAsync(At("home", "user", "Downloads"));

        await pane.GoBackAsync();

        Assert.Equal(At("home", "user"), Assert.Single(pane.Selection).Location);
        await pane.CloseAsync();
    }

    [Fact]
    public async Task ASelectionThatIsGoneByTheTimeYouComeBackIsDropped()
    {
        var pane = await PaneAt(At("home", "user"));
        pane.Selection = [pane.Entries.Single(row => row.Name == "a.txt")];
        await pane.NavigateAsync(At("home", "other"));
        File.Delete(Path.Combine(_root, "home", "user", "a.txt"));

        await pane.GoBackAsync();

        Assert.Empty(pane.Selection);
        await pane.CloseAsync();
    }
}
