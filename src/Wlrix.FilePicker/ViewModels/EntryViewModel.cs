using System.ComponentModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Wlrix.Files.Core;
using Wlrix.FilePicker.Services;

namespace Wlrix.FilePicker.ViewModels;

/// <summary>One row of the chooser's listing.</summary>
/// <remarks>
/// A plain object implementing <see cref="INotifyPropertyChanged"/> by hand rather than a
/// <c>ReactiveObject</c>, for the reason the file manager's own row type gives: a directory can
/// hold a hundred thousand of these, only the icon ever changes, and ReactiveUI's machinery
/// allocates per object. The formatted strings are computed once here rather than in a getter,
/// because virtualization would otherwise run the getter again on every scroll pass.
/// </remarks>
public sealed class EntryViewModel : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs IconChanged = new(nameof(Icon));
    private static readonly PropertyChangedEventArgs DirectoryGlyphChanged = new(nameof(ShowDirectoryGlyph));
    private static readonly PropertyChangedEventArgs FileGlyphChanged = new(nameof(ShowFileGlyph));

    private Bitmap? _icon;

    public EntryViewModel(FileEntry entry)
    {
        Entry = entry;
        SizeText = entry.IsDirectory ? string.Empty : FileSizes.Binary(entry.Size);
        ModifiedText = entry.Modified is { } modified
            ? modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
            : string.Empty;
    }

    public FileEntry Entry { get; }

    public Location Location => Entry.Location;

    public string Name => Entry.Name;

    public bool IsDirectory => Entry.IsDirectory;

    public string SizeText { get; }

    public string ModifiedText { get; }

    /// <summary>The icon for this entry, or null until one has been rendered.</summary>
    /// <remarks>
    /// Null draws the placeholder geometry rather than a gap, so a row is never visibly
    /// incomplete while its type's icon is being rasterized.
    /// </remarks>
    public Bitmap? Icon
    {
        get => _icon;
        private set
        {
            if (ReferenceEquals(_icon, value))
                return;
            _icon = value;
            PropertyChanged?.Invoke(this, IconChanged);
            // The placeholders stand in *until* the icon arrives, so they have to go when it
            // does. Bound to IsDirectory alone they never went, and every row drew its real
            // icon with a placeholder sitting beside it.
            PropertyChanged?.Invoke(this, DirectoryGlyphChanged);
            PropertyChanged?.Invoke(this, FileGlyphChanged);
        }
    }

    public bool ShowDirectoryGlyph => _icon is null && IsDirectory;

    public bool ShowFileGlyph => _icon is null && !IsDirectory;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Asks for this row's icon, filling <see cref="Icon"/> in when it arrives.</summary>
    /// <remarks>
    /// Called when a container is realized. The "still wanted" check is what makes that safe
    /// under virtualization: a render finishing after its row has been recycled for another
    /// entry must not paint the wrong icon into it.
    /// </remarks>
    public void EnsureIcon(IconService icons, int size)
    {
        if (_icon is not null)
            return;
        icons.Request(Entry, size, bitmap => Icon = bitmap, () => _icon is null);
    }
}
