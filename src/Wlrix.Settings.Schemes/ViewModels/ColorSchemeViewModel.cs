using Avalonia.Threading;
using ReactiveUI;
using Wlrix.Avalonia;
using Wlrix.Settings.Client;
using Wlrix.Settings.Schemes.Localization;

namespace Wlrix.Settings.Schemes.ViewModels;

/// <summary>
/// Backs the Color Schemes window: IRIX's Color Scheme Browser, which is a list of schemes, a
/// sample image of the scheme under the cursor, and Apply / Reset / Cancel.
///
/// Two sources, and it is worth being clear about which is which. The **list** comes from
/// <see cref="WlrixSchemes"/>, generated into the theme from the same palette JSON the Rust
/// components are built from — so it is exactly what the compositor can be set to, and a new
/// palette file appears here with no edit. The **value** goes through
/// <c>com.wlrix.Settings</c>: one key, <see cref="SessionScheme.Key"/>, which the daemon fans
/// out to <c>compositor.toml</c>, <c>desktop.toml</c>, <c>screenshot.toml</c> and
/// <c>tray.toml</c>, writing each once and signaling each owner once. This app knows none of
/// those filenames.
/// </summary>
public sealed class ColorSchemeViewModel : ViewModelBase, IDisposable
{
    /// <summary>The daemon key that carries the scheme for the whole desktop.</summary>
    private const string Key = Theme.SessionScheme.Key;

    private SettingsClient? _client;

    /// <summary>What was in force when the window opened, for Reset and for Cancel.</summary>
    private string _openScheme = WlrixSchemes.Default.Id;

    /// <summary>Whether anything has been written since the window opened.</summary>
    private bool _applied;

    private WlrixScheme _selected = WlrixSchemes.Default;
    private bool _ready;
    private string _status = string.Empty;

    /// <summary>
    /// Builds the window's model without touching the daemon, so the XAML designer and a
    /// session with no settings service both get something to draw.
    /// <see cref="InitializeAsync"/> is what fills it in.
    /// </summary>
    public ColorSchemeViewModel()
    {
        Schemes = WlrixSchemes.All;
    }

    /// <summary>Every scheme this build ships, in the order they are listed.</summary>
    public IReadOnlyList<WlrixScheme> Schemes { get; }

    /// <summary>
    /// The scheme the list is on, which is what the sample image draws in.
    ///
    /// Selecting does not write. IRIX's browser let you look through the schemes and then press
    /// Apply, and repainting the whole desktop under the cursor as somebody arrows down a list
    /// would be a poor imitation of it.
    /// </summary>
    public WlrixScheme Selected
    {
        get => _selected;
        set
        {
            // A ListBox clears its selection in some rebuilds; keeping the last scheme is better
            // than a sample image of nothing.
            if (value is null || ReferenceEquals(value, _selected))
                return;

            this.RaiseAndSetIfChanged(ref _selected, value);
            this.RaisePropertyChanged(nameof(SelectedDescription));

            // Browsing away puts the description back. Everything Status has to say — a drift
            // warning, an owner that was not running — is about a scheme that is no longer the
            // one on screen, and leaving it there would hide the description for good. A write
            // does not land here: Apply usually sets what is already selected, so its advice
            // survives.
            Status = string.Empty;
        }
    }

    /// <summary>
    /// The line under the list, which is where IRIX put "Default SGI scheme".
    /// </summary>
    public string SelectedDescription =>
        Strings.SchemeDescription(Selected.Name, Selected.IsDark, Selected.Gamma);

    /// <summary>Whether the settings have loaded and Apply means anything yet.</summary>
    public bool IsReady
    {
        get => _ready;
        private set => this.RaiseAndSetIfChanged(ref _ready, value);
    }

    /// <summary>
    /// What happened to the last change, when it is worth saying.
    ///
    /// Empty when everything applied. This is where "the compositor isn't running; this applies
    /// at next login" ends up, instead of the window appearing to have done nothing.
    /// </summary>
    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    /// <summary>
    /// Ask the daemon what the session's scheme is, then start listening for changes from
    /// anywhere else.
    ///
    /// Failing here leaves the window drawn and browsable but not ready: the list and the sample
    /// image are the theme's own and need nothing, so a session with no settings service still
    /// shows what the schemes look like — it just cannot apply one, and says so.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            _client = await SettingsClient.ConnectAsync().ConfigureAwait(true);
            var described = await _client.DescribeKeyAsync(Key).ConfigureAwait(true);
            Adopt(await _client.GetAsync(Key).ConfigureAwait(true));
            _openScheme = Selected.Id;

