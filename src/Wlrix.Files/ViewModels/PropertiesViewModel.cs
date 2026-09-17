using System.Globalization;
using System.Reactive;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ReactiveUI;
using Wlrix.Files.Core;
using Wlrix.Files.Core.Metadata;
using Wlrix.Files.Core.Mime;
using Wlrix.Files.Core.Platform;
using Wlrix.Files.Localization;
using Wlrix.Files.Services;

namespace Wlrix.Files.ViewModels;

/// <summary>What the properties window shows, and the permissions it can change.</summary>
/// <remarks>
/// One view model for one file, several files, or a directory, because the window is the same
/// window and the differences are all "which lines have something to say". A permissions grid
/// appears only for a single item on a filesystem that has modes; everything else degrades to
/// a line of text rather than a disabled control, since a disabled checkbox invites the
/// question of how to enable it.
///
/// <para>
/// A directory's size is not known when the window opens and cannot be — finding it means
/// walking the tree. The window opens immediately with the total counting up, which is why
/// this owns a cancellation source and a background task rather than taking its numbers from
/// the caller.
/// </para>
/// </remarks>
public sealed class PropertiesViewModel : ReactiveObject, IDisposable
{
    /// <summary>How often the counting total is allowed to redraw.</summary>
    /// <remarks>
    /// A tree of a hundred thousand files reports a hundred thousand times. Left unthrottled
    /// that is a hundred thousand property changes on the UI thread, which is slower than the
    /// walk it is reporting and makes the window unusable while it runs — the exact failure
    /// the whole application is built to avoid.
    /// </remarks>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>The heading icon's size, matching the <c>Image</c> in the window.</summary>
    private const int IconSize = 48;

    private readonly IFileSystemProvider _provider;
    private readonly SharedMimeDatabase _mime;
    private readonly IconService _icons;
    private readonly MountTable _mounts;
    private readonly IReadOnlyList<FileEntry> _targets;
    private readonly CancellationTokenSource _walk = new();

    /// <summary>
    /// The walk's token, taken once.
    /// </summary>
    /// <remarks>
    /// Not read from the source each time it is wanted: closing the window disposes the
    /// source while the loads are still in flight, and <see cref="CancellationTokenSource
    /// .Token"/> throws once disposed. A token taken beforehand keeps answering — already
    /// canceled — which is exactly what the awaiting code should hear.
    /// </remarks>
    private readonly CancellationToken _stopping;

    private Bitmap? _icon;
    private string _kindText = string.Empty;
    private string _sizeText = Strings.PropCalculating;
    private string? _contentsText;
    private string? _freeSpaceText;
    private string? _problem;
    private UnixPermissions _permissions;
    private UnixPermissions _original;
    private string _octalText = string.Empty;
    private bool _updatingOctal;
    private bool _counted;

    public PropertiesViewModel(
        IFileSystemProvider provider,
        IconService icons,
        SharedMimeDatabase mime,
        MountTable mounts,
        IReadOnlyList<FileEntry> targets)
    {
        ArgumentOutOfRangeException.ThrowIfZero(targets.Count);

        _provider = provider;
        _icons = icons;
        _mime = mime;
        _mounts = mounts;
        _targets = targets;
        _stopping = _walk.Token;

        Single = targets.Count == 1 ? targets[0] : null;
        Title = Single is not null
            ? Strings.PropertiesTitle(Single.Name)
            : Strings.PropertiesTitleMany(targets.Count);

        Name = Single?.Name ?? Strings.PropItemCount(targets.Count);
        _kindText = DescribeKind(null);
        LocationText = DescribeLocation();
        ModifiedText = Single?.Modified is { } modified
            ? modified.ToLocalTime().ToString("f", CultureInfo.CurrentCulture)
            : string.Empty;
        LinkTargetText = Single?.SymlinkTarget;

        Apply = ReactiveCommand.CreateFromTask(ApplyAsync);
    }

    /// <summary>The one entry this is about, or null when it is about several.</summary>
    public FileEntry? Single { get; }

