using Microsoft.Extensions.Logging.Abstractions;
using Wlrix.Files.Core;
using Wlrix.Files.ViewModels;
using Xunit;

namespace Wlrix.Files.Tests;

/// <summary>
/// The selection a pane remembers, and what happens to it when the listing changes underneath.
/// </summary>
/// <remarks>
/// No listing control here, which is the point: a pane in a background tab has none either, and
/// it is exactly that pane whose remembered selection used to go stale.
/// </remarks>
public sealed class PaneSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "wlrix-pane-selection", Guid.NewGuid().ToString("N"));

    private readonly FileSystemProvider _provider = new();

    public PaneSelectionTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "1");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "2");
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

    private async Task<PaneViewModel> LoadedPane()
    {
        var pane = new PaneViewModel(_provider, NullLogger<PaneViewModel>.Instance, Location.FromLocalPath(_root));
        await pane.ReloadAsync();
        return pane;
    }

    [Fact]
    public async Task ARowThatLeavesTheListingLeavesTheSelection()
    {
        // A file moved out of this directory, with the selection still pointing at it, is what
        // made the next move fail with "no such file".
        var pane = await LoadedPane();
        var a = pane.Entries.Single(row => row.Name == "a.txt");
        var b = pane.Entries.Single(row => row.Name == "b.txt");
        pane.Selection = [a, b];

        pane.Entries.Remove(a);

        Assert.Equal([b], pane.Selection);
        await pane.CloseAsync();
    }

    [Fact]
    public async Task ReReadingTheDirectoryClearsTheSelection()
    {
        // The rows after a reload are new objects, so nothing remembered could match them anyway.
        var pane = await LoadedPane();
        pane.Selection = [.. pane.Entries];

        await pane.ReloadAsync();

        Assert.Empty(pane.Selection);
        await pane.CloseAsync();
    }

    [Fact]
    public async Task TheSelectionChangeIsAnnouncedSoTheWindowCanFollow()
    {
        var pane = await LoadedPane();
        var a = pane.Entries.Single(row => row.Name == "a.txt");
        pane.Selection = [a];
        var raised = 0;
        pane.SelectionChanged += () => raised++;

        pane.Entries.Remove(a);

        Assert.Equal(1, raised);
        await pane.CloseAsync();
    }

    [Fact]
    public async Task RemovingAnUnselectedRowLeavesTheSelectionAsItWas()
    {
        var pane = await LoadedPane();
        var a = pane.Entries.Single(row => row.Name == "a.txt");
        var b = pane.Entries.Single(row => row.Name == "b.txt");
        IReadOnlyList<FileEntryViewModel> selection = [a];
        pane.Selection = selection;

        pane.Entries.Remove(b);

        Assert.Same(selection, pane.Selection);
        await pane.CloseAsync();
    }
}
