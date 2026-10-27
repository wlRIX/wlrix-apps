using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using ReactiveUI;
using Wlrix.Archiver.Models;
using Wlrix.Archiver.Services;

namespace Wlrix.Archiver.ViewModels;

/// <summary>One row of the listing.</summary>
public sealed class EntryNodeViewModel : ViewModelBase
{
    /// <summary>The size of the icon beside the name: the file manager's list-view size.</summary>
    public const int IconSize = 16;

    private readonly EntryIcons? _icons;
    private bool _isExpanded;
    private bool _iconRequested;
    private Bitmap? _icon;

    public EntryNodeViewModel(ArchiveNode node, ColumnLayout columns, int depth = 0,
        EntryIcons? icons = null)
    {
        Entry = node.Entry;
        Columns = columns;
        Depth = depth;
        _icons = icons;
        Children = new ObservableCollection<EntryNodeViewModel>(
            node.Children.Select(child => new EntryNodeViewModel(child, columns, depth + 1, icons)));

        // Only the top level starts open, and the depth check is the whole point of it.
        // Expanding every directory is what the eye wants and what the layout cannot pay for: a
        // 29,631-entry tarball has 5,304 directories, and a TreeView asked to realize five
        // thousand rows at once locks the window for seconds after the read has already
        // finished. One level in costs nothing and still shows the archive's shape.
        _isExpanded = Entry.IsDirectory && depth == 0;
    }

    public ArchiveEntry Entry { get; }

    /// <summary>The shared column widths; each row's grid binds to these.</summary>
    public ColumnLayout Columns { get; }

    public ObservableCollection<EntryNodeViewModel> Children { get; }

    /// <summary>
    /// How deep this row sits. The name cell indents by this; the other columns do not.
    /// </summary>
    /// <remarks>
    /// Carried on the row rather than read from <c>TreeViewItem.Level</c> because the indent has
    /// to be applied inside the header template, where the containing item is not reachable.
    /// That placement is the entire reason this listing lines up across depths — see
    /// <c>Themes/ArchiveTree.axaml</c>.
    /// </remarks>
    public int Depth { get; }

    /// <summary>Whether there is anything to expand. Leaf rows draw no toggle.</summary>
    public bool HasChildren => Children.Count != 0;

    public string Name => Entry.Name;

    public bool IsDirectory => Entry.IsDirectory;

    /// <summary>The icon theme's icon for this entry, or null until it arrives or if it has none.</summary>
    /// <remarks>
    /// Asked for on first read rather than when the row is built. A row is only read once the
    /// tree realizes it, and most rows of a large archive sit inside collapsed directories and
    /// never are; rendering icons for all of them up front would be wasted work.
    /// </remarks>
    public Bitmap? Icon
    {
        get
        {
            if (!_iconRequested && _icons is not null)
            {
                _iconRequested = true;
                _icons.Request(Entry, IconSize, icon =>
                {
                    _icon = icon;
                    this.RaisePropertyChanged(nameof(Icon));
                    this.RaisePropertyChanged(nameof(HasIcon));
                });
            }
            return _icon;
        }
    }

    /// <summary>
    /// Whether the theme's icon is showing. Until it does, the row draws the plain folder or
    /// file glyph, so a theme with no icon for a type still leaves the row readable.
    /// </summary>
    public bool HasIcon => _icon is not null;

    /// <summary>The in-archive path, which is what the commands act on.</summary>
    public string Path => Entry.Path;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }

    /// <summary>
    /// The size column: blank for a directory, and for anything the format did not record.
    /// </summary>
    /// <remarks>
    /// A directory has no meaningful size and printing <c>0 B</c> for one invites the reader to
    /// believe it. Same for a synthesized parent, which was never in the archive to have a size.
    /// </remarks>
    public string SizeText => Entry.IsDirectory || Entry.Size is not { } size
        ? string.Empty
        : FormatSize(size);

    public string ModeText => Entry.ModeText;

    public string OwnerText => Entry.Owner ?? string.Empty;

    public string GroupText => Entry.Group ?? string.Empty;

    /// <summary>
    /// The modification date, in the current culture's short forms.
    /// </summary>
    /// <remarks>
    /// <see cref="CultureInfo.CurrentCulture"/>, not invariant: this is a date a person reads,
    /// and on a Japanese system it should read as one. The rest of the app formats with the
    /// invariant culture because it is parsing machine output, which is the opposite case.
    /// </remarks>
    public string DateText => Entry.Modified is { } modified
        ? modified.ToString("g", CultureInfo.CurrentCulture)
        : string.Empty;

    /// <summary>Every row at or below this one, this one first.</summary>
    public IEnumerable<EntryNodeViewModel> Descend()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var node in child.Descend())
                yield return node;
        }
    }

    /// <summary>
    /// A byte count as IRIX-era tools wrote it: bytes below a kilobyte, then one decimal place.
    /// </summary>
    internal static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return string.Format(CultureInfo.CurrentCulture, "{0} B", bytes);

        string[] units = ["KiB", "MiB", "GiB", "TiB", "PiB"];
        var value = (double)bytes;
        var unit = -1;
        // Loop rather than a log: the rounding has to happen at the same place the display does,
        // or 1023.95 KiB prints as "1024.0 KiB" instead of stepping up to the next unit.
        do
        {
            value /= 1024;
            unit++;
        }
        while (Math.Round(value, 1) >= 1024 && unit < units.Length - 1);

        return string.Format(CultureInfo.CurrentCulture, "{0:0.0} {1}", value, units[unit]);
    }
}
