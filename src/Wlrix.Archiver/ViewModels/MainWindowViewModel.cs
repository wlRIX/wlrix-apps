using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReactiveUI;
using Wlrix.Archiver.Localization;
using Wlrix.Archiver.Models;
using Wlrix.Archiver.Services;
using Wlrix.Archiver.Services.Archives;
using Wlrix.Archiver.Services.Encodings;
using ZLogger;

namespace Wlrix.Archiver.ViewModels;

/// <summary>The window: the open archive, the listing, and the menu commands.</summary>
public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IArchiveBackendRegistry _registry;
    private readonly FilenameDecoder _decoder;
    private readonly DragStaging _staging;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly List<IDisposable> _subscriptions = [];

    private OpenArchive? _archive;
    private bool _isBusy;
    private double _progress;
    private bool _hasProgress;
    private string _status = Strings.EmptyNoArchive;

    /// <summary>Cancels the operation currently running, if any.</summary>
    private CancellationTokenSource? _running;

    /// <summary>The design-time constructor. Only the XAML previewer calls this.</summary>
    public MainWindowViewModel()
        : this(new ArchiveBackendRegistry([new SharpCompressBackend(new FilenameDecoder())]),
            new FilenameDecoder(),
            new DragStaging(new ArchiveBackendRegistry(
                [new SharpCompressBackend(new FilenameDecoder())])),
            NullLogger<MainWindowViewModel>.Instance)
    {
    }

    public MainWindowViewModel(IArchiveBackendRegistry registry, FilenameDecoder decoder,
        DragStaging staging, ILogger<MainWindowViewModel> logger)
    {
        _registry = registry;
        _decoder = decoder;
        _staging = staging;
        _logger = logger;

        Encodings = new ObservableCollection<EncodingChoiceViewModel>(
            EncodingCatalog.All.Select(entry => new EncodingChoiceViewModel(entry, this)));

        var hasArchive = this.WhenAnyValue(model => model.Archive)
            .Select(archive => archive is not null);
        var canRemove = this.WhenAnyValue(model => model.Archive, model => model.HasSelection,
            (archive, selected) => selected
                && archive is not null
                && archive.Capabilities.HasFlag(ArchiveCapabilities.Remove));

        Open = ReactiveCommand.CreateFromTask(() => OpenRequestedAsync());
        Extract = ReactiveCommand.CreateFromTask(() => ExtractRequestedAsync(), hasArchive);
        Remove = ReactiveCommand.CreateFromTask(() => RemoveRequestedAsync(), canRemove);
        // Wired but disabled: the new-archive dialog is the next piece of work, and a menu item
        // that opens nothing is worse than one that is visibly not ready yet.
        New = ReactiveCommand.Create(() => { }, Observable.Return(false));
        Exit = ReactiveCommand.Create(() => ExitRequested?.Invoke());
        Cancel = ReactiveCommand.Create(
            () => _running?.Cancel(), this.WhenAnyValue(model => model.IsBusy));
        ShowAbout = ReactiveCommand.Create(() => AboutRequested?.Invoke());

        // A command that throws has nowhere to put the exception and rethrows it on the
        // scheduler, taking the process with it. Everything reports here instead — the same
        // guard Wlrix.SoftwareManager's main view model documents.
        foreach (var command in new IHandleObservableErrors[]
                     { Open, Extract, Remove, New, Exit, ShowAbout, Cancel })
        {
            _subscriptions.Add(command.ThrownExceptions.Subscribe(OnCommandFailed));
        }
    }

    /// <summary>Column widths, shared by the heading and every row.</summary>
    public ColumnLayout Columns { get; } = new();

    /// <summary>The tree the listing shows.</summary>
    public ObservableCollection<EntryNodeViewModel> Roots { get; } = [];

    /// <summary>The encodings the View menu offers.</summary>
    public ObservableCollection<EncodingChoiceViewModel> Encodings { get; }

    /// <summary>What is selected in the tree. The view keeps this in step.</summary>
    public ObservableCollection<EntryNodeViewModel> Selection { get; } = [];

    public ReactiveCommand<Unit, Unit> New { get; }
    public ReactiveCommand<Unit, Unit> Open { get; }
    public ReactiveCommand<Unit, Unit> Extract { get; }
    public ReactiveCommand<Unit, Unit> Remove { get; }
    public ReactiveCommand<Unit, Unit> Exit { get; }
    public ReactiveCommand<Unit, Unit> ShowAbout { get; }

    /// <summary>Stops the operation in progress. Enabled only while one is running.</summary>
    public ReactiveCommand<Unit, Unit> Cancel { get; }

    /// <summary>Raised (UI thread) to ask the view for an archive to open.</summary>
    public event Func<Task<string?>>? OpenFileRequested;

    /// <summary>Raised (UI thread) to ask the view where to extract to.</summary>
    public event Func<Task<string?>>? DestinationRequested;

    /// <summary>Raised (UI thread) to ask the view to confirm a destructive change.</summary>
    public event Func<string, Task<bool>>? ConfirmRequested;

    /// <summary>Raised (UI thread) to put an error in front of the user.</summary>
    public event Action<string>? ErrorRaised;

    /// <summary>Raised (UI thread) when the About item is chosen.</summary>
    public event Action? AboutRequested;

    /// <summary>Raised (UI thread) when the window should close.</summary>
    public event Action? ExitRequested;

    /// <summary>The archive currently open, or <c>null</c>.</summary>
    public OpenArchive? Archive
    {
        get => _archive;
        private set => this.RaiseAndSetIfChanged(ref _archive, value);
    }

    /// <summary>Whether a long operation is running; the window disables itself meanwhile.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    /// <summary>How far the running operation has got, from 0 to 1.</summary>
    public double Progress
    {
        get => _progress;
        private set => this.RaiseAndSetIfChanged(ref _progress, value);
    }

    /// <summary>
    /// Whether <see cref="Progress"/> means anything yet; the bar hides when it does not.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="IsBusy"/> because plenty of work has no measurable end — <c>7z
    /// l</c> prints nothing until it finishes, and an entry count has no total to divide by. A
    /// bar sitting at zero through all of that says "stuck", which is worse than no bar and the
    /// count in words.
    /// </remarks>
    public bool HasProgress
    {
        get => _hasProgress;
        private set => this.RaiseAndSetIfChanged(ref _hasProgress, value);
    }

    /// <summary>The line under the listing: what happened, or why it is empty.</summary>
    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    /// <summary>The window title: the archive's name, or just the application's.</summary>
    public string Title => Archive is { } archive
        ? $"{System.IO.Path.GetFileName(archive.Path)} — {Strings.Archiver}"
        : Strings.Archiver;

    /// <summary>Whether anything is selected. Gates Remove and the drag source.</summary>
    public bool HasSelection => Selection.Count != 0;

    /// <summary>Whether the open archive will accept files dropped onto it.</summary>
    public bool CanAdd => Archive?.Capabilities.HasFlag(ArchiveCapabilities.Add) == true;

    /// <summary>Whether the listing has no rows to show.</summary>
    public bool IsEmpty => Roots.Count == 0;

    /// <summary>Tells the view model the tree's selection changed.</summary>
    /// <remarks>
    /// Pushed from the view rather than bound: <c>TreeView.SelectedItems</c> is not settable,
    /// so there is nothing to two-way bind to.
    /// </remarks>
    public void SelectionChanged(IEnumerable<EntryNodeViewModel> selected)
    {
        Selection.Clear();
        foreach (var node in selected)
            Selection.Add(node);

        this.RaisePropertyChanged(nameof(HasSelection));
    }

    /// <summary>Opens <paramref name="path"/>, replacing whatever is open.</summary>
    public async Task OpenAsync(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        var format = _registry.Identify(path);
        var backend = _registry.For(format);
        if (backend is null)
        {
            ErrorRaised?.Invoke(Strings.Unsupported(name));
            return;
        }

        await RunAsync(Strings.OpenFailed(name), async token =>
        {
            var archive = await backend.OpenAsync(path, format, ProgressFor(name), token)
                .ConfigureAwait(true);
            _logger.ZLogInformation(
                $"opened {name} as {format} with {archive.Entries.Count} entries");
            Load(archive);
        }).ConfigureAwait(true);
    }

    /// <summary>Adds files from disk to the open archive. The drop target's entry point.</summary>
    public async Task AddAsync(IReadOnlyList<string> sourcePaths)
    {
        if (Archive is not { } archive || sourcePaths.Count == 0)
            return;

        if (!archive.Capabilities.HasFlag(ArchiveCapabilities.Add))
        {
            ErrorRaised?.Invoke(Strings.ReadOnly(archive.Format.ToString()));
            return;
        }

        var name = System.IO.Path.GetFileName(archive.Path);
        var backend = _registry.For(archive.Format)!;
        await RunAsync(Strings.AddFailed(name), async token =>
        {
            Status = Strings.Saving(name);
            await backend.AddAsync(archive.Path, archive.Format, sourcePaths, "", token)
                .ConfigureAwait(true);
            await ReloadAsync(archive, token).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Extracts the selection to a scratch directory and returns the files to hand a drop
    /// target. Empty when nothing is selected or no archive is open.
    /// </summary>
    public async Task<IReadOnlyList<string>> StageSelectionForDragAsync()
    {
        if (Archive is not { } archive || Selection.Count == 0)
            return [];

        try
        {
            return await _staging
                .StageAsync(archive, Selection.Select(node => node.Path).ToList())
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is ArchiveException or IOException
                                       or UnauthorizedAccessException)
        {
            // A failed drag reports and comes to nothing, rather than tearing down the window:
            // the user still has the archive open and the menu still works.
            _logger.ZLogWarning($"could not stage the selection for a drag: {ex.Message}");
            ErrorRaised?.Invoke(ex.Message);
            return [];
        }
    }

    /// <summary>Re-reads the open archive under a different filename encoding.</summary>
    internal async Task ApplyEncodingAsync(FilenameEncoding encoding)
    {
        _decoder.Selected = encoding;
        foreach (var choice in Encodings)
            choice.Refresh();

        if (Archive is not { } archive)
            return;

        // Re-reading is the same cost as opening — on a large gzip, the same fifty seconds — so
        // it goes through RunAsync for the progress bar and the cancel, not straight to the
        // backend.
        var name = System.IO.Path.GetFileName(archive.Path);
        await RunAsync(Strings.OpenFailed(name),
            token => ReloadAsync(archive, token)).ConfigureAwait(true);
    }

    /// <summary>What the "Automatic" menu item says once detection has settled on something.</summary>
    internal string? DetectedEncodingName => _decoder.Detected?.DisplayName;

    /// <summary>The id of the encoding in force; the View menu checks the item that matches.</summary>
    internal string SelectedEncodingId => _decoder.Selected.Id;

    private async Task OpenRequestedAsync()
    {
        if (OpenFileRequested is null)
            return;

        if (await OpenFileRequested().ConfigureAwait(true) is { } path)
            await OpenAsync(path).ConfigureAwait(true);
    }

    private async Task ExtractRequestedAsync()
    {
        if (Archive is not { } archive || DestinationRequested is null)
            return;

        if (await DestinationRequested().ConfigureAwait(true) is not { } destination)
            return;

        // "Extract selected or all contents if none" — an empty list means everything, all the
        // way down to the backends.
        var wanted = Selection.Select(node => node.Path).ToList();
        var name = System.IO.Path.GetFileName(archive.Path);
        var backend = _registry.For(archive.Format)!;

        await RunAsync(Strings.ExtractFailed(name), async token =>
        {
            await backend.ExtractAsync(archive.Path, archive.Format, wanted, destination,
                    flatten: false, ProgressFor(name), token)
                .ConfigureAwait(true);
            // Two sentences rather than one with a substituted subject: "Extracted 3 to /tmp"
            // is not a sentence, and neither is its Japanese.
            Status = wanted.Count == 0
                ? Strings.ExtractedTo(name, destination)
                : Strings.ExtractedCountTo(wanted.Count, destination);
        }).ConfigureAwait(true);
    }

    private async Task RemoveRequestedAsync()
    {
        if (Archive is not { } archive || Selection.Count == 0 || ConfirmRequested is null)
            return;

        var what = Selection.Count == 1
            ? Selection[0].Name
            : Selection.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        if (!await ConfirmRequested(Strings.ConfirmRemove(what)).ConfigureAwait(true))
            return;

        var doomed = Selection.Select(node => node.Path).ToList();
        var name = System.IO.Path.GetFileName(archive.Path);
        var backend = _registry.For(archive.Format)!;

        await RunAsync(Strings.RemoveFailed(name), async token =>
        {
            Status = Strings.Saving(name);
            await backend.RemoveAsync(archive.Path, archive.Format, doomed, token)
                .ConfigureAwait(true);
            await ReloadAsync(archive, token).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task ReloadAsync(OpenArchive archive, CancellationToken cancellationToken)
    {
        var backend = _registry.For(archive.Format)!;
        var name = System.IO.Path.GetFileName(archive.Path);
        Load(await backend.OpenAsync(archive.Path, archive.Format, ProgressFor(name),
            cancellationToken).ConfigureAwait(true));
    }

    /// <summary>Puts an archive's entries into the tree.</summary>
    private void Load(OpenArchive archive)
    {
        Archive = archive;
        Roots.Clear();
        foreach (var node in ArchiveTree.Build(archive.Entries))
            Roots.Add(new EntryNodeViewModel(node, Columns));

        SelectionChanged([]);
        // Detection happens during the read above, so the menu label can only be right now.
        foreach (var choice in Encodings)
            choice.Refresh();

        Status = Roots.Count == 0 ? Strings.EmptyArchive : string.Empty;
        this.RaisePropertyChanged(nameof(Title));
        this.RaisePropertyChanged(nameof(CanAdd));
        this.RaisePropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// Runs <paramref name="work"/> with the busy flag set, turning failure into a dialog.
    /// </summary>
    /// <remarks>
    /// The token is the reason this takes a delegate over one: reading a multi-gigabyte gzip
    /// takes the better part of a minute and the user has to be able to stop it, so every
    /// operation runs under a source this owns and cancels on the next one.
    /// </remarks>
    private async Task RunAsync(string failureMessage, Func<CancellationToken, Task> work)
    {
        // Starting a second operation abandons the first rather than queueing behind it: the
        // common case is opening a large archive and immediately realizing it was the wrong one.
        var previous = _running;
        _running = new CancellationTokenSource();
        previous?.Cancel();
        previous?.Dispose();
        var token = _running.Token;

        IsBusy = true;
        try
        {
            await work(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The user's own doing, so no dialog. Say so in the status line and leave whatever
            // was already listed alone.
            Status = Strings.StatusCanceled;
        }
        catch (ArchiveException ex)
        {
            // Already a sentence written for a person; the generic message would be less
            // informative than what the backend said.
            _logger.ZLogWarning($"{failureMessage} {ex.Message}");
            ErrorRaised?.Invoke(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or InvalidOperationException)
        {
            _logger.ZLogWarning($"{failureMessage} {ex.Message}");
            ErrorRaised?.Invoke($"{failureMessage} {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            HasProgress = false;
        }
    }

    /// <summary>
    /// A progress sink that drives <see cref="Status"/> and <see cref="Progress"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="System.Progress{T}"/> captures the synchronization context it is constructed
    /// on and posts every report back to it, which is what makes this safe to hand to a backend
    /// running on the thread pool. Constructed here, in a method the UI thread calls — building
    /// it anywhere else would post the updates to the wrong thread, or to none.
    ///
    /// The backends already rate-limit what they send; see <c>ProgressThrottle</c>.
    /// </remarks>
    private IProgress<ArchiveProgress> ProgressFor(string name) =>
        new Progress<ArchiveProgress>(report =>
        {
            HasProgress = report.Fraction is not null;
            if (report.Fraction is { } fraction)
                Progress = fraction;

            Status = report.Phase switch
            {
                ArchivePhase.Decompressing => Strings.Decompressing(name),
                ArchivePhase.Reading when report.Count > 0 =>
                    Strings.ReadingCount(name, report.Count),
                ArchivePhase.Reading => Strings.Reading(name),
                ArchivePhase.Extracting when report.Count > 0 =>
                    Strings.ExtractingCount(report.Count),
                ArchivePhase.Extracting => Strings.StatusExtracting,
                ArchivePhase.Saving => Strings.Saving(name),
                _ => Status,
            };
        });

    private void OnCommandFailed(Exception exception)
    {
        _logger.ZLogError($"command failed: {exception}");
        ErrorRaised?.Invoke(exception.Message);
    }

    public void Dispose()
    {
        // Cancel first: a read in flight holds a scratch file open, and letting the window close
        // out from under it leaves that behind.
        _running?.Cancel();
        _running?.Dispose();
        _running = null;

        foreach (var subscription in _subscriptions)
            subscription.Dispose();
        _subscriptions.Clear();
    }
}

/// <summary>One entry in <c>View ▸ Encoding</c>.</summary>
public sealed class EncodingChoiceViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _owner;

    internal EncodingChoiceViewModel(FilenameEncoding encoding, MainWindowViewModel owner)
    {
        Encoding = encoding;
        _owner = owner;
        Choose = ReactiveCommand.CreateFromTask(() => owner.ApplyEncodingAsync(encoding));
    }

    internal FilenameEncoding Encoding { get; }

    /// <summary>
    /// The label. "Automatic" grows a parenthetical once it has actually detected something, so
    /// the menu answers "what did it decide?" without a separate status line for it.
    /// </summary>
    public string Header => Encoding.IsAutomatic && _owner.DetectedEncodingName is { } detected
        ? Strings.AutomaticDetected(detected)
        : Encoding.DisplayName;

    /// <summary>Whether this is the encoding in force; the menu shows a check beside it.</summary>
    /// <remarks>
    /// Keyed on what the user chose, not on what detection landed on. With "Automatic" in force
    /// and Shift-JIS detected, the check stays on Automatic — moving it to Shift-JIS would say
    /// the encoding had been pinned there, and the next archive would prove that false.
    /// </remarks>
    public bool IsChecked => Encoding.Id == _owner.SelectedEncodingId;

    public ReactiveCommand<Unit, Unit> Choose { get; }

    /// <summary>Re-reads <see cref="Header"/> and <see cref="IsChecked"/> after a change.</summary>
    internal void Refresh()
    {
        this.RaisePropertyChanged(nameof(Header));
        this.RaisePropertyChanged(nameof(IsChecked));
    }
}
