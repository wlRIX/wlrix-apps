using System.ComponentModel;
using Avalonia.Collections;
using ReactiveUI;
using Wlrix.Files.Core;

namespace Wlrix.Files.ViewModels;

/// <summary>
/// One tab: one or two panes, a label, and which of them the window acts on.
/// </summary>
/// <remarks>
/// The pane already existed as its own object precisely so that this could be a collection
/// rather than a rewrite, and it was twice over: a tab is a pane with a title on it, and a
/// split tab is two of them side by side.
///
/// <para>
/// Two is the limit, and deliberately. A split exists so that a copy has a visible source and
/// a visible destination; a third pane adds no answer to that question and turns "which pane
/// does Paste use" from a glance into a hunt.
/// </para>
/// </remarks>
public sealed class TabViewModel : ReactiveObject, IDisposable
{
    private readonly PropertyChangedEventHandler _paneChanged;
    private PaneViewModel _activePane;

    public TabViewModel(MainWindowViewModel window, PaneViewModel pane)
    {
        Window = window;
        _activePane = pane;
        pane.Tab = this;
        Panes.Add(pane);

        // The label follows the active pane, so a tab renames itself as it is navigated rather
        // than keeping the name of the directory it was opened at.
        _paneChanged = (sender, e) =>
        {
            if (e.PropertyName != nameof(PaneViewModel.Location) || !ReferenceEquals(sender, ActivePane))
                return;
            this.RaisePropertyChanged(nameof(Title));
            Moved?.Invoke();
        };
        pane.PropertyChanged += _paneChanged;
    }

    /// <summary>Raised when this tab goes somewhere else, so the session can be rewritten.</summary>
    public event Action? Moved;

    /// <summary>The window this tab belongs to. The listing reaches its services through it.</summary>
    public MainWindowViewModel Window { get; }

    /// <summary>The panes, left to right. One, or two when split.</summary>
    public AvaloniaList<PaneViewModel> Panes { get; } = [];

    /// <summary>The pane the window's commands act on.</summary>
    public PaneViewModel ActivePane
    {
        get => _activePane;
        private set
        {
            if (ReferenceEquals(_activePane, value))
                return;

            var previous = _activePane;
            this.RaiseAndSetIfChanged(ref _activePane, value);
            previous.RaiseActiveChanged();
            value.RaiseActiveChanged();
            this.RaisePropertyChanged(nameof(Pane));
            this.RaisePropertyChanged(nameof(Title));
            ActivePaneChanged?.Invoke();
        }
    }

    /// <summary>Raised when the window should look at the other pane now.</summary>
    public event Action? ActivePaneChanged;

    /// <summary>The active pane, under the name the rest of the application knows it by.</summary>
    public PaneViewModel Pane => ActivePane;

    /// <summary>Whether this tab is showing two panes.</summary>
    public bool IsSplit => Panes.Count > 1;

    /// <summary>Makes <paramref name="pane"/> the one the window acts on.</summary>
    /// <remarks>
    /// Called when a listing is clicked or takes focus. Silently ignores a pane belonging to
    /// another tab, which is the shape of bug that would otherwise leave the window acting on
    /// a directory nobody is looking at.
    /// </remarks>
    public void SetActivePane(PaneViewModel pane)
    {
        if (Panes.Contains(pane))
            ActivePane = pane;
    }

    /// <summary>
    /// Adds a second pane, starting where this one is.
    /// </summary>
    /// <remarks>
    /// Starting at the same directory rather than at home: a split is nearly always the first
    /// half of "copy this to somewhere else", and beginning at the same place means one
    /// navigation instead of two. The new pane becomes the active one, because that is the one
    /// about to be pointed somewhere.
    /// </remarks>
    /// <param name="at">Where the new pane starts. Defaults to where this one is.</param>
    public PaneViewModel? Split(Func<Location, PaneViewModel> create, Location? at = null)
    {
        if (IsSplit)
            return null;

        var pane = create(at ?? ActivePane.Location);
        pane.Tab = this;
        pane.PropertyChanged += _paneChanged;
        Panes.Add(pane);
        this.RaisePropertyChanged(nameof(IsSplit));
        ActivePane = pane;
        return pane;
    }

    /// <summary>
    /// Drops the pane that is not active, leaving the one being looked at.
    /// </summary>
    /// <remarks>
    /// The inactive one goes, not the second one. Closing the split while working in the right
    /// pane and being returned to the left is the kind of surprise that loses somebody their
    /// place.
    /// </remarks>
    public PaneViewModel? Unsplit()
    {
        if (!IsSplit)
            return null;

        var going = Panes.First(pane => !ReferenceEquals(pane, ActivePane));
        going.PropertyChanged -= _paneChanged;
        going.Tab = null;
        Panes.Remove(going);
        this.RaisePropertyChanged(nameof(IsSplit));
        ActivePane.RaiseActiveChanged();
        return going;
    }

    /// <summary>What the tab strip shows.</summary>
    /// <remarks>
    /// The directory's own name, except at a filesystem root, where the name is empty and the
    /// separator is the only thing that says where you are.
    /// </remarks>
    public string Title => ActivePane.Location.Name is { Length: > 0 } name ? name : "/";

    /// <summary>
    /// What was selected here, kept across a switch away and back.
    /// </summary>
    /// <remarks>
    /// The active pane's, which is what the window means by "the selection". Each pane keeps
    /// its own, so a split tab does not lose the other side's on a switch.
    /// </remarks>
    public IReadOnlyList<FileEntryViewModel> Selection
    {
        get => ActivePane.Selection;
        set => ActivePane.Selection = value;
    }

    public void Dispose()
    {
        foreach (var pane in Panes)
        {
            pane.PropertyChanged -= _paneChanged;
            pane.Tab = null;
        }
    }
}
