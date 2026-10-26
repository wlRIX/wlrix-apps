using System.Reactive;
using ReactiveUI;
using Wlrix.Files.Core;
using Wlrix.Files.Core.Operations;
using Wlrix.Files.Localization;

namespace Wlrix.Files.ViewModels;

/// <summary>One running operation, as a panel in the progress window shows it.</summary>
/// <remarks>
/// Laid out after the job panel Plasma shows for Dolphin: what is happening and to where, the
/// time left, a graph of the speed, and the details folded away below. Two clocks drive it.
/// Progress reports update the amounts the moment they arrive; <see cref="Tick"/>, once a
/// second from the monitor, updates everything that is about time -- the speed, the average,
/// the time left and the graph -- so those keep moving, toward zero, when the bytes stop.
/// </remarks>
public sealed class OperationViewModel : ReactiveObject
{
    /// <summary>How many seconds of speed the graph shows.</summary>
    public const int HistoryLength = 60;

    private readonly TransferRate _rate = new(TimeSpan.FromSeconds(3));
    private readonly Queue<double> _history = new();
    private TimeSpan _active;
    private TimeSpan? _lastTick;

    private string _status = string.Empty;
    private double? _fraction;
    private string _speedText = string.Empty;
    private string _averageText = string.Empty;
    private string _sourceText = string.Empty;
    private string _amountText = string.Empty;
    private string _filesText = string.Empty;
    private bool _showDetails;
    private IReadOnlyList<double> _speedHistory = [];

    public OperationViewModel(QueuedOperation queued)
    {
        Queued = queued;
        var operation = queued.Operation;
        Title = Strings.ProgressTitle(operation.Kind);
        Destination = operation.Target;
        DestinationText = Describe(operation.Target);
        var what = operation.Sources.Count == 1 ? operation.Sources[0].Name : Strings.ProgressItems(operation.Sources.Count);
        Summary = operation.Target is { } target ? Strings.ProgressTo(what, target.Name) : what;
        _sourceText = operation.Sources.Count == 1 ? Describe(operation.Sources[0]) : string.Empty;

        TogglePause = ReactiveCommand.Create(() =>
        {
            if (Queued.IsPaused)
                Queued.Resume();
            else
                Queued.Pause();
        });
        Cancel = ReactiveCommand.Create(() =>
        {
            // A paused operation is waiting on the gate, which the cancellation also opens.
            Queued.Cancel();
        });
        ToggleDetails = ReactiveCommand.Create(() => { ShowDetails = !ShowDetails; });
        OpenDestination = ReactiveCommand.Create(() =>
        {
            if (Destination is { } destination)
                OpenRequested?.Invoke(destination.Parent is { } parent && operation.Kind == OperationKind.Compress ? parent : destination);
        });
        Refresh();
    }

    public QueuedOperation Queued { get; }

    /// <summary>"Copying", "Moving": the verb, as the panel's heading.</summary>
    public string Title { get; }

    /// <summary>What and where to: "ubuntu.iso to Downloads".</summary>
    public string Summary { get; }

    /// <summary>Where it is going, for the details' link; null for a delete.</summary>
    public Location? Destination { get; }

    public string DestinationText { get; }

    public bool HasDestination => Destination is not null;

    /// <summary>The top right: time left, or why there is none -- waiting, paused, examining.</summary>
    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public double? Fraction
    {
        get => _fraction;
        private set => this.RaiseAndSetIfChanged(ref _fraction, value);
    }

    public string SpeedText
    {
        get => _speedText;
        private set => this.RaiseAndSetIfChanged(ref _speedText, value);
    }

    public string AverageText
    {
        get => _averageText;
        private set => this.RaiseAndSetIfChanged(ref _averageText, value);
    }

    public string SourceText
    {
        get => _sourceText;
        private set => this.RaiseAndSetIfChanged(ref _sourceText, value);
    }

    public string AmountText
    {
        get => _amountText;
        private set => this.RaiseAndSetIfChanged(ref _amountText, value);
    }

    public string FilesText
    {
        get => _filesText;
        private set => this.RaiseAndSetIfChanged(ref _filesText, value);
    }

    public bool ShowDetails
    {
        get => _showDetails;
        set => this.RaiseAndSetIfChanged(ref _showDetails, value);
    }

    public bool IsPaused => Queued.IsPaused;

    public string PauseText => IsPaused ? Strings.ProgressResume : Strings.ProgressPause;

    /// <summary>Bytes a second, oldest first, one a second. A new list each time, for binding.</summary>
    public IReadOnlyList<double> SpeedHistory
    {
        get => _speedHistory;
        private set => this.RaiseAndSetIfChanged(ref _speedHistory, value);
    }

