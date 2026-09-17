using System.Reactive;
using Avalonia.Collections;
using ReactiveUI;
using Wlrix.Files.Core;
using Wlrix.Files.Core.Listing;
using Wlrix.Files.Core.Platform;
using Wlrix.Files.Core.Portal;
using Wlrix.Files.Core.State;
using Wlrix.FilePicker.Localization;
using Wlrix.FilePicker.Services;

namespace Wlrix.FilePicker.ViewModels;

/// <summary>Somewhere in the rail on the left.</summary>
public sealed record PlaceViewModel(string Label, Location Location);

/// <summary>The whole dialog.</summary>
/// <remarks>
/// One window, one folder, no tabs and no panes. Everything below the surface is the file
/// manager's: <see cref="DirectoryListing"/> reads the directory through the same batching
/// pipeline, so a chooser opened on a directory of a hundred thousand files paints its first
/// screenful as quickly as the file manager does.
/// </remarks>
public sealed class PickerViewModel : ViewModelBase, IDisposable
{
    private readonly FileChooserRequest _request;
    private readonly IFileSystem _files;
    private readonly IconService _icons;

    /// <summary>Every row of the current directory, before the filter and the hidden rule.</summary>
    /// <remarks>
    /// Kept so that changing the filter or showing hidden files is a re-filter of what is
    /// already in memory rather than a re-read of the directory.
    /// </remarks>
    private List<EntryViewModel> _all = [];

    private CancellationTokenSource? _loading;
    private Location _location;
    private string _fileName = "";
    private string _status = "";
    private bool _showHidden;
    private bool _busy;
    private FileFilter? _filter;

    public PickerViewModel(
        FileChooserRequest request,
        IFileSystem files,
        IconService icons,
        IReadOnlyList<PlaceViewModel> places,
        Location start)
    {
        _request = request;
        _files = files;
        _icons = icons;
        _location = start;
        Places = places;

        Filters = request.Filters;
        _filter = Filters.Count == 0
            ? null
            : Filters[request.CurrentFilter >= 0 && request.CurrentFilter < Filters.Count ? request.CurrentFilter : 0];

        Choices = [.. request.Choices.Select(choice => new ChoiceViewModel(choice))];
        _fileName = request.StartingName();

        GoUp = ReactiveCommand.Create(() =>
        {
            if (_location.Parent is { } parent)
                _ = GoAsync(parent);
        });
        ToggleHidden = ReactiveCommand.Create(() => { ShowHidden = !ShowHidden; });
        Accept = ReactiveCommand.Create(AcceptSelection);
        Cancel = ReactiveCommand.Create(() => CloseRequested?.Invoke(null));
    }

    /// <summary>The rows on screen.</summary>
    /// <remarks>
    /// An <see cref="AvaloniaList{T}"/>, not an <c>ObservableCollection</c>: a batch is added
    /// with one ranged notification, where a hundred thousand individual ones is the stall the
    /// whole pipeline exists to avoid.
    /// </remarks>
    public AvaloniaList<EntryViewModel> Entries { get; } = [];

    public IReadOnlyList<PlaceViewModel> Places { get; }

    public IReadOnlyList<FileFilter> Filters { get; }

    public IReadOnlyList<ChoiceViewModel> Choices { get; }

    public ReactiveCommand<Unit, Unit> GoUp { get; }
    public ReactiveCommand<Unit, Unit> ToggleHidden { get; }
    public ReactiveCommand<Unit, Unit> Accept { get; }
    public ReactiveCommand<Unit, Unit> Cancel { get; }

    /// <summary>The rows the user has selected.</summary>
    /// <remarks>
    /// Pushed in by the view rather than bound, because <c>SelectedItems</c> on a
    /// multi-selection list is a live collection rather than a property a view model can own.
    /// </remarks>
    public IReadOnlyList<EntryViewModel> Selection { get; private set; } = [];

    /// <summary>The view reporting what is selected now.</summary>
    /// <remarks>
    /// A method rather than a setter because it does something: in a save dialog, moving onto a
    /// file puts that file's name in the field. That is what makes "save over this one" a
    /// matter of arrowing to it, and it is what every other chooser on the system does — but
    /// only for a single file, since two selected rows have no one name between them, and never
    /// for a folder, whose name in the save field would be a file named after a directory.
    /// </remarks>
    public void SetSelection(IReadOnlyList<EntryViewModel> rows)
    {
        Selection = rows;
        if (ShowNameField && rows is [{ IsDirectory: false } file])
            FileName = file.Name;
    }

    public string Title => _request.Title.Length > 0
        ? _request.Title
        : Strings.DefaultTitle(_request.Mode, _request.Directory);

    public string AcceptText => _request.AcceptLabel.Length > 0
        ? _request.AcceptLabel
        : Strings.DefaultAccept(_request.Mode);

    /// <summary>Which of the three calls this is.</summary>
    public FileChooserMode Mode => _request.Mode;

