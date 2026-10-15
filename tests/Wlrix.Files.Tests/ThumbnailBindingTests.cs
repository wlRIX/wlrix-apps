using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using Wlrix.Files.Core;
using Wlrix.Files.ViewModels;
using Xunit;

namespace Wlrix.Files.Tests;

/// <summary>
/// A thumbnail reaching a row that is already on screen.
/// </summary>
/// <remarks>
/// Rows are bound before their thumbnails exist, so the picture only ever arrives as a change. The
/// icon view once heard that the row had a thumbnail but not what it was: it drew the frame around
/// an empty picture until the row was bound again by a refresh, a tab switch or a return to the
/// directory.
/// </remarks>
public class ThumbnailBindingTests
{
    [Fact]
    public void EveryPropertyAViewBindsIsAnnouncedWhenAThumbnailArrives()
    {
        var row = new FileEntryViewModel(new FileEntry
        {
            Location = Location.Parse("/base/photo.png"),
            Name = "photo.png",
            Kind = FileKind.File
        });
        var announced = new List<string?>();
        row.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        row.Thumbnail = PlaceholderBitmap();

        // The icon view binds the picture and whether to frame it; the details view binds Preview.
        Assert.Contains(nameof(FileEntryViewModel.Thumbnail), announced);
        Assert.Contains(nameof(FileEntryViewModel.HasThumbnail), announced);
        Assert.Contains(nameof(FileEntryViewModel.Preview), announced);
        Assert.Contains(nameof(FileEntryViewModel.ShowsIcon), announced);
    }

    /// <summary>A bitmap that is only ever compared by reference.</summary>
    /// <remarks>
    /// Uninitialized, because a real one needs a running platform and a headless one would take
    /// the UI thread from the tests that drain the dispatcher themselves. Bitmap has no
    /// finalizer, so nothing ever touches what was never set up.
    /// </remarks>
    private static Bitmap PlaceholderBitmap() =>
        (Bitmap)RuntimeHelpers.GetUninitializedObject(typeof(Bitmap));
}