    /// <summary>Whether the operation moves bytes, and so has a speed and a graph to show.</summary>
    public bool ShowsSpeed => Queued.Operation.Kind is OperationKind.Copy or OperationKind.Move or OperationKind.Compress;

    public ReactiveCommand<Unit, Unit> TogglePause { get; }
    public ReactiveCommand<Unit, Unit> Cancel { get; }
    public ReactiveCommand<Unit, Unit> ToggleDetails { get; }

    /// <summary>Opens where things are going, as the destination link in Plasma's panel does.</summary>
    /// <remarks>A compress's target is the archive file, so its link opens the directory it is in.</remarks>
    public ReactiveCommand<Unit, Unit> OpenDestination { get; }

    /// <summary>Asks for a directory to be shown, for whoever owns the browser windows.</summary>
    public event Action<Location>? OpenRequested;

    /// <summary>Takes in the latest progress report. Cheap; called for every one.</summary>
    public void Refresh()
    {
        var progress = Queued.Progress;
        Fraction = progress.Phase is OperationPhase.Working or OperationPhase.Completed ? progress.Fraction : null;
        if (progress.BytesTotal > 0)
            AmountText = Strings.ProgressAmount(FileSizes.Binary(progress.BytesDone), FileSizes.Binary(progress.BytesTotal));
        if (progress.FilesTotal > 0)
        {
            // The file being copied counts as the one we are on, as Dolphin's "1 of 1 file" does
            // while that one file is still going.
            var on = Math.Min(progress.FilesDone + 1, progress.FilesTotal);
            FilesText = Strings.ProgressFiles(on, progress.FilesTotal);
        }
        if (progress.CurrentSource is { } source)
            SourceText = Describe(source);

        this.RaisePropertyChanged(nameof(IsPaused));
        this.RaisePropertyChanged(nameof(PauseText));
        Status = StatusFor(progress, _rate.BytesPerSecond);
    }

    /// <summary>Samples the speed, the average and the time left, once a second.</summary>
    /// <param name="now">Any steadily increasing clock; only the differences matter.</param>
    public void Tick(TimeSpan now)
    {
        var progress = Queued.Progress;
        var moving = progress.Phase == OperationPhase.Working && !IsPaused;

        // The average is over time actually spent moving bytes. Time paused, or queued behind
        // another operation on the same disk, says nothing about how fast this one goes.
        if (_lastTick is { } last && moving)
            _active += now - last;
        _lastTick = now;

        if (moving)
            _rate.Add(now, progress.BytesDone);
        else
            _rate.Reset();
        var perSecond = moving ? _rate.BytesPerSecond : 0;

        _history.Enqueue(Math.Max(0, perSecond ?? 0));
        while (_history.Count > HistoryLength)
            _history.Dequeue();
        SpeedHistory = [.. _history];

        SpeedText = Strings.ProgressSpeed(perSecond is { } rate ? Strings.ProgressRate(FileSizes.Binary((long)rate)) : "—");
        AverageText = _active.TotalSeconds >= 1
            ? Strings.ProgressAverage(Strings.ProgressRate(FileSizes.Binary((long)(progress.BytesDone / _active.TotalSeconds))))
            : Strings.ProgressAverage("—");

        Refresh();
    }

    /// <summary>The heading's right-hand text.</summary>
    private string StatusFor(OperationProgress progress, double? perSecond)
    {
        if (IsPaused)
            return Strings.ProgressPaused;
        return progress.Phase switch
        {
            OperationPhase.Waiting => Strings.ProgressWaiting,
            OperationPhase.Scanning => Strings.OperationScanning(progress.ItemsDone),
            OperationPhase.Working when progress.BytesTotal > 0 && perSecond is > 0 =>
                Strings.ProgressRemaining(Remaining((progress.BytesTotal - progress.BytesDone) / perSecond.Value)),
            _ => string.Empty
        };
    }

    /// <summary>Time left, in the coarsest unit that still says something.</summary>
    /// <remarks>
    /// Rounded up, so the last moments of a copy say "1 s" rather than "0 s", and whole
    /// minutes past the first, because a figure that changes every second for an hour is
    /// noise rather than information.
    /// </remarks>
    internal static string Remaining(double seconds)
    {
        var whole = (long)Math.Ceiling(Math.Max(0, seconds));
        if (whole < 60)
            return Strings.ProgressSeconds(Math.Max(1, whole));
        var minutes = (whole + 59) / 60;
        if (minutes < 60)
            return Strings.ProgressMinutes(minutes);
        return Strings.ProgressHours(minutes / 60, minutes % 60);
    }

    /// <summary>A location as somebody would type it: a path when local, the address when not.</summary>
    private static string Describe(Location? location) =>
        location is null ? string.Empty : location.IsLocal ? location.Path : location.ToUriString();
}