    /// <summary>Whether more than one file may be chosen.</summary>
    public bool Multiple => _request.Multiple;

    /// <summary>How many files a <c>SaveFiles</c> request carries.</summary>
    public int FileCount => _request.Files.Count;

    /// <summary>Whether the name field is shown, which is what makes this a save dialog.</summary>
    public bool ShowNameField => _request.Mode == FileChooserMode.Save;

    public bool ShowFilters => Filters.Count > 0;

    public bool ShowChoices => Choices.Count > 0;

    public Location Location => _location;

    public string PathText => _location.Path;

    public bool CanGoUp => _location.Parent is not null;

    public string FileName
    {
        get => _fileName;
        set => this.RaiseAndSetIfChanged(ref _fileName, value);
    }

    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    /// <summary>Whether a directory is being read. The accept button is off while it is.</summary>
    public bool IsBusy
    {
        get => _busy;
        private set => this.RaiseAndSetIfChanged(ref _busy, value);
    }

    public bool ShowHidden
    {
        get => _showHidden;
        set
        {
            this.RaiseAndSetIfChanged(ref _showHidden, value);
            Refilter();
        }
    }

    public FileFilter? SelectedFilter
    {
        get => _filter;
        set
        {
            this.RaiseAndSetIfChanged(ref _filter, value);
            Refilter();
        }
    }

    /// <summary>Asked when accepting would replace a file. The answer decides.</summary>
    public event Func<string, Task<bool>>? ConfirmOverwriteRequested;

    /// <summary>Something went wrong and the user should be told.</summary>
    public event Action<string>? ErrorRaised;

    /// <summary>The question is answered. Null means canceled.</summary>
    public event Action<FileChooserResult?>? CloseRequested;

    /// <summary>
    /// The rows were rebuilt, and these are the ones that were selected and still exist.
    /// </summary>
    /// <remarks>
    /// The view puts the selection back. Changing the filter or showing hidden files replaces
    /// every row, and a list whose items are replaced has no selection and nothing focused —
    /// so without this, Ctrl+H left the dialog with the arrow keys doing nothing at all, which
    /// is a worse state than the one the shortcut was pressed from.
    /// </remarks>
    public event Action<IReadOnlyList<EntryViewModel>>? Refiltered;

    /// <summary>Open a folder.</summary>
    public Task GoAsync(Location target)
    {
        _location = target;
        this.RaisePropertyChanged(nameof(Location));
        this.RaisePropertyChanged(nameof(PathText));
        this.RaisePropertyChanged(nameof(CanGoUp));
        return ReloadAsync();
    }

    /// <summary>What a double-click, or Return on the listing, does to a row.</summary>
    public Task ActivateAsync(EntryViewModel row)
    {
        if (row.IsDirectory)
            return GoAsync(row.Location);

        // A file. In a save dialog its name fills the field rather than answering, because
        // double-clicking a file there means "this one, under this name" and the user may
        // still be about to change it -- and because answering outright would overwrite it
        // with no question asked.
        if (ShowNameField)
        {
            FileName = row.Name;
            return Task.CompletedTask;
        }

        if (_request.Directory)
            return Task.CompletedTask;

        Answer([row.Location]);
        return Task.CompletedTask;
    }