            await _client.WatchAsync().ConfigureAwait(true);
            _client.Changed += OnChangedElsewhere;
            _client.FileInvalid += OnFileInvalid;

            IsReady = true;
            await ReportDriftAsync(described).ConfigureAwait(true);
        }
        catch (Exception e)
        {
            // Bus activation means "not on the bus" is a broken install rather than an idle
            // session, so say so rather than silently doing nothing.
            Status = Strings.NoSettingsService(e.Message);
        }
    }

    /// <summary>Write the selected scheme, which is one call and four files.</summary>
    public async Task ApplyAsync()
    {
        if (_client is null)
            return;

        try
        {
            var result = await _client.SetAsync(Key, Selected.Id).ConfigureAwait(true);
            _applied = true;
            Status = result.Advice ?? string.Empty;
        }
        catch (Exception e)
        {
            // A daemon too old to know the key is the case worth expecting: it says so by name
            // rather than the button silently doing nothing.
            Status = e.Message.Split('\n')[0];
        }
    }

    /// <summary>Go back to the scheme that was in force when the window opened, and apply it.</summary>
    public async Task ResetAsync()
    {
        Select(_openScheme);
        await ApplyAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// What Cancel does before the window closes: undo anything Apply wrote.
    ///
    /// IRIX's Cancel was "leave things as you found them", not "close". A Cancel that closed
    /// with the desktop repainted would be the one button in the window that lied.
    /// </summary>
    public async Task RevertAsync()
    {
        if (!_applied || _client is null)
            return;
        Select(_openScheme);
        await ApplyAsync().ConfigureAwait(true);
    }

    public void Dispose()
    {
        if (_client is not null)
        {
            _client.Changed -= OnChangedElsewhere;
            _client.FileInvalid -= OnFileInvalid;
            _client.Dispose();
            _client = null;
        }
    }

    /// <summary>
    /// Say so when the four components are not all on the same scheme.
    ///
    /// Only a hand-edit of one file can cause it — the daemon writes all four together — but
    /// somebody who has done that deserves to know why their desktop is two colors, rather than
    /// seeing a list with one scheme highlighted and no explanation. The member keys come from
    /// the daemon's own description of the key, so this app still does not know the filenames.
    /// </summary>
    private async Task ReportDriftAsync(SettingDescription described)
    {
        if (_client is null || !described.IsGroup)
            return;

        var values = new List<string>();
        foreach (var member in described.Members)
            values.Add(await _client.GetAsync(member).ConfigureAwait(true) as string ?? string.Empty);

        if (values.Distinct().Count() > 1)
            Status = Strings.ComponentsDisagree;
    }

    /// <summary>
    /// Somebody else changed the scheme — another panel, or a person editing one of the four
    /// files in an editor with this window open.
    /// </summary>
    private void OnChangedElsewhere(object? sender, SettingsChangedEventArgs e)
    {
        // The echo of our own write is deliberately *not* dropped. There is no debounce timer
        // to fight here, and a Set that landed on a different value than it asked for — an
        // unrecognized id, say — should move the list to what is actually in force.
        if (!e.Values.TryGetValue(Key, out var value))
            return;

        Dispatcher.UIThread.Post(() => Adopt(value));
    }

    private void OnFileInvalid(object? sender, SettingsFileInvalidEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
            Status = Strings.FileInvalid(e.Path));

    private void Adopt(object? value) => Select(value as string);

    /// <summary>
    /// Put the list on a scheme id, leaving it alone if the id names nothing this build ships.
    ///
    /// Leaving it alone rather than clearing the selection is deliberate: an unrecognized id is
    /// a config written by a build with a scheme this one does not have, and an empty list would
    /// invite somebody to pick something and overwrite it. Every component resolves such an id
    /// to the default and carries on, so the desktop is not broken — just not listable.
    /// </summary>
    private void Select(string? id)
    {
        if (WlrixSchemes.ById(id) is { } scheme)
            Selected = scheme;
    }
}
