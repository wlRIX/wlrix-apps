using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ReactiveUI;
using Wlrix.Settings.Background.Localization;
using Wlrix.Settings.Background.Services;
using Wlrix.Settings.Client;

namespace Wlrix.Settings.Background.ViewModels;

/// <summary>One tile in the picture list.</summary>
public sealed class WallpaperTile : ViewModelBase
{
    private Bitmap? _thumbnail;
    private bool _decoded;

    /// <summary>The "No Picture" tile, which is the desktop color alone.</summary>
    public WallpaperTile() => Name = Strings.NoPicture;

    public WallpaperTile(Wallpaper wallpaper, bool elsewhere = false)
    {
        Path = wallpaper.Path;
        IsOwn = wallpaper.IsOwn;
        var name = System.IO.Path.GetFileNameWithoutExtension(wallpaper.Path);
        Name = elsewhere ? Strings.Elsewhere(name) : name;
    }

    /// <summary>What is written to <c>background.image</c>; null for the "No Picture" tile.</summary>
    public string? Path { get; }

    public string Name { get; }

    /// <summary>One of the user's own copies, which Remove may delete.</summary>
    public bool IsOwn { get; }

    /// <summary>The small picture, once it has been decoded; null until then, and for good if it cannot be.</summary>
    public Bitmap? Thumbnail => _thumbnail;

    /// <summary>
    /// A picture whose small version cannot be shown -- an XPM, say, which only the desktop
    /// reads -- and which gets a glyph instead. False while it is still being decoded, so a
    /// slow photo does not flash the glyph first.
    /// </summary>
    public bool ShowsGlyph => Path is not null && _decoded && _thumbnail is null;

    /// <summary>The decoder has finished with this tile; <paramref name="thumbnail"/> is null if it failed.</summary>
    public void Decoded(Bitmap? thumbnail)
    {
        _decoded = true;
        _thumbnail = thumbnail;
        this.RaisePropertyChanged(nameof(Thumbnail));
        this.RaisePropertyChanged(nameof(ShowsGlyph));
    }
}

/// <summary>A fit mode, with the label the list shows for it.</summary>
public sealed record ModeChoice(string Value, string Label);

/// <summary>An entry in the Desk option menu: a desk, or every desk at once.</summary>
/// <param name="Id">The desk, or null for "All Desks", which is <c>background.toml</c>.</param>
public sealed record DeskChoice(int? Id, string Label);

/// <summary>
/// Backs the Background window: IRIX's Background panel, which is a list of pictures to put on
/// the desktop, plus how a picture is fitted and the color around it.
/// </summary>
/// <remarks>
/// <para>
/// Everything applies the moment it is chosen. A wallpaper is judged on the real desktop, not in
/// a thumbnail, and a choice costs one file write and one <c>SIGHUP</c> to <c>wlrix-bg</c>, which
/// swaps a buffer. Cancel is what makes that safe: it puts back every key this window changed,
/// exactly as it was when the window opened.
/// </para>
/// <para>
/// The values go through <c>com.wlrix.Settings</c> only. This app knows the six key names and
/// nothing about <c>background.toml</c> or <c>lock.toml</c>.
/// </para>
/// <para>
/// The Desk option menu chooses what the controls edit. "All Desks" is those keys, the
/// background every desk shows unless it has its own; a desk is that desk's own background,
/// which the compositor keeps with it and this window sets over <c>wlrix-desks</c>. A desk
/// without one shows the shared background, and the first change made to it gives it its own,
/// starting from what it was showing. The controls go on speaking in <c>background.*</c> keys
/// either way; <see cref="WriteAsync"/> is where they part.
/// </para>
/// </remarks>
public sealed class BackgroundViewModel : ViewModelBase, IDisposable
{
    /// <summary>How wide a thumbnail is decoded.</summary>
    public const int ThumbnailWidth = 96;

    private readonly WallpaperTile _none = new();

    /// <summary>The effective value of each of the six keys, as last heard from the daemon.</summary>
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>The six keys as they were when the window opened, for Reset and Cancel.</summary>
    private Dictionary<string, OpenValue> _open = new(StringComparer.Ordinal);

    /// <summary>Every key written since the window opened, so Cancel writes back only those.</summary>
    private readonly HashSet<string> _touched = new(StringComparer.Ordinal);

