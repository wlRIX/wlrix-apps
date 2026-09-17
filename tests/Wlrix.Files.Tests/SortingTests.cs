using Wlrix.Files.Core;
using Wlrix.Files.Core.State;
using Wlrix.Files.ViewModels;
using Xunit;

namespace Wlrix.Files.Tests;

/// <summary>
/// The ordering rules, which are fiddlier than they look: grouping is orthogonal to
/// direction, and every key falls back to the name so equal values stay readable.
/// </summary>
/// <remarks>
/// No Avalonia here. The comparer is a static function over plain objects precisely so it
/// can be tested without a dispatcher, which CI has no way to provide.
/// </remarks>
public class SortingTests
{
    private static FileEntryViewModel Row(string name, bool directory = false, long size = 0, string? modified = null) =>
        new(new FileEntry
        {
            Location = Location.Parse("/base").Child(name),
            Name = name,
            Kind = directory ? FileKind.Directory : FileKind.File,
            Size = size,
            Modified = modified is null ? null : DateTimeOffset.Parse(modified, System.Globalization.CultureInfo.InvariantCulture)
        });

    private static List<FileEntryViewModel> Sorted(List<FileEntryViewModel> rows, SortKey key, bool descending, bool foldersFirst)
    {
        rows.Sort(PaneViewModel.Comparer(key, descending, foldersFirst));
        return rows;
    }

    [Fact]
    public void FoldersComeFirstAndStayFirstWhenTheOrderIsReversed()
    {
        // The case that is easy to get wrong: reversing must flip the names within each
        // group, not float the files above the folders.
        var rows = new List<FileEntryViewModel> { Row("b.txt"), Row("a-dir", directory: true), Row("a.txt"), Row("b-dir", directory: true) };

        Assert.Equal(["a-dir", "b-dir", "a.txt", "b.txt"],
            Sorted([.. rows], SortKey.Name, descending: false, foldersFirst: true).Select(r => r.Name));

        Assert.Equal(["b-dir", "a-dir", "b.txt", "a.txt"],
            Sorted([.. rows], SortKey.Name, descending: true, foldersFirst: true).Select(r => r.Name));
    }

    [Fact]
    public void WithoutFoldersFirstDirectoriesSortAmongTheFiles()
    {
        var rows = new List<FileEntryViewModel> { Row("c.txt"), Row("b-dir", directory: true), Row("a.txt") };
        Assert.Equal(["a.txt", "b-dir", "c.txt"],
            Sorted(rows, SortKey.Name, descending: false, foldersFirst: false).Select(r => r.Name));
    }

    [Fact]
    public void NameSortingIsCaseInsensitiveSoTheListReadsAlphabetically()
    {
        // Ordinal would put every capital ahead of every lowercase letter, which looks
        // broken in a directory of mixed-case names.
        var rows = new List<FileEntryViewModel> { Row("banana"), Row("Apple"), Row("cherry") };
        Assert.Equal(["Apple", "banana", "cherry"],
            Sorted(rows, SortKey.Name, descending: false, foldersFirst: true).Select(r => r.Name));
    }

    [Fact]
    public void EqualValuesFallBackToTheNameRatherThanArrivalOrder()
    {
        // Every file here is the same size; without the tie-break the listing would come
        // out in whatever order the filesystem happened to return.
        var rows = new List<FileEntryViewModel> { Row("c", size: 10), Row("a", size: 10), Row("b", size: 10) };
        Assert.Equal(["a", "b", "c"],
            Sorted(rows, SortKey.Size, descending: false, foldersFirst: true).Select(r => r.Name));
    }

    [Fact]
    public void SortingBySizeOrdersNumericallyNotAsText()
    {
        var rows = new List<FileEntryViewModel> { Row("big", size: 1000), Row("small", size: 9), Row("mid", size: 100) };
        Assert.Equal(["small", "mid", "big"],
            Sorted(rows, SortKey.Size, descending: false, foldersFirst: true).Select(r => r.Name));
    }

    [Fact]
    public void SortingByDatePutsEntriesWithNoTimestampFirst()
    {
        // A null modified time is "unknown", and Nullable.Compare orders null lowest. It
        // must not throw, which is the real point.
        var rows = new List<FileEntryViewModel>
        {
            Row("newer", modified: "2026-01-02T00:00:00Z"),
            Row("unknown"),
            Row("older", modified: "2020-01-02T00:00:00Z")
        };
        Assert.Equal(["unknown", "older", "newer"],
            Sorted(rows, SortKey.Modified, descending: false, foldersFirst: true).Select(r => r.Name));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KiB")]
    [InlineData(1536, "1.5 KiB")]
    [InlineData(1048576, "1.0 MiB")]
    [InlineData(1073741824, "1.0 GiB")]
    public void SizesAreFormattedIrixStyle(long bytes, string expected) =>
        Assert.Equal(expected, FileEntryViewModel.FormatSize(bytes));

    [Fact]
    public void JustUnderAKibibyteCarriesToTheNextUnitRatherThanReadingAs1024()
    {
        // The reason FormatSize loops instead of using a logarithm: the log form rounds
        // this to "1024.0 KiB", and the seam is visible in a sorted listing.
        Assert.Equal("1.0 MiB", FileEntryViewModel.FormatSize(1024 * 1024 - 1));
    }

    [Fact]
    public void ADirectoryShowsNoSizeBecauseItsInodeSizeMeansNothingToAUser()
    {
        var row = Row("d", directory: true, size: 4096);
        Assert.Equal(string.Empty, row.SizeText);
    }
}
