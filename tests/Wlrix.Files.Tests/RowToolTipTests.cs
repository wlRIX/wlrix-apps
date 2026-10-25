using Wlrix.Files.Core;
using Wlrix.Files.ViewModels;
using Xunit;

namespace Wlrix.Files.Tests;

/// <summary>What hovering a row says.</summary>
public class RowToolTipTests
{
    private static FileEntryViewModel Row(string name, bool directory = false, long size = 0) =>
        new(new FileEntry
        {
            Location = Location.Parse("/base").Child(name),
            Name = name,
            Kind = directory ? FileKind.Directory : FileKind.File,
            Size = size
        });

    [Fact]
    public void AFileShowsItsWholeNameAndItsSize()
    {
        var name = "a name far too long to fit under an icon in two lines of label.txt";
        var row = Row(name, size: 2048);

        Assert.Equal($"{name}\n{row.SizeText}", row.ToolTipText);
        Assert.NotEmpty(row.SizeText);
    }

    [Fact]
    public void ADirectoryShowsOnlyItsName()
    {
        // Its size needs a walk of everything inside it, which is what Properties is for.
        Assert.Equal("photos", Row("photos", directory: true).ToolTipText);
    }

    [Fact]
    public void ASearchResultShowsWhereItWasFound()
    {
        // The row shows the path relative to the search, so the tip shows the same, in full.
        var row = Row("notes.txt", size: 1);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.DisplayName = "projects/old/notes.txt";

        Assert.StartsWith("projects/old/notes.txt\n", row.ToolTipText);
        Assert.Contains(nameof(FileEntryViewModel.ToolTipText), changed);
    }
}