    private SettingsClient? _client;
    private DeskClient? _deskClient;

    /// <summary>The ordinary desks, as the compositor last described them.</summary>
    private IReadOnlyList<DeskEntry> _deskEntries = [];

    /// <summary>
    /// Each desk this window has changed, with the background it had when first changed -- null
    /// for the shared one -- so Cancel can put it back.
    /// </summary>
    private readonly Dictionary<int, DeskBackground?> _deskOpen = [];

    private DeskChoice? _selectedDesk;
    private readonly DeskChoice _allDesks = new(null, Strings.AllDesks);

    /// <summary>
    /// Set while the controls are being moved to match the daemon, so that moving them does not
    /// read as the user choosing something and write it straight back.
    /// </summary>
    private bool _adopting;

    private WallpaperTile? _selected;
    private ModeChoice? _selectedMode;
    private Color _color = Color.FromRgb(0x55, 0x55, 0x55);
    private bool _alsoLockScreen;
    private bool _ready;
    private string _status = string.Empty;

    /// <summary>
    /// Builds the window's model without touching the daemon, so the XAML designer and a session
    /// with no settings service both get something to draw. <see cref="InitializeAsync"/> is what
    /// fills it in.
    /// </summary>
    public BackgroundViewModel()
    {
        Pictures = [_none];
        foreach (var wallpaper in Wallpapers.All())
            Pictures.Add(Tile(wallpaper));
        _selected = _none;
    }

    /// <summary>"All Desks", then every ordinary desk; empty until the compositor has listed them.</summary>
    public ObservableCollection<DeskChoice> Desks { get; } = [];

    /// <summary>Whether there are desks to choose between, which needs wlrix-desks 4.</summary>
    public bool HasDesks => Desks.Count > 1;

    /// <summary>Which background the controls edit. Choosing one shows it.</summary>
    public DeskChoice? SelectedDesk
    {
        get => _selectedDesk;
        set
        {
            // Rebuilding the list clears the selection on the way; the desk being edited stays.
            if (value is null || ReferenceEquals(value, _selectedDesk))
                return;

            this.RaiseAndSetIfChanged(ref _selectedDesk, value);
            RaiseDeskStates();
            if (IsReady)
                Adopt(lockFollows: null);
        }
    }

    /// <summary>The desk being edited, or null for the shared background.</summary>
    private DeskEntry? EditedDesk =>
        _selectedDesk?.Id is { } id ? _deskEntries.FirstOrDefault(desk => desk.Id == id) : null;

    /// <summary>The lock screen follows the shared background, not any one desk's.</summary>
    public bool CanFollowLock => IsReady && EditedDesk is null;

    /// <summary>Only a desk with a background of its own has one to give up.</summary>
    public bool CanUseShared => IsReady && EditedDesk is { Background: not null };

    /// <summary>Said under the option menu while the desk chosen shows the shared background.</summary>
    public string DeskNote => EditedDesk is { Background: null } desk ? Strings.DeskUsesShared(desk.Name) : string.Empty;

    /// <summary>"No Picture" first, then every wallpaper, the user's own before the stock ones.</summary>
    public ObservableCollection<WallpaperTile> Pictures { get; }

    /// <summary>The fit modes, from the daemon's description of <c>background.mode</c>.</summary>
    public ObservableCollection<ModeChoice> Modes { get; } = [];

    /// <summary>The picture on the desktop. Choosing one puts it there.</summary>
    public WallpaperTile? Selected
    {
        get => _selected;
        set
        {
            // A ListBox clears its selection while its items change; keeping the picture that is
            // actually on the desktop highlighted is better than nothing highlighted.
            if (value is null || ReferenceEquals(value, _selected))
                return;

            this.RaiseAndSetIfChanged(ref _selected, value);
            this.RaisePropertyChanged(nameof(CanRemove));
            this.RaisePropertyChanged(nameof(CanFit));
            if (!_adopting)
                _ = ApplyPictureAsync(value);
        }
    }

