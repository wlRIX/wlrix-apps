using Avalonia.Controls;
using ReactiveUI;

namespace Wlrix.Archiver.ViewModels;

/// <summary>The width of each column, shared by the heading and every row.</summary>
/// <remarks>
/// The listing is a <c>TreeView</c> wearing a table's clothes: the heading is a <c>Grid</c>
/// docked above the tree, and each row's header template is another <c>Grid</c> with the same
/// columns. Two grids with the same shape have to agree on widths, and
/// <c>Grid.IsSharedSizeScope</c> cannot do it here — a shared size group sizes to the widest
/// content, which is the opposite of a user-resizable column, and it has no way to be driven by
/// a splitter.
///
/// So the widths live here instead. The heading's <c>ColumnDefinition.Width</c> binds two-way,
/// so a <c>GridSplitter</c> drag writes back; every row binds one-way and follows. The same
/// duplication <c>Wlrix.SoftwareManager</c>'s inventory pane complains about in a comment,
/// except shared through a model rather than copied and kept in step by hand.
///
/// <see cref="GridLength"/> rather than <c>double</c> because Name is star-sized: it takes
/// whatever the fixed columns leave, so the table fills the window at any size.
/// </remarks>
public sealed class ColumnLayout : ReactiveObject
{
    private GridLength _name = new(1, GridUnitType.Star);
    private GridLength _size = new(110);
    private GridLength _mode = new(100);
    private GridLength _owner = new(80);
    private GridLength _group = new(80);
    private GridLength _date = new(150);

    /// <summary>How wide the splitter columns between headings are.</summary>
    /// <remarks>
    /// Part of the layout rather than a constant in the XAML because the row grids have to
    /// reserve exactly the same gap, or every column after the first drifts out of line with
    /// its heading.
    /// </remarks>
    public GridLength Splitter { get; } = new(6);

    /// <summary>
    /// Reserves the width the tree's vertical scrollbar takes, so the last heading stays over
    /// the last column instead of hanging past it.
    /// </summary>
    public GridLength Gutter { get; } = new(18);

    public GridLength Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    public GridLength Size
    {
        get => _size;
        set => this.RaiseAndSetIfChanged(ref _size, value);
    }

    public GridLength Mode
    {
        get => _mode;
        set => this.RaiseAndSetIfChanged(ref _mode, value);
    }

    public GridLength Owner
    {
        get => _owner;
        set => this.RaiseAndSetIfChanged(ref _owner, value);
    }

    public GridLength Group
    {
        get => _group;
        set => this.RaiseAndSetIfChanged(ref _group, value);
    }

    public GridLength Date
    {
        get => _date;
        set => this.RaiseAndSetIfChanged(ref _date, value);
    }
}
