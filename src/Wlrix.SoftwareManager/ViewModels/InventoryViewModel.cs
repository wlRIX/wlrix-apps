using System.Collections.ObjectModel;
using System.ComponentModel;
using ReactiveUI;

namespace Wlrix.SoftwareManager.ViewModels;

/// <summary>
/// The Software Inventory pane: the package list, and the read-only field above it that names
/// whichever row is selected.
/// </summary>
public sealed class InventoryViewModel : ViewModelBase
{
    /// <summary>
    /// What is marked, whether or not it is in the list right now. The rows come and go with every
    /// search, so the marks live here: search for one thing, mark it, search for another, mark that,
    /// and Start does both.
    /// </summary>
    private readonly Dictionary<string, PackageMark> _marks = new(StringComparer.Ordinal);

    private PackageRowViewModel? _selected;
    private bool _isBusy;

    /// <summary>
    /// Raised whenever a row's Install or Remove mark changes. The window listens so Start can
    /// enable itself: the marks live on the rows, and nothing else would tell it they moved.
    /// </summary>
    public event Action? MarksChanged;

    /// <summary>The rows currently shown, in the order the backend returned them.</summary>
    public ObservableCollection<PackageRowViewModel> Rows { get; } = [];

    /// <summary>The row the user last clicked, or <c>null</c>.</summary>
    public PackageRowViewModel? Selected
    {
        get => _selected;
        set
        {
            this.RaiseAndSetIfChanged(ref _selected, value);
            this.RaisePropertyChanged(nameof(SelectedName));
        }
    }

    /// <summary>What the field above the list shows. Empty when nothing is selected.</summary>
    public string SelectedName => _selected?.FullName ?? string.Empty;

    /// <summary>Whether a query is in flight, so the view can say so rather than look empty.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            this.RaiseAndSetIfChanged(ref _isBusy, value);
            this.RaisePropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>Whether to show the "nothing here" line: no rows, and not still looking.</summary>
    public bool IsEmpty => Rows.Count == 0 && !_isBusy;

    /// <summary>
    /// Replaces every row, keeping the selection if the same package is still listed, and putting
    /// each row's mark back if it has one.
    /// </summary>
    public void Replace(IEnumerable<PackageRowViewModel> rows)
    {
        var previous = _selected?.Name;

        foreach (var row in Rows)
            row.PropertyChanged -= OnRowChanged;

        Rows.Clear();
        foreach (var row in rows)
        {
            if (!row.IsLocked)
            {
                Restore(row);
                row.PropertyChanged += OnRowChanged;
            }

            Rows.Add(row);
        }

        Selected = previous is null ? null : Rows.FirstOrDefault(row => row.Name == previous);
        this.RaisePropertyChanged(nameof(IsEmpty));
        MarksChanged?.Invoke();
    }

    /// <summary>
    /// Shows a stored mark on a newly listed row. A row that cannot take it, such as an installation
    /// mark on a row in the installed list, shows nothing and the mark stays as it was.
    /// </summary>
    private void Restore(PackageRowViewModel row)
    {
        if (!_marks.TryGetValue(row.Name, out var mark))
            return;

        if (mark.IsRemoval && row.CanRemove)
            row.MarkedForRemoval = true;
        else if (!mark.IsRemoval && row.CanInstall)
            row.MarkedForInstall = true;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PackageRowViewModel.MarkedForInstall)
            or nameof(PackageRowViewModel.MarkedForRemoval)) || sender is not PackageRowViewModel row)
            return;

        if (row.IsMarked)
            _marks[row.Name] = new PackageMark(row.MarkedForRemoval, row.IsFile, row.SizeKilobytes);
        else
            _marks.Remove(row.Name);

        MarksChanged?.Invoke();
    }

    /// <summary>Whether anything at all is marked, which is what Start needs to know.</summary>
    public bool HasMarks => _marks.Count > 0;

    /// <summary>How many packages are marked, listed or not.</summary>
    public int MarkCount => _marks.Count;

    /// <summary>The names marked for installation from a repository.</summary>
    public IReadOnlyList<string> MarkedForInstall => Names(mark => mark is { IsRemoval: false, IsFile: false });

    /// <summary>The paths of package files marked for installation.</summary>
    public IReadOnlyList<string> MarkedFiles => Names(mark => mark is { IsRemoval: false, IsFile: true });

    /// <summary>The names marked for removal.</summary>
    public IReadOnlyList<string> MarkedForRemoval => Names(mark => mark.IsRemoval);

    /// <summary>The size of everything marked for installation, in kilobytes.</summary>
    public long MarkedInstallKilobytes => _marks.Values.Where(mark => !mark.IsRemoval).Sum(mark => mark.SizeKilobytes);

    /// <summary>The size of everything marked for removal, in kilobytes.</summary>
    public long MarkedRemovalKilobytes => _marks.Values.Where(mark => mark.IsRemoval).Sum(mark => mark.SizeKilobytes);

    /// <summary>Clears every mark, without disturbing the list itself.</summary>
    public void ClearMarks()
    {
        foreach (var row in Rows.Where(row => !row.IsLocked))
        {
            row.MarkedForInstall = false;
            row.MarkedForRemoval = false;
        }

        _marks.Clear();
        MarksChanged?.Invoke();
    }

    private List<string> Names(Func<PackageMark, bool> predicate) =>
        _marks.Where(pair => predicate(pair.Value)).Select(pair => pair.Key).ToList();

    private sealed record PackageMark(bool IsRemoval, bool IsFile, long SizeKilobytes);
}