    /// <summary>How the picture is fitted. Choosing one applies it.</summary>
    public ModeChoice? SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedMode))
                return;

            this.RaiseAndSetIfChanged(ref _selectedMode, value);
            if (!_adopting)
                _ = WriteAsync(new Dictionary<string, object> { [BackgroundKeys.Mode] = value.Value });
        }
    }

    /// <summary>The desktop color, behind the picture and on its own under "No Picture".</summary>
    public Color Color
    {
        get => _color;
        private set
        {
            this.RaiseAndSetIfChanged(ref _color, value);
            this.RaisePropertyChanged(nameof(ColorBrush));
        }
    }

    public IBrush ColorBrush => new SolidColorBrush(_color);

    /// <summary>
    /// Whether the lock screen follows the desktop. Turning it on gives the lock screen what the
    /// desktop has now; turning it off leaves the lock screen as it is.
    /// </summary>
    public bool AlsoLockScreen
    {
        get => _alsoLockScreen;
        set
        {
            if (value == _alsoLockScreen)
                return;

            this.RaiseAndSetIfChanged(ref _alsoLockScreen, value);
            if (value && !_adopting && IsReady)
                _ = CopyToLockAsync();
        }
    }

    /// <summary>Only the user's own pictures can be removed; a stock one is not theirs to delete.</summary>
    public bool CanRemove => IsReady && _selected is { IsOwn: true };

    /// <summary>A fit mode means nothing without a picture to fit.</summary>
    public bool CanFit => IsReady && _selected is { Path: not null };

    /// <summary>Whether the settings have loaded and choosing anything means anything yet.</summary>
    public bool IsReady
    {
        get => _ready;
        private set
        {
            this.RaiseAndSetIfChanged(ref _ready, value);
            this.RaisePropertyChanged(nameof(CanRemove));
            this.RaisePropertyChanged(nameof(CanFit));
            RaiseDeskStates();
        }
    }

    /// <summary>
    /// What happened to the last change, when it is worth saying. Empty when everything applied.
    /// </summary>
    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    /// <summary>
    /// Ask the daemon for the six values, then start listening for changes from anywhere else.
    ///
    /// Failing here leaves the window drawn but not ready: the list is the wallpaper folders'
    /// own and needs nothing, so a session with no settings service still shows the pictures —
    /// it just cannot put one on the desktop, and says so.
    /// </summary>
    public async Task InitializeAsync()
    {
        LoadThumbnails(Pictures);
        try
        {
            _client = await SettingsClient.ConnectAsync().ConfigureAwait(true);

            var mode = await _client.DescribeKeyAsync(BackgroundKeys.Mode).ConfigureAwait(true);
            for (var i = 0; i < mode.Choices.Count; i++)
            {
                if (mode.Choices[i] == BackgroundKeys.SolidMode)
                    continue;
                var fallback = i < mode.ChoiceLabels.Count ? mode.ChoiceLabels[i] : mode.Choices[i];
                Modes.Add(new ModeChoice(mode.Choices[i], Strings.Mode(mode.Choices[i], fallback)));
            }

            var sources = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var ns in new[] { "background", "lock" })
            {
                foreach (var (key, source) in await _client.SourcesAsync(ns).ConfigureAwait(true))
                    sources[key] = source;
            }

            foreach (var key in BackgroundKeys.Desktop.Concat(BackgroundKeys.Lock))
            {
                _values[key] = await _client.GetAsync(key).ConfigureAwait(true) as string ?? string.Empty;
                _open[key] = new OpenValue(_values[key],
                    IsDefault: !sources.TryGetValue(key, out var source) || source == "default");
            }

            await _client.WatchAsync().ConfigureAwait(true);
            _client.Changed += OnChangedElsewhere;
            _client.FileInvalid += OnFileInvalid;

            Adopt(lockFollows: BackgroundKeys.LockFollows(_values));
            IsReady = true;

            // The desks come afterwards and on their own: without a compositor that keeps
            // desk backgrounds, the window is the shared background alone, as it always was.
            _deskClient = new DeskClient();
            _deskClient.Changed += desks => Dispatcher.UIThread.Post(() => OnDesks(desks));
            _deskClient.Start();
        }
        catch (Exception e)
        {
            // Bus activation means "not on the bus" is a broken install rather than an idle
            // session, so say so rather than silently doing nothing.
            Status = Strings.NoSettingsService(e.Message);
        }
    }

    /// <summary>Put the desktop color on <paramref name="color"/>.</summary>
    public Task SetColorAsync(Color color) =>
        WriteAsync(new Dictionary<string, object>
        {
            [BackgroundKeys.Color] = string.Create(CultureInfo.InvariantCulture,
                $"#{color.R:x2}{color.G:x2}{color.B:x2}"),
        });

    /// <summary>
    /// Copy <paramref name="paths"/> into the user's wallpaper folder, list them, and put the
    /// last one on the desktop. A file the desktop cannot show is skipped and named in the
    /// status line rather than copied in to sit there as a gray tile.
    /// </summary>
    public void Add(IEnumerable<string> paths)
    {
        WallpaperTile? last = null;
        foreach (var path in paths)
        {
            var name = System.IO.Path.GetFileName(path);
            if (!Wallpapers.IsSupported(path))
            {
                Status = Strings.Unsupported(name);
                continue;
            }

            try
            {
                var tile = Tile(Wallpapers.Add(path));
                // After the last of the user's own pictures, which is where a rescan would put it.
                var index = 1 + Pictures.Skip(1).TakeWhile(existing => existing.IsOwn).Count();
                Pictures.Insert(index, tile);
                LoadThumbnails([tile]);
                last = tile;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Status = Strings.AddFailed(name, e.Message);
            }
        }

        if (last is not null)
            Selected = last;
    }

    /// <summary>
    /// Delete the selected picture, which is one of the user's own.
    ///
    /// It is the one on the desktop — choosing is applying — so the desktop moves to the next
    /// picture in the list first, and only then is the file deleted, so <c>wlrix-bg</c> is never
    /// pointed at a file that is not there. If it was also the picture the window opened with,
    /// Cancel can no longer put it back, and puts back the replacement instead.
    /// </summary>
    public async Task RemoveSelectedAsync()
    {
        if (_selected is not { IsOwn: true, Path: { } path } doomed)
            return;

        // The picture that takes its place in the list, or the one before it at the end.
        var index = Pictures.IndexOf(doomed);
        var next = index + 1 < Pictures.Count ? Pictures[index + 1] : Pictures[index - 1];
        // Moved without the setter's own write, so the write can be awaited before the delete.
        _adopting = true;
        Selected = next;
        _adopting = false;
        await ApplyPictureAsync(next).ConfigureAwait(true);

        foreach (var key in new[] { BackgroundKeys.Image, BackgroundKeys.ToLock(BackgroundKeys.Image) })
        {
            if (_open.TryGetValue(key, out var opened) && opened.Value == path)
                _open[key] = new OpenValue(next.Path ?? string.Empty, IsDefault: false);
        }

        try
        {
            Wallpapers.Remove(new Wallpaper(path, IsOwn: true));
            Pictures.Remove(doomed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = Strings.RemoveFailed(doomed.Name, e.Message);
        }
    }

    /// <summary>Put back everything this window changed, and stay open.</summary>
    public async Task ResetAsync()
    {
        await RevertAsync().ConfigureAwait(true);
        _touched.Clear();
        _deskOpen.Clear();
    }

    /// <summary>Take the chosen desk's own background away, so it shows the shared one again.</summary>
    public void UseSharedBackground()
    {
        if (_deskClient is null || EditedDesk is not { Background: not null } desk)
            return;

        _deskOpen.TryAdd(desk.Id, desk.Background);
        _deskClient.ClearBackground(desk.Id);
        // Shown at once rather than when the compositor answers, so the controls do not sit on
        // the old background for a poll.
        Replace(desk with { Background = null });
        Adopt(lockFollows: null);
    }

    /// <summary>
    /// What Cancel does before the window closes: put back every key written since the window
    /// opened, as it was. IRIX's Cancel was "leave things as you found them", not "close".
    /// </summary>
    public async Task RevertAsync()
    {
        if (_deskClient is not null)
        {
            foreach (var (id, background) in _deskOpen)
            {
                if (background is null)
                    _deskClient.ClearBackground(id);
                else
                    _deskClient.SetBackground(id, background);
            }
            // The client sends them from its own thread; the window is about to close, and the
            // compositor should have heard before it does.
            if (_deskOpen.Count > 0)
                await Task.Delay(250).ConfigureAwait(true);
        }

        if (_client is null || _touched.Count == 0)
            return;

        var (sets, resets) = BackgroundKeys.RevertPlan(_open, _touched);
        try
        {
            if (sets.Count > 0)
                await _client.SetManyAsync(sets).ConfigureAwait(true);
            if (resets.Count > 0)
                await _client.ResetAsync([.. resets]).ConfigureAwait(true);
            Status = string.Empty;
        }
        catch (Exception e)
        {
            Status = e.Message.Split('\n')[0];
        }
    }

    public void Dispose()
    {
        _deskClient?.Dispose();
        _deskClient = null;
        if (_client is not null)
        {
            _client.Changed -= OnChangedElsewhere;
            _client.FileInvalid -= OnFileInvalid;
            _client.Dispose();
            _client = null;
        }
    }

    private Task ApplyPictureAsync(WallpaperTile tile)
    {
        var values = new Dictionary<string, object> { [BackgroundKeys.Image] = tile.Path ?? string.Empty };

        // A picture chosen under `solid` would be written and never shown. "No Picture" is how
        // this panel says "color only", so leaving solid takes the mode back to a picture one.
        if (tile.Path is not null && Current(BackgroundKeys.Mode) == BackgroundKeys.SolidMode)
            values[BackgroundKeys.Mode] = BackgroundKeys.PictureMode;

        return WriteAsync(values);
    }

    /// <summary>Give the lock screen the desktop's three values as they are now.</summary>
    private Task CopyToLockAsync() =>
        WriteLockedAsync(BackgroundKeys.Desktop.ToDictionary(
            BackgroundKeys.ToLock, key => (object)Value(key), StringComparer.Ordinal));

    /// <summary>
    /// Write desktop keys, and the lock screen's matching ones while it follows -- or, with a desk
    /// chosen, that desk's own background with those values in it.
    /// </summary>
    private Task WriteAsync(Dictionary<string, object> values)
    {
        if (EditedDesk is { } desk)
        {
            WriteDesk(desk, values);
            return Task.CompletedTask;
        }

        if (_alsoLockScreen)
        {
            foreach (var (key, value) in values.ToList())
                values[BackgroundKeys.ToLock(key)] = value;
        }
        return WriteLockedAsync(values);
    }

    private async Task WriteLockedAsync(IReadOnlyDictionary<string, object> values)
    {
        if (_client is null || !IsReady)
            return;

        try
        {
            var result = await _client.SetManyAsync(values).ConfigureAwait(true);
            foreach (var (key, value) in values)
            {
                _touched.Add(key);
                _values[key] = (string)value;
            }

            // Only the desktop's outcome is worth a line. The lock screen reads its file when it
            // starts, so "wlrix-lock is not running" is the normal state rather than news.
            Status = result.Outcomes
                .Where(entry => entry.Key != "wlrix-lock")
                .Any(entry => entry.Value != "applied")
                ? result.Advice ?? string.Empty
                : string.Empty;
        }
        catch (Exception e)
        {
            // The daemon refused the value. Its first line names the key and why.
            Status = e.Message.Split('\n')[0];
        }
    }

    private void WriteDesk(DeskEntry desk, IReadOnlyDictionary<string, object> values)
    {
        if (_deskClient is null || !IsReady)
            return;

        var background = BackgroundKeys.With(BackgroundKeys.Showing(desk.Background, Value), values);
        _deskOpen.TryAdd(desk.Id, desk.Background);
        _deskClient.SetBackground(desk.Id, background);
        Replace(desk with { Background = background });
        Status = string.Empty;
    }

    /// <summary>The compositor's list of desks, after any change to it from anywhere.</summary>
    private void OnDesks(IReadOnlyList<DeskEntry> desks)
    {
        var first = _deskEntries.Count == 0;
        _deskEntries = desks;

        var selectedId = _selectedDesk?.Id;
        _adopting = true;
        try
        {
            Desks.Clear();
            Desks.Add(_allDesks);
            foreach (var desk in desks)
                Desks.Add(new DeskChoice(desk.Id, desk.Name));
        }
        finally
        {
            _adopting = false;
        }

        // On opening, the desk on screen if it has a background of its own -- that is the one the
        // user is looking at -- and otherwise the shared background, which is what is showing.
        // Afterwards whatever was chosen, unless that desk has been deleted.
        DeskChoice? choice = first
            ? desks.FirstOrDefault(desk => desk is { Active: true, Background: not null }) is { } active
                ? Desks.First(entry => entry.Id == active.Id)
                : _allDesks
            : Desks.FirstOrDefault(entry => entry.Id == selectedId) ?? _allDesks;

        _selectedDesk = null;
        SelectedDesk = choice;
        this.RaisePropertyChanged(nameof(HasDesks));
    }

    /// <summary>Put <paramref name="desk"/> in the list in place of the entry with its id.</summary>
    private void Replace(DeskEntry desk)
    {
        _deskEntries = [.. _deskEntries.Select(entry => entry.Id == desk.Id ? desk : entry)];
        RaiseDeskStates();
    }

    private void RaiseDeskStates()
    {
        this.RaisePropertyChanged(nameof(CanFollowLock));
        this.RaisePropertyChanged(nameof(CanUseShared));
        this.RaisePropertyChanged(nameof(DeskNote));
    }

    /// <summary>Somebody else changed a value: another panel, or a hand-edit of the file.</summary>
    private void OnChangedElsewhere(object? sender, SettingsChangedEventArgs e)
    {
        var relevant = e.Values
            .Where(entry => BackgroundKeys.Desktop.Contains(entry.Key) || BackgroundKeys.Lock.Contains(entry.Key))
            .ToList();
        if (relevant.Count == 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            foreach (var (key, value) in relevant)
                _values[key] = value as string ?? string.Empty;
            Adopt(lockFollows: null);
        });
    }

    private void OnFileInvalid(object? sender, SettingsFileInvalidEventArgs e) =>
        Dispatcher.UIThread.Post(() => Status = Strings.FileInvalid(e.Path));

    /// <summary>
    /// Move the controls to <see cref="_values"/> without writing anything.
    /// </summary>
    /// <param name="lockFollows">The lock box's new state, or null to leave it as the user set it.</param>
    private void Adopt(bool? lockFollows)
    {
        _adopting = true;
        try
        {
            if (lockFollows is { } follows)
                AlsoLockScreen = follows;

            if (Color.TryParse(Current(BackgroundKeys.Color), out var color))
                Color = color;

            var mode = Current(BackgroundKeys.Mode);
            if (Modes.FirstOrDefault(choice => choice.Value == mode) is { } choice)
                SelectedMode = choice;

            var image = Current(BackgroundKeys.Image);
            Selected = mode == BackgroundKeys.SolidMode || image.Length == 0 ? _none : TileFor(image);
        }
        finally
        {
            _adopting = false;
        }
    }

    /// <summary>
    /// The tile for <paramref name="path"/>, adding one when the config names a picture that is in
    /// no wallpaper folder. Showing it, rather than highlighting nothing, is what keeps a
    /// hand-edited config visible; and choosing something else is still one click away.
    /// </summary>
    private WallpaperTile TileFor(string path)
    {
        if (Pictures.FirstOrDefault(tile => tile.Path == path) is { } existing)
            return existing;

        var tile = Tile(new Wallpaper(path, IsOwn: false), elsewhere: true);
        Pictures.Insert(1, tile);
        LoadThumbnails([tile]);
        return tile;
    }

    private static WallpaperTile Tile(Wallpaper wallpaper, bool elsewhere = false) => new(wallpaper, elsewhere);

    private string Value(string key) => _values.TryGetValue(key, out var value) ? value : string.Empty;

    /// <summary>
    /// A desktop key's value for whatever the controls are editing: the chosen desk's own
    /// background, the shared one it shows when it has none, or the shared one itself.
    /// </summary>
    private string Current(string key)
    {
        if (EditedDesk is not { Background: { } own })
            return Value(key);
        return key switch
        {
            BackgroundKeys.Image => own.Image,
            BackgroundKeys.Mode => own.Mode,
            BackgroundKeys.Color => own.Color,
            _ => Value(key),
        };
    }

    /// <summary>Decode thumbnails on the thread pool, filling each tile in as it is ready.</summary>
    private static void LoadThumbnails(IEnumerable<WallpaperTile> tiles)
    {
        foreach (var tile in tiles.Where(tile => tile.Path is not null).ToList())
        {
            _ = Task.Run(() =>
            {
                var thumbnail = Wallpapers.Thumbnail(tile.Path!, ThumbnailWidth);
                Dispatcher.UIThread.Post(() => tile.Decoded(thumbnail));
            });
        }
    }
}
