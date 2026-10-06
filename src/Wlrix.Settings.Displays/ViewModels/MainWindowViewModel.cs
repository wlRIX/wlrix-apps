using System.Collections.ObjectModel;
using Avalonia.Threading;
using ReactiveUI;
using Wlrix.Settings.Displays.Layout;
using Wlrix.Settings.Displays.Localization;
using Wlrix.Settings.Displays.Models;
using Wlrix.Settings.Displays.Services;

namespace Wlrix.Settings.Displays.ViewModels;

/// <summary>
/// The Displays panel: every display, the one selected, and the staged layout.
///
/// Edits are staged rather than applied as they are made, as in KDE's panel and unlike the
/// other wlRIX panels, because a wrong mode can leave nothing visible to undo it with. Apply
/// sends the whole layout as one configuration and then asks whether to keep it; not answering
/// puts the old one back.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// How close, in logical pixels, a dropped display's edge has to come to a neighbor's to be
    /// pulled into line with it.
    /// </summary>
    internal const int AlignDistance = 64;

    private readonly IOutputFeed _feed;
    private readonly Action<Action> _post;
    private OutputSnapshot? _snapshot;
    private DisplayViewModel? _selected;
    private bool _isReady;
    private bool _isBusy;
    private string? _status;
    private int _layoutVersion;

    public MainWindowViewModel() : this(new SampleOutputFeed()) { }

    /// <param name="post">
    /// How a feed event reaches the UI thread. Defaults to the dispatcher; the tests run it in
    /// place.
    /// </param>
    public MainWindowViewModel(IOutputFeed feed, Action<Action>? post = null)
    {
        _feed = feed;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
    }

    public ObservableCollection<DisplayViewModel> Displays { get; } = [];

    public DisplayViewModel? SelectedDisplay
    {
        get => _selected;
        set
        {
            // The list never shows nothing selected while there is something to select.
            if (value is null && Displays.Count > 0)
                return;
            this.RaiseAndSetIfChanged(ref _selected, value);
        }
    }

    /// <summary>Whether there is more than one display, and so an arrangement to show.</summary>
    public bool HasMultiple => Displays.Count > 1;

    /// <summary>Whether the compositor has described the displays yet.</summary>
    public bool IsReady
    {
        get => _isReady;
        private set => this.RaiseAndSetIfChanged(ref _isReady, value);
    }

    /// <summary>While a configuration is on its way, or waiting to be kept.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isBusy, value);
            RaiseDirty();
        }
    }

    public bool IsDirty => Displays.Any(d => d.IsDirty);

    public bool CanApply => IsDirty && !_isBusy;
    public bool CanReset => IsDirty && !_isBusy;

    /// <summary>What became of the last thing tried, when it is worth saying.</summary>
    public string? Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    /// <summary>Bumped whenever a display moves or changes size, for the arrangement to redraw.</summary>
    public int LayoutVersion
    {
        get => _layoutVersion;
        private set => this.RaiseAndSetIfChanged(ref _layoutVersion, value);
    }

    /// <summary>
    /// Asked after a configuration has been applied: true to keep it. The window supplies the
    /// countdown dialog; anything other than a yes, including no answer, reverts.
    /// </summary>
    public Func<Task<bool>> ConfirmKeep { get; set; } = () => Task.FromResult(true);

    public void Start()
    {
        _feed.SnapshotReceived += snapshot => _post(() => OnSnapshot(snapshot));
        _feed.Unavailable += () => _post(() => Status = Strings.NoCompositor);
        _feed.Start();
    }

    /// <summary>
    /// A display was dragged to <paramref name="x"/>, <paramref name="y"/>: snap it against the
    /// others and mend the rest of the layout around it.
    /// </summary>
    public void MoveDisplay(DisplayViewModel display, int x, int y)
    {
        if (!display.Enabled)
            return;
        var others = Displays.Where(d => d.Enabled && d != display).Select(d => d.Box).ToList();
        var snapped = ArrangementMath.Snap(display.Box.At(x, y), others, AlignDistance);
        display.MoveTo(snapped.X, snapped.Y);
        Settle(display);
        AfterEdit();
    }

    public async Task ApplyAsync()
    {
        if (!CanApply || _snapshot is not { } snapshot)
            return;

        IsBusy = true;
        Status = null;
        try
        {
            var before = snapshot.Heads.Select(OutputChange.From).ToList();
            var result = await _feed.ApplyAsync(Displays.Select(d => d.ToChange()).ToList());
            if (result != ApplyResult.Succeeded)
            {
                Status = Message(result);
                return;
            }

            if (await ConfirmKeep())
                return;

            var reverted = await _feed.ApplyAsync(before);
            Status = reverted == ApplyResult.Succeeded ? Strings.Reverted : Strings.RevertFailed;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Put every display back to what the compositor last reported.</summary>
    public void Reset()
    {
        if (_snapshot is not { } snapshot || _isBusy)
            return;
        foreach (var display in Displays)
        {
            if (snapshot.Heads.FirstOrDefault(h => h.Name == display.Name) is { } head)
                display.Update(head, keepEdits: false);
        }
        Status = null;
        AfterEdit();
    }

    public void Dispose() => _feed.Dispose();

    // ── Snapshots ───────────────────────────────────────────────────────────────────────

    private void OnSnapshot(OutputSnapshot snapshot)
    {
        var hadEdits = _snapshot is not null && IsDirty;
        _snapshot = snapshot;

        var sameDisplays = Displays.Count == snapshot.Heads.Count
                           && snapshot.Heads.All(h => Displays.Any(d => d.Name == h.Name));
        if (sameDisplays)
        {
            // Keep what is staged, unless a configuration is in flight: then this is the
            // compositor saying what it made of it, and that is what to show.
            var keep = hadEdits && !_isBusy;
            foreach (var display in Displays)
                display.Update(snapshot.Heads.First(h => h.Name == display.Name), keep);
        }
        else
        {
            Rebuild(snapshot);
            if (hadEdits)
                Status = Strings.DisplaysChanged;
        }

        if (Status == Strings.NoCompositor)
            Status = null;
        IsReady = true;
        AfterEdit();
    }

    private void Rebuild(OutputSnapshot snapshot)
    {
        var selected = _selected?.Name;
        foreach (var display in Displays)
            display.Edited -= OnEdited;
        Displays.Clear();

        foreach (var head in snapshot.Heads)
        {
            var display = new DisplayViewModel(head);
            display.Edited += OnEdited;
            Displays.Add(display);
        }

        _selected = null;
        SelectedDisplay = Displays.FirstOrDefault(d => d.Name == selected)
                          ?? Displays.FirstOrDefault(d => d.Enabled)
                          ?? Displays.FirstOrDefault();
        this.RaisePropertyChanged(nameof(HasMultiple));
    }

    // ── Edits ───────────────────────────────────────────────────────────────────────────

    private void OnEdited(DisplayViewModel display, EditKind kind)
    {
        switch (kind)
        {
            // Something else keeps its place and this display is fitted in beside the rest,
            // or the rest close up around the gap it left.
            case EditKind.Toggled:
                if (Displays.FirstOrDefault(d => d.Enabled && d != display) is { } anchor)
                    Settle(anchor);
                else if (display.Enabled)
                    Settle(display);
                break;
            // It keeps its place and the others make room, or close up.
            case EditKind.Resized:
                Settle(display);
                break;
        }
        AfterEdit();
    }

    private void Settle(DisplayViewModel anchor)
    {
        var enabled = Displays.Where(d => d.Enabled).ToList();
        var index = enabled.IndexOf(anchor);
        if (index < 0)
            return;
        var boxes = ArrangementMath.Settle(enabled.Select(d => d.Box).ToList(), index, AlignDistance);
        for (var i = 0; i < enabled.Count; i++)
            enabled[i].MoveTo(boxes[i].X, boxes[i].Y);
        LayoutVersion++;
    }

    private void AfterEdit()
    {
        var enabled = Displays.Count(d => d.Enabled);
        foreach (var display in Displays)
            display.CanDisable = !(display.Enabled && enabled <= 1);
        LayoutVersion++;
        RaiseDirty();
    }

    private void RaiseDirty()
    {
        this.RaisePropertyChanged(nameof(IsDirty));
        this.RaisePropertyChanged(nameof(CanApply));
        this.RaisePropertyChanged(nameof(CanReset));
    }

    private static string Message(ApplyResult result) => result switch
    {
        ApplyResult.Cancelled => Strings.ApplyCancelled,
        ApplyResult.Unavailable => Strings.NoCompositor,
        _ => Strings.ApplyFailed,
    };
}
