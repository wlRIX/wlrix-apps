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
    public string SelectedName => _selected?.DisplayName ?? string.Empty;

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

    /// <summary>Replaces every row, keeping the selection if the same package is still listed.</summary>
    public void Replace(IEnumerable<PackageRowViewModel> rows)
    {
        var previous = _selected?.Name;

        foreach (var row in Rows)
            row.PropertyChanged -= OnRowChanged;

        Rows.Clear();
        foreach (var row in rows)
        {
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }

        Selected = previous is null ? null : Rows.FirstOrDefault(row => row.Name == previous);
        this.RaisePropertyChanged(nameof(IsEmpty));
        MarksChanged?.Invoke();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PackageRowViewModel.MarkedForInstall)
            or nameof(PackageRowViewModel.MarkedForRemoval))
            MarksChanged?.Invoke();
    }

    /// <summary>The rows the user has marked, which is what a transaction is built from.</summary>
    public IReadOnlyList<PackageRowViewModel> Marked =>
        Rows.Where(row => row.IsMarked).ToList();

    /// <summary>Whether anything at all is marked, which is what Start needs to know.</summary>
    public bool HasMarks => Rows.Any(row => row.IsMarked);

    /// <summary>The names marked for installation.</summary>
    public IReadOnlyList<string> MarkedForInstall =>
        Rows.Where(row => row.MarkedForInstall).Select(row => row.Name).ToList();

    /// <summary>The names marked for removal.</summary>
    public IReadOnlyList<string> MarkedForRemoval =>
        Rows.Where(row => row.MarkedForRemoval).Select(row => row.Name).ToList();

    /// <summary>Clears every mark, without disturbing the list itself.</summary>
    public void ClearMarks()
    {
        foreach (var row in Rows)
        {
            row.MarkedForInstall = false;
            row.MarkedForRemoval = false;
        }
    }
}
