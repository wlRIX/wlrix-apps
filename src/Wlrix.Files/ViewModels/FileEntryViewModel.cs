using System.ComponentModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Wlrix.Files.Core;
using Wlrix.Files.Core.Thumbnails;
using Wlrix.Files.Localization;
using Wlrix.Files.Services;

namespace Wlrix.Files.ViewModels;

/// <summary>One row of the listing.</summary>
/// <remarks>
/// A plain object, not a ReactiveObject: there are a hundred thousand of these in a large
/// directory, nothing about an entry changes after it is read, and the change-tracking
/// machinery would cost real memory for no benefit. When a file changes on disk the watcher
/// replaces the row rather than mutating it.
///
/// <para>
/// The formatted strings are computed once in the constructor rather than in a property
/// getter. Virtualization means a getter would run again on every scroll pass over the same
/// row, and <see cref="DateTime"/> formatting is not cheap.
/// </para>
///
/// <para>
/// It implements <see cref="INotifyPropertyChanged"/> by hand for the one property that
/// changes — the icon, which arrives after a render — rather than deriving from
/// <c>ReactiveObject</c>. At this population the difference is real: ReactiveUI's machinery
/// allocates per object, and none of the other properties ever change.
/// </para>
/// </remarks>
public sealed class FileEntryViewModel : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs DisplayNameChanged = new(nameof(DisplayName));
    private static readonly PropertyChangedEventArgs IconChanged = new(nameof(Icon));
    private static readonly PropertyChangedEventArgs DropTargetChanged = new(nameof(IsDropTarget));
    private static readonly PropertyChangedEventArgs PreviewChanged = new(nameof(Preview));
    private static readonly PropertyChangedEventArgs DirectoryGlyphChanged = new(nameof(ShowDirectoryGlyph));
    private static readonly PropertyChangedEventArgs FileGlyphChanged = new(nameof(ShowFileGlyph));

    private Bitmap? _icon;
    private Bitmap? _thumbnail;
    private bool _isDropTarget;

    private string? _displayName;

    public FileEntryViewModel(FileEntry entry)
    {
        Entry = entry;
        SizeText = entry.IsDirectory ? string.Empty : FormatSize(entry.Size);
        KindText = Strings.Kind(entry.Kind);
        ModifiedText = entry.Modified is { } modified
            ? modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
            : string.Empty;
    }

    public FileEntry Entry { get; }

    public Location Location => Entry.Location;

    public string Name => Entry.Name;

    /// <summary>
    /// What the row shows, which is the file's name everywhere except in search results.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Name"/> on purpose. Ten files called <c>notes.txt</c> in ten
    /// directories are ten identical rows, and the path is the only thing telling them apart —
    /// but the name itself has to stay the real one, because it is what a rename fills in and
    /// what the type is resolved from, and <c>Location.Child</c> refuses one with a separator
    /// in it.
    /// </remarks>
    public string DisplayName
    {
        get => _displayName ?? Entry.Name;
        set
        {
            _displayName = value;
            PropertyChanged?.Invoke(this, DisplayNameChanged);
        }
    }

    public bool IsDirectory => Entry.IsDirectory;

    public string SizeText { get; }

    public string KindText { get; }

    public string ModifiedText { get; }

    /// <summary>
    /// The icon for this entry, or null until one has been rendered.
    /// </summary>
    /// <remarks>
    /// Null renders as the drawn placeholder geometry rather than as a gap, so a row is never
    /// visibly incomplete while its type's icon is being rasterized.
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
            PropertyChanged?.Invoke(this, PreviewChanged);
            // The drawn placeholders stand in *until* the icon arrives, so they have to go
            // when it does. Bound to IsDirectory alone they never went, and every row in the
            // details view drew its real icon with a placeholder sitting beside it.
            PropertyChanged?.Invoke(this, DirectoryGlyphChanged);
            PropertyChanged?.Invoke(this, FileGlyphChanged);
        }
    }

    /// <summary>
    /// A picture of the file's own contents, once one has been made.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Icon"/> rather than overwriting it, so turning thumbnails off
    /// puts the type icon back without re-reading the directory — and so a row that is both
    /// waiting for a thumbnail and showing its type icon does not flicker through empty.
    /// </remarks>
    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value))
                return;
            _thumbnail = value;
            PropertyChanged?.Invoke(this, PreviewChanged);
            PropertyChanged?.Invoke(this, DirectoryGlyphChanged);
            PropertyChanged?.Invoke(this, FileGlyphChanged);
        }
    }

    /// <summary>What the row actually shows: the file itself if we have it, else its type.</summary>
    public Bitmap? Preview => _thumbnail ?? _icon;

    /// <summary>Whether to draw the folder placeholder: a directory with nothing to show yet.</summary>
    public bool ShowDirectoryGlyph => Preview is null && IsDirectory;

    /// <summary>Whether to draw the document placeholder: anything else with nothing yet.</summary>
    public bool ShowFileGlyph => Preview is null && !IsDirectory;

    /// <summary>Whether a drag is currently hovering this row, ready to drop into it.</summary>
    /// <remarks>
    /// The second property that changes, and for the same reason the first one does: without
    /// it there is no way to see which folder a drop is aimed at. That matters more here than
    /// under a toolkit that draws a drag image — the compositor gives Avalonia none, so the
    /// cursor says what will happen and this says where.
    /// </remarks>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set
        {
            if (_isDropTarget == value)
                return;
            _isDropTarget = value;
            PropertyChanged?.Invoke(this, DropTargetChanged);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Asks for this row's icon, filling <see cref="Icon"/> in when it arrives.</summary>
    /// <remarks>
    /// Called when a container is realized. The "still wanted" check is what makes that safe
    /// under virtualization: a render that finishes after its row has been recycled for
    /// another entry must not paint the wrong icon into it.
    /// </remarks>
    public void EnsureIcon(IconService icons, int size)
    {
        if (_icon is not null)
            return;
        icons.Request(Entry, size, bitmap => Icon = bitmap, () => _icon is null);
    }

    /// <summary>Asks for this row's thumbnail, if the file is one that can have a picture.</summary>
    /// <remarks>
    /// Called alongside the icon rather than instead of it: the type icon arrives in
    /// milliseconds and the thumbnail may take a hundred, so the row shows something
    /// immediately and improves rather than sitting blank.
    /// </remarks>
    public void EnsureThumbnail(ThumbnailService thumbnails, ThumbnailSize size)
    {
        if (_thumbnail is not null)
            return;
        thumbnails.Request(Entry, size, bitmap => Thumbnail = bitmap, () => _thumbnail is null);
    }

    /// <summary>Drops the thumbnail, leaving the type icon showing.</summary>
    public void ClearThumbnail() => Thumbnail = null;

    /// <summary>
    /// Forgets the icon, so the row asks for it again.
    /// </summary>
    /// <remarks>
    /// What an icon theme change needs. <see cref="EnsureIcon"/> deliberately does nothing once
    /// a row has an icon — that is what stops virtualization re-rendering the same picture on
    /// every scroll — so without this a theme change reaches only the rows that had not been
    /// drawn yet, and the visible ones keep the old theme's icons until the directory is
    /// re-read. It looks exactly like the setting not working.
    /// </remarks>
    public void ClearIcon() => Icon = null;

    /// <summary>The size column's text. See <see cref="FileSizes.Binary"/> for the policy.</summary>
    /// <remarks>
    /// Kept as a name on this type because half a dozen call sites and a test read it here,
    /// and because a row's size text is what somebody looking for it would search for.
    /// </remarks>
    internal static string FormatSize(long bytes) => FileSizes.Binary(bytes);
}
