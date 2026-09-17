namespace Wlrix.Files.Core.State;

/// <summary>How a listing is drawn.</summary>
public enum ViewMode
{
    /// <summary>An IRIX grid of large icons with the name beneath.</summary>
    Icons,
    /// <summary>A sortable table of name, size, kind and date.</summary>
    Details
}

/// <summary>What a listing is ordered by.</summary>
public enum SortKey
{
    Name,
    Size,
    Kind,
    Modified
}

/// <summary>
/// How one directory was left looking.
/// </summary>
/// <remarks>
/// In Core rather than beside the view models, because it is the *persisted* vocabulary: the
/// state store writes it, and the future picker will read the same file so a directory looks
/// the same in the picker as it does in the window.
/// </remarks>
public sealed record DirectoryViewState
{
    public ViewMode ViewMode { get; init; } = ViewMode.Icons;
    public SortKey SortKey { get; init; } = SortKey.Name;
    public bool SortDescending { get; init; }
    public bool FoldersFirst { get; init; } = true;
    public bool ShowHidden { get; init; }
    public bool Thumbnails { get; init; }

    /// <summary>Details-view column widths, in order. Empty means the defaults.</summary>
    public IReadOnlyList<double> ColumnWidths { get; init; } = [];
}