    public string Title { get; }

    public string Name { get; }

    /// <summary>What kind of thing this is, in words where the database has words for it.</summary>
    /// <remarks>
    /// Starts as the name's answer and sharpens once the file has been sniffed, which is the
    /// same order everything else uses. A window that opened blank while a share was read
    /// would be worse than one that starts right and gets more specific.
    /// </remarks>
    public string KindText
    {
        get => _kindText;
        private set => this.RaiseAndSetIfChanged(ref _kindText, value);
    }

    /// <summary>The directory holding it, or the shared one when several are selected.</summary>
    public string LocationText { get; }

    public string ModifiedText { get; }

    /// <summary>Where a link points, or null if this is not one.</summary>
    public string? LinkTargetText { get; }

    public bool HasLinkTarget => LinkTargetText is not null;

    public bool HasModified => ModifiedText.Length > 0;

    public Bitmap? Icon
    {
        get => _icon;
        private set => this.RaiseAndSetIfChanged(ref _icon, value);
    }

    /// <summary>The total size, counting up while the walk runs.</summary>
    public string SizeText
    {
        get => _sizeText;
        private set => this.RaiseAndSetIfChanged(ref _sizeText, value);
    }

    /// <summary>How many things are inside, for a directory. Null for a plain file.</summary>
    public string? ContentsText
    {
        get => _contentsText;
        private set
        {
            this.RaiseAndSetIfChanged(ref _contentsText, value);
            this.RaisePropertyChanged(nameof(HasContents));
        }
    }

    public bool HasContents => _contentsText is not null;

    public string? FreeSpaceText
    {
        get => _freeSpaceText;
        private set
        {
            this.RaiseAndSetIfChanged(ref _freeSpaceText, value);
            this.RaisePropertyChanged(nameof(HasFreeSpace));
        }
    }

    public bool HasFreeSpace => _freeSpaceText is not null;

    /// <summary>What went wrong, when something did. Shown in place of nothing happening.</summary>
    public string? Problem
    {
        get => _problem;
        private set
        {
            this.RaiseAndSetIfChanged(ref _problem, value);
            this.RaisePropertyChanged(nameof(HasProblem));
        }
    }

    public bool HasProblem => _problem is not null;

    /// <summary>Whether there is a mode to show at all.</summary>
    public bool HasPermissions { get; private set; }

    /// <summary>Whether this filesystem will accept a new one.</summary>
    public bool CanEditPermissions { get; private set; }

    /// <summary>The <c>ls</c> line, kept in step with the checkboxes.</summary>
    public string PermissionsText => _permissions.Describe(Single?.Kind ?? FileKind.File);

    /// <summary>
    /// The mode as four octal digits, editable.
    /// </summary>
    /// <remarks>
    /// Two-way, and typing in it moves the checkboxes. Unparsable text is left alone rather
    /// than reverted mid-keystroke: "7" on the way to "755" is not yet a mode, and snapping
    /// it back would make the field impossible to type in.
    /// </remarks>
    public string OctalText
    {
        get => _octalText;
        set
        {
            this.RaiseAndSetIfChanged(ref _octalText, value);
            if (_updatingOctal || !UnixPermissions.TryParseOctal(value, out var parsed))
                return;
            SetPermissions(parsed, updateOctal: false);
        }
    }

