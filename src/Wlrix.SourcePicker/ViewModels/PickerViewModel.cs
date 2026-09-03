using Avalonia.Threading;
using ReactiveUI;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Wlrix.SourcePicker.Localization;
using Wlrix.SourcePicker.Models;

namespace Wlrix.SourcePicker.ViewModels;

/// <summary>The dialog: what may be shared, what is chosen, and how the question ends.</summary>
public sealed class PickerViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// How often the thumbnails are pulled.
    /// </summary>
    /// <remarks>
    /// Matched to the portal's own capture tick. Polling faster would only re-read a sequence
    /// number that has not moved; slower would make the grid visibly stutter.
    /// </remarks>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);

    private readonly DispatcherTimer _timer;
    private bool _updatingSelection;

    public PickerViewModel(Manifest manifest)
    {
        Manifest = manifest;

        var sources = manifest.Sources.Select(source => new SourceViewModel(source)).ToList();
        Monitors = new ObservableCollection<SourceViewModel>(sources.Where(s => s.IsMonitor));
        Windows = new ObservableCollection<SourceViewModel>(sources.Where(s => !s.IsMonitor));
        All = sources;

        foreach (var source in All)
        {
            source.WhenAnyValue(s => s.IsSelected)
                .Skip(1)
                .Subscribe(selected => OnSelectionChanged(source, selected));
        }

        Share = ReactiveCommand.Create(
            () => Chosen,
            this.WhenAnyValue(vm => vm.SelectionCount).Select(count => count > 0));

        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        // Once immediately, so the first frames are not a tick late.
        Refresh();
    }

    public Manifest Manifest { get; }

    public IReadOnlyList<SourceViewModel> All { get; }
    public ObservableCollection<SourceViewModel> Monitors { get; }
    public ObservableCollection<SourceViewModel> Windows { get; }

    public bool HasMonitors => Monitors.Count > 0;
    public bool HasWindows => Windows.Count > 0;

    /// <summary>
    /// The instruction across the top, naming the application when it gave one.
    /// </summary>
    /// <remarks>
    /// An application id is not a name — <c>org.mozilla.firefox</c> is what arrives — but it
    /// is what there is, and telling the user *something* asked is worth more than a sentence
    /// that names nobody.
    /// </remarks>
    public string Prompt => Strings.Prompt(Manifest.AppId);

    public string Hint => Strings.Hint(Manifest.Multiple);

    /// <summary>Shown only when the pointer really will be in the stream.</summary>
    public bool ShowsCursor => Manifest.Cursor;

    public int SelectionCount => All.Count(source => source.IsSelected);

    /// <summary>What to answer with, in the order the user chose.</summary>
    public IReadOnlyList<string> Chosen =>
        _order.Where(id => All.Any(s => s.Id == id && s.IsSelected)).ToList();

    private readonly List<string> _order = [];

    public ReactiveCommand<Unit, IReadOnlyList<string>> Share { get; }

    /// <summary>
    /// Keep the selection within what the portal allows.
    /// </summary>
    /// <remarks>
    /// This is where "the lamp behaves like a radio button" actually happens. When the request
    /// is for a single source, choosing one unlights the rest; when several are allowed, the
    /// lamps are independent. The rule comes from the manifest rather than from the control
    /// tree, which is why the tiles are toggle buttons and not grouped radio buttons.
    /// <para>
    /// The portal checks this again on its side. It has to: the picker is a separate process
    /// and its answer is untrusted input, so this is a courtesy to the user, not a guarantee.
    /// </para>
    /// </remarks>
    private void OnSelectionChanged(SourceViewModel changed, bool selected)
    {
        // Guard against the recursion of clearing other tiles, each of which reports back.
        if (_updatingSelection)
        {
            return;
        }

        if (selected)
        {
            _order.Remove(changed.Id);
            _order.Add(changed.Id);

            if (!Manifest.Multiple)
            {
                _updatingSelection = true;
                foreach (var other in All.Where(s => !ReferenceEquals(s, changed)))
                {
                    other.IsSelected = false;
                }
                _updatingSelection = false;
                _order.RemoveAll(id => id != changed.Id);
            }
        }
        else
        {
            _order.Remove(changed.Id);
        }

        this.RaisePropertyChanged(nameof(SelectionCount));
    }

    private void Refresh()
    {
        foreach (var source in All)
        {
            source.Refresh();
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        foreach (var source in All)
        {
            source.Dispose();
        }
    }
}