    /// <summary>Read the current directory.</summary>
    public async Task ReloadAsync()
    {
        var previous = _loading;
        _loading = new CancellationTokenSource();
        var token = _loading.Token;
        if (previous is not null)
        {
            await previous.CancelAsync().ConfigureAwait(true);
            previous.Dispose();
        }

        Entries.Clear();
        _all = [];
        IsBusy = true;
        Status = Strings.Catalog.Get("StatusLoading");

        var arrived = new List<EntryViewModel>();
        try
        {
            var listing = new DirectoryListing(_files, _location);
            await foreach (var batch in listing.ReadAsync(token).ConfigureAwait(true))
            {
                foreach (var entry in batch.Entries)
                    arrived.Add(new EntryViewModel(entry));
            }

            if (token.IsCancellationRequested)
                return;

            // Sorted once at the end rather than inserted into a sorted list as they arrive,
            // which is quadratic and shows as a stall on a large directory. Folders first and
            // then by name, with no way to change it: a chooser is somewhere to find one file,
            // and a sort menu is the file manager's job.
            arrived.Sort(static (a, b) =>
                a.IsDirectory != b.IsDirectory
                    ? a.IsDirectory ? -1 : 1
                    : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

            _all = arrived;
            Refilter();

            if (listing.Error is { } error)
                ErrorRaised?.Invoke(error.Message);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later navigation. The newer read owns the list now.
        }
        catch (FileOperationException ex)
        {
            Status = "";
            ErrorRaised?.Invoke(ex.Message);
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsBusy = false;
        }
    }

    /// <summary>Applies the filter and the hidden rule to what has already been read.</summary>
    private void Refilter()
    {
        var showing = _all.Where(Admits).ToList();
        // Reference equality is the right comparison: these are the same row objects, held
        // back in `_all` rather than rebuilt, so a row that survives the filter is the very
        // one that was selected.
        var keep = Selection.Where(showing.Contains).ToList();

        Entries.Clear();
        Entries.AddRange(showing);
        Status = showing.Count == 0
            ? Strings.Catalog.Get("StatusEmpty")
            : Strings.Items(showing.Count);
        Refiltered?.Invoke(keep);
    }

    /// <summary>Whether a row is shown.</summary>
    private bool Admits(EntryViewModel row)
    {
        if (row.Entry.IsHidden && !_showHidden)
            return false;

        // A folder is how you reach the rest of the filesystem, so no filter may hide one --
        // and in directory mode a folder is the only thing there is to choose.
        if (row.IsDirectory)
            return true;
        if (_request.Directory)
            return false;

        return _filter is null || _filter.Matches(row.Name, () => _icons.MimeTypeOf(row.Entry));
    }

    private void AcceptSelection() => _ = AcceptAsync();

    /// <summary>The accept button, and Return anywhere that is not the listing.</summary>
    internal async Task AcceptAsync()
    {
        var decision = AcceptPolicy.Decide(
            _request,
            _location,
            [.. Selection.Select(row => row.Entry)],
            FileName,
            Probe);

        switch (decision.Action)
        {
            case AcceptAction.Navigate when decision.Target is { } target:
                await GoAsync(target).ConfigureAwait(true);
                return;

            case AcceptAction.ConfirmOverwrite when decision.Chosen is { Count: > 0 } chosen:
                if (ConfirmOverwriteRequested is null)
                    return;
                if (await ConfirmOverwriteRequested(chosen[0].Name).ConfigureAwait(true))
                    Answer(chosen);
                return;

            case AcceptAction.Accept when decision.Chosen is { Count: > 0 } accepted:
                Answer(accepted);
                return;

            default:
                // Nothing to answer with. Silent on purpose: the button is pressed by Return
                // as well, and an error dialog for an empty selection would fire on a stray
                // keystroke.
                return;
        }
    }

    /// <summary>What is at a location, or null if nothing is.</summary>
    /// <remarks>
    /// Synchronous and against the local filesystem directly. A chooser answers about one
    /// path at the moment a button is pressed, and an async probe would mean the decision
    /// being made a turn after the press that asked for it.
    /// </remarks>
    private static FileKind? Probe(Location location)
    {
        if (!location.TryGetLocalPath(out var path))
            return null;
        if (System.IO.Directory.Exists(path))
            return FileKind.Directory;
        return File.Exists(path) ? FileKind.File : null;
    }

    private void Answer(IReadOnlyList<Location> chosen)
    {
        CloseRequested?.Invoke(new FileChooserResult
        {
            // The interface requires file:// URIs and nothing else, which is what this dialog
            // offers: it browses the local filesystem only. A remote share would have to be
            // staged to a local file first, and handing an application a copy it believes is
            // the original is worse than not offering the share.
            Uris = [.. chosen.Select(location => location.ToUriString())],
            Choices = [.. Choices.Select(choice => choice.Answer())],
            CurrentFilter = _filter is null ? -1 : IndexOfFilter(_filter),
            // An open is read access unless the application asked to save. The interface
            // defaults this to false and an application may act on it.
            Writable = _request.Mode != FileChooserMode.Open,
        });
    }

    private int IndexOfFilter(FileFilter filter)
    {
        for (var index = 0; index < Filters.Count; index++)
        {
            if (ReferenceEquals(Filters[index], filter))
                return index;
        }

        return -1;
    }

    /// <summary>Asks for a row's icon. Called when its container is realized.</summary>
    public void EnsureIcon(EntryViewModel row) => row.EnsureIcon(_icons, IconSize);

    /// <summary>The icon size the listing draws at.</summary>
    public const int IconSize = 16;

    public void Dispose()
    {
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;
    }

    /// <summary>The rail: the user's own directories, then their bookmarks.</summary>
    /// <remarks>
    /// The bookmarks are the file manager's own, read from the same <c>bookmarks.json</c>.
    /// Somebody who bookmarked a folder there expects it here — that is most of why a
    /// desktop's own chooser is worth having over the toolkit's.
    /// </remarks>
    public static IReadOnlyList<PlaceViewModel> ReadPlaces(XdgUserDirs dirs, FilesStateStore state)
    {
        var places = new List<PlaceViewModel>
        {
            new(Strings.Catalog.Get("PlaceHome"), dirs.Home),
        };

        foreach (var directory in dirs.Places())
            places.Add(new PlaceViewModel(Strings.PlaceName(directory.Key), directory.Location));

        foreach (var bookmark in state.Bookmarks)
        {
            if (!Location.TryParse(bookmark.Uri, out var location) || !location.IsLocal)
                continue;
            places.Add(new PlaceViewModel(
                bookmark.Label.Length > 0 ? bookmark.Label : location.Name, location));
        }

        return places;
    }
}