    public bool OwnerRead { get => Get(PermissionClass.Owner, PermissionBits.Read); set => Set(PermissionClass.Owner, PermissionBits.Read, value); }
    public bool OwnerWrite { get => Get(PermissionClass.Owner, PermissionBits.Write); set => Set(PermissionClass.Owner, PermissionBits.Write, value); }
    public bool OwnerExecute { get => Get(PermissionClass.Owner, PermissionBits.Execute); set => Set(PermissionClass.Owner, PermissionBits.Execute, value); }
    public bool GroupRead { get => Get(PermissionClass.Group, PermissionBits.Read); set => Set(PermissionClass.Group, PermissionBits.Read, value); }
    public bool GroupWrite { get => Get(PermissionClass.Group, PermissionBits.Write); set => Set(PermissionClass.Group, PermissionBits.Write, value); }
    public bool GroupExecute { get => Get(PermissionClass.Group, PermissionBits.Execute); set => Set(PermissionClass.Group, PermissionBits.Execute, value); }
    public bool OtherRead { get => Get(PermissionClass.Other, PermissionBits.Read); set => Set(PermissionClass.Other, PermissionBits.Read, value); }
    public bool OtherWrite { get => Get(PermissionClass.Other, PermissionBits.Write); set => Set(PermissionClass.Other, PermissionBits.Write, value); }
    public bool OtherExecute { get => Get(PermissionClass.Other, PermissionBits.Execute); set => Set(PermissionClass.Other, PermissionBits.Execute, value); }

    public bool SetUserId
    {
        get => _permissions.SetUserId;
        set => SetPermissions(_permissions.WithSetUserId(value));
    }

    public bool SetGroupId
    {
        get => _permissions.SetGroupId;
        set => SetPermissions(_permissions.WithSetGroupId(value));
    }

    public bool Sticky
    {
        get => _permissions.Sticky;
        set => SetPermissions(_permissions.WithSticky(value));
    }

    /// <summary>Whether the mode has been changed and not yet written.</summary>
    public bool IsDirty => _permissions != _original;

    /// <summary>Writes the mode back, if it changed.</summary>
    public ReactiveCommand<Unit, Unit> Apply { get; }

