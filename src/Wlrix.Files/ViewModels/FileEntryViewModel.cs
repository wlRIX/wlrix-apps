using System.ComponentModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Wlrix.Files.Core;
using Wlrix.Files.Core.Icons;
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
    private static readonly PropertyChangedEventArgs RestChanged = new(nameof(IconRest));
    private static readonly PropertyChangedEventArgs HoverChanged = new(nameof(IconHover));
    private static readonly PropertyChangedEventArgs SelectedChanged = new(nameof(IconSelected));
    private static readonly PropertyChangedEventArgs SelectedInactiveChanged = new(nameof(IconSelectedInactive));
    private static readonly PropertyChangedEventArgs GhostChanged = new(nameof(IconGhost));
    private static readonly PropertyChangedEventArgs DropChanged = new(nameof(IconDrop));
    private static readonly PropertyChangedEventArgs DragSourceChanged = new(nameof(IsDragSource));
    private static readonly PropertyChangedEventArgs ThumbnailChanged = new(nameof(Thumbnail));
    private static readonly PropertyChangedEventArgs HasThumbnailChanged = new(nameof(HasThumbnail));
    private static readonly PropertyChangedEventArgs ShowsIconChanged = new(nameof(ShowsIcon));

    private Bitmap? _icon;
    private Bitmap? _rest;
    private Bitmap? _hover;
    private Bitmap? _selected;
    private Bitmap? _selectedInactive;
    private Bitmap? _ghost;
    private Bitmap? _drop;
    private bool _isDragSource;
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

    /// <summary>Whether the entry is a symlink, which the name shows in italics as IRIX did.</summary>
    public bool IsSymlink => Entry.IsSymlink;

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
            // The icon view binds the picture itself and the details view binds Preview, so
            // both are raised. Without the first, the icon view framed an empty picture until
            // the row was bound again.
            PropertyChanged?.Invoke(this, ThumbnailChanged);
            PropertyChanged?.Invoke(this, PreviewChanged);
            PropertyChanged?.Invoke(this, HasThumbnailChanged);
            PropertyChanged?.Invoke(this, ShowsIconChanged);
            PropertyChanged?.Invoke(this, DirectoryGlyphChanged);
            PropertyChanged?.Invoke(this, FileGlyphChanged);
        }
    }

    /// <summary>What the row actually shows: the file itself if we have it, else its type.</summary>
    public Bitmap? Preview => _thumbnail ?? _icon;

    /// <summary>The icon in the icon view at rest: its body stippled with the rest gray.</summary>
    /// <remarks>
    /// This and the two below are the icon view's, which shows state the way IRIX's file manager
    /// did -- by recoloring the icon's body -- rather than by highlighting the label. The template
    /// picks among the three by whether the cell is under the pointer or selected, so a hover
    /// costs no render and no property change. See <see cref="IconStatePainter"/>.
    /// </remarks>
    public Bitmap? IconRest
    {
        get => _rest;
        private set => Set(ref _rest, value, RestChanged);
    }

    /// <summary>The icon under the pointer: its body white.</summary>
    public Bitmap? IconHover
    {
        get => _hover;
        private set => Set(ref _hover, value, HoverChanged);
    }

    /// <summary>The icon selected: its body yellow.</summary>
    public Bitmap? IconSelected
    {
        get => _selected;
        private set => Set(ref _selected, value, SelectedChanged);
    }

    /// <summary>The icon selected in a window without the keyboard: its body dim yellow.</summary>
    public Bitmap? IconSelectedInactive
    {
        get => _selectedInactive;
        private set => Set(ref _selectedInactive, value, SelectedInactiveChanged);
    }

    /// <summary>The icon left behind while it is dragged away: its body dark gray.</summary>
    public Bitmap? IconGhost
    {
        get => _ghost;
        private set => Set(ref _ghost, value, GhostChanged);
    }

    /// <summary>A folder's icon while it would accept a drop: its body royal blue.</summary>
    public Bitmap? IconDrop
    {
        get => _drop;
        private set => Set(ref _drop, value, DropChanged);
    }

    /// <summary>
    /// Whether this row is being dragged, so the icon view shows its ghost in place -- as IRIX
    /// did, with the icon itself following the pointer in selected yellow.
    /// </summary>
    public bool IsDragSource
    {
        get => _isDragSource;
        set
        {
            if (_isDragSource == value)
                return;
            _isDragSource = value;
            PropertyChanged?.Invoke(this, DragSourceChanged);
        }
    }

    /// <summary>Whether a thumbnail is showing, which the icon view frames rather than recolors.</summary>
    public bool HasThumbnail => _thumbnail is not null;

    /// <summary>Whether the type icon is showing: there is no thumbnail in front of it.</summary>
    public bool ShowsIcon => _thumbnail is null;

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

    /// <summary>
    /// Asks for this row's icon in each of the icon view's three states, as well as as drawn.
    /// </summary>
    /// <remarks>
    /// The plain icon too, because it is what decides whether the drawn placeholder still
    /// shows; it costs nothing extra, since the three states are recolored from the same render.
    /// </remarks>
    public void EnsureStateIcons(IconService icons, int size)
    {
        EnsureIcon(icons, size);
        if (_rest is null)
            icons.Request(Entry, size, IconState.Normal, bitmap => IconRest = bitmap, () => _rest is null);
        if (_hover is null)
            icons.Request(Entry, size, IconState.Hover, bitmap => IconHover = bitmap, () => _hover is null);
        if (_selected is null)
            icons.Request(Entry, size, IconState.Selected, bitmap => IconSelected = bitmap, () => _selected is null);
        if (_selectedInactive is null)
            icons.Request(Entry, size, IconState.SelectedInactive, bitmap => IconSelectedInactive = bitmap,
                () => _selectedInactive is null);
        if (_ghost is null)
            icons.Request(Entry, size, IconState.Ghost, bitmap => IconGhost = bitmap, () => _ghost is null);
        // Only a folder is ever a drop target; a file's blue would be a render nobody sees.
        if (_drop is null && IsDirectory)
            icons.Request(Entry, size, IconState.DropTarget, bitmap => IconDrop = bitmap, () => _drop is null);
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
    public void ClearIcon()
    {
        Icon = null;
        IconRest = null;
        IconHover = null;
        IconSelected = null;
        IconSelectedInactive = null;
        IconGhost = null;
        IconDrop = null;
    }

    private void Set(ref Bitmap? field, Bitmap? value, PropertyChangedEventArgs changed)
    {
        if (ReferenceEquals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, changed);
    }

    /// <summary>The size column's text. See <see cref="FileSizes.Binary"/> for the policy.</summary>
    /// <remarks>
    /// Kept as a name on this type because half a dozen call sites and a test read it here,
    /// and because a row's size text is what somebody looking for it would search for.
    /// </remarks>
    internal static string FormatSize(long bytes) => FileSizes.Binary(bytes);
}