    /// <summary>
    /// Reads everything the window states as fact, and starts the walk that fills in the size.
    /// </summary>
    /// <remarks>
    /// Called once the window is up, so a directory that takes a minute to add up is a number
    /// climbing in a window already on screen rather than a delay before one appears.
    /// </remarks>
    public async Task LoadAsync()
    {
        await DescribeKindByContentAsync().ConfigureAwait(true);
        await LoadPermissionsAsync().ConfigureAwait(true);
        await LoadFreeSpaceAsync().ConfigureAwait(true);
        await MeasureAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Fetches the icon, separately from everything else.
    /// </summary>
    /// <remarks>
    /// Apart because decoding one needs a rendering platform and nothing else here does, which
    /// is what lets the rest of this be tested without standing up Avalonia. It is also the
    /// only thing the window can do without: an icon that never arrives costs a picture, where
    /// a size that never arrives costs the answer.
    /// </remarks>
    public async Task LoadIconAsync()
    {
        if (Single is null)
            return;
        Icon = await _icons.GetAsync(Single, IconSize).ConfigureAwait(true);
    }

    public void Dispose()
    {
        _walk.Cancel();
        _walk.Dispose();
        Apply.Dispose();
    }

    // --- loading -----------------------------------------------------------

    /// <summary>
    /// Reads the mode from a fresh stat rather than from the listing entry.
    /// </summary>
    /// <remarks>
    /// The entry may be minutes old — a listing is not re-read while a window sits open — and
    /// the whole point of this section is to show and then change the current mode. Writing
    /// back a stale one would undo whatever <c>chmod</c> did in the meantime.
    /// </remarks>
    private async Task LoadPermissionsAsync()
    {
        if (Single is null)
            return;

        try
        {
            var fs = await _provider.GetAsync(Single.Location, _stopping).ConfigureAwait(true);
            var stat = await fs.StatAsync(Single.Location, _stopping).ConfigureAwait(true);
            if (stat.UnixMode is not { } mode)
                return;

            HasPermissions = true;
            CanEditPermissions = fs.Capabilities.HasFlag(FileSystemCapabilities.PosixMode);
            _original = new UnixPermissions(mode);
            SetPermissions(_original);

            this.RaisePropertyChanged(nameof(HasPermissions));
            this.RaisePropertyChanged(nameof(CanEditPermissions));
        }
        catch (Exception ex) when (ex is FileOperationException or OperationCanceledException)
        {
            // Not fatal to the window: everything else it shows came from the listing and is
            // still true. The permissions section simply stays away.
        }
    }

    /// <summary>Sharpens the kind line by reading the file, when the name left it open.</summary>
    /// <remarks>
    /// Only ever for a single file, and only when the name did not already answer — which is
    /// the check inside <see cref="ContentSniffer"/>, so the common case costs no read at all.
    /// </remarks>
    private async Task DescribeKindByContentAsync()
    {
        if (Single is null)
            return;

        try
        {
            var mimeType = await ContentSniffer
                .ResolveAsync(_mime, _provider, Single, _stopping)
                .ConfigureAwait(true);
            KindText = DescribeKind(mimeType);
        }
        catch (Exception ex) when (ex is FileOperationException or OperationCanceledException)
        {
        }
    }

    private async Task LoadFreeSpaceAsync()
    {
        var location = _targets[0].Location;
        try
        {
            var fs = await _provider.GetAsync(location, _stopping).ConfigureAwait(true);
            if (await fs.GetFreeSpaceAsync(location, _stopping).ConfigureAwait(true) is not { } space)
                return;

            FreeSpaceText = Strings.PropFreeOf(
                FileEntryViewModel.FormatSize(space.AvailableBytes),
                FileEntryViewModel.FormatSize(space.TotalBytes));
        }
        catch (Exception ex) when (ex is FileOperationException or OperationCanceledException)
        {
        }
    }

    /// <summary>Walks the selection, showing the total as it climbs.</summary>
    private async Task MeasureAsync()
    {
        var roots = _targets.Select(static entry => entry.Location).ToList();
        var last = DateTime.MinValue;

        var progress = new AnonymousProgress(running =>
        {
            // Sampled by the clock rather than by count, so the rate is the same on a share
            // yielding ten entries a second as on a local disk yielding a million.
            var now = DateTime.UtcNow;
            if (now - last < ProgressInterval)
                return;
            last = now;
            Dispatcher.UIThread.Post(() =>
            {
                // A report posted a moment ago can still be waiting in the queue when the
                // walk finishes, and it would then paint "counting" over the final total —
                // which is what a small directory does every time, since the whole walk fits
                // inside one dispatcher turn. The finished total wins.
                if (!_counted)
                    Show(running);
            });
        });

        try
        {
            var total = await DirectoryUsage
                .MeasureAsync(_provider, roots, progress, _stopping)
                .ConfigureAwait(true);
            _counted = true;
            Show(total);
        }
        catch (OperationCanceledException)
        {
            // The window was closed while counting. Nothing left to tell anyone.
        }
        catch (FileOperationException ex)
        {
            SizeText = string.Empty;
            Problem = ex.Message;
        }
    }

    private void Show(DirectorySize size)
    {
        SizeText = size.Complete
            ? Strings.PropSizeExact(FileEntryViewModel.FormatSize(size.Bytes), size.Bytes)
            : Strings.PropCounting(FileEntryViewModel.FormatSize(size.Bytes), size.Items);

        // A plain file is its own single entry, and "1 item, 0 folders" underneath its size
        // says nothing. The line is for containers.
        var container = _targets.Count > 1 || _targets[0].IsDirectory;
        if (container)
        {
            ContentsText = size.Unreadable > 0
                ? Strings.PropContentsPartial(size.Files, size.Directories, size.Unreadable)
                : Strings.PropContents(size.Files, size.Directories);
        }
    }

    // --- permissions -------------------------------------------------------

    private bool Get(PermissionClass who, PermissionBits what) => _permissions.Has(who, what);

    private void Set(PermissionClass who, PermissionBits what, bool allowed) =>
        SetPermissions(_permissions.With(who, what, allowed));

    /// <summary>
    /// Moves to a new mode and tells every spelling of it to redraw.
    /// </summary>
    /// <remarks>
    /// The checkboxes, the octal field and the <c>ls</c> line are three views of twelve bits,
    /// so one of them changing has to move the other two. Raising the lot is cheap and is the
    /// only arrangement in which they cannot drift apart.
    /// </remarks>
    private void SetPermissions(UnixPermissions permissions, bool updateOctal = true)
    {
        _permissions = permissions;

        if (updateOctal)
        {
            _updatingOctal = true;
            OctalText = permissions.Octal;
            _updatingOctal = false;
        }

        this.RaisePropertyChanged(nameof(OwnerRead));
        this.RaisePropertyChanged(nameof(OwnerWrite));
        this.RaisePropertyChanged(nameof(OwnerExecute));
        this.RaisePropertyChanged(nameof(GroupRead));
        this.RaisePropertyChanged(nameof(GroupWrite));
        this.RaisePropertyChanged(nameof(GroupExecute));
        this.RaisePropertyChanged(nameof(OtherRead));
        this.RaisePropertyChanged(nameof(OtherWrite));
        this.RaisePropertyChanged(nameof(OtherExecute));
        this.RaisePropertyChanged(nameof(SetUserId));
        this.RaisePropertyChanged(nameof(SetGroupId));
        this.RaisePropertyChanged(nameof(Sticky));
        this.RaisePropertyChanged(nameof(PermissionsText));
        this.RaisePropertyChanged(nameof(IsDirty));
    }

    /// <summary>Writes the mode back, if there is one and it changed.</summary>
    /// <remarks>
    /// Silent when nothing changed, rather than issuing a <c>chmod</c> that would be a no-op
    /// on most filesystems and a modification-time change on some.
    /// </remarks>
    private async Task ApplyAsync()
    {
        if (Single is null || !CanEditPermissions || !IsDirty)
            return;

        try
        {
            var fs = await _provider.GetAsync(Single.Location, CancellationToken.None).ConfigureAwait(true);
            await fs.SetUnixModeAsync(Single.Location, _permissions.Mode, CancellationToken.None)
                .ConfigureAwait(true);
            _original = _permissions;
            this.RaisePropertyChanged(nameof(IsDirty));
            Problem = null;
        }
        catch (FileOperationException ex)
        {
            Problem = ex.Message;
        }
    }

    // --- description -------------------------------------------------------

    /// <param name="sniffed">
    /// The type read from the file's contents, or null to use the name's answer. Called twice:
    /// once as the window is built and once when the bytes have been read.
    /// </param>
    private string DescribeKind(string? sniffed)
    {
        if (Single is null)
        {
            var directories = _targets.Count(static entry => entry.IsDirectory);
            return Strings.PropContents(_targets.Count - directories, directories);
        }

        if (Single.IsDirectory)
            return Strings.PropKindFolder;

        var mimeType = sniffed ?? _mime.Resolve(Single);

        // Nothing in the database describes inode/x-empty — there is no XML file for it,
        // because it is a name both readers invent rather than one the spec ships. Showing the
        // bare type for the commonest case of all, a file somebody just created, would be a
        // poor reward for having answered the question precisely.
        if (mimeType == SharedMimeDatabase.Empty)
            return Strings.PropKindEmpty;

        var description = _mime.DescriptionFor(mimeType);

        // The type in parentheses stays even when there is a description, because the
        // description is what a person reads and the type is what an Open With rule matches.
        return description is null ? mimeType : $"{description} ({mimeType})";
    }

    private string DescribeLocation()
    {
        var parent = _targets[0].Location.Parent;
        if (parent is null)
            return _targets[0].Location.ToUriString();

        return parent.IsLocal ? parent.Path : parent.ToUriString();
    }

    /// <summary>
    /// An <see cref="IProgress{T}"/> that reports on the thread that called it.
    /// </summary>
    /// <remarks>
    /// <see cref="Progress{T}"/> captures the synchronization context at construction and
    /// posts every report to it, which here would mean one UI-thread message per file — the
    /// throttling below it would never see them in time to drop any.
    /// </remarks>
    private sealed class AnonymousProgress(Action<DirectorySize> report) : IProgress<DirectorySize>
    {
        public void Report(DirectorySize value) => report(value);
    }
}
