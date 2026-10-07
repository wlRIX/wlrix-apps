// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using Avalonia.Threading;
using ReactiveUI;
using Wlrix.Settings.Audio.Localization;
using Wlrix.Settings.Audio.Models;
using Wlrix.Settings.Audio.Services;

namespace Wlrix.Settings.Audio.ViewModels;

/// <summary>
/// The Audio Panel: a column per device, which of them are shown, the one selected, and the
/// line along the bottom.
///
/// The selected device is what the Selected menu acts on, as in IRIX's apanel, and what a
/// right-click opens the settings of. Columns are matched to devices by key across updates, so
/// selection and a slider being dragged survive the server reporting a change.
///
/// <see cref="Columns"/> is every device; <see cref="ShownColumns"/> is the ones the View menu
/// leaves on screen. A device is shown when its own toggle is on, or when it is the default of
/// its kind and View → Default Input or Default Output is on, so with every device's own
/// toggle off the window still follows the defaults wherever they move.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IAudioFeed _feed;
    private readonly PanelState _state;
    private readonly Action<Action> _post;
    private readonly Func<long>? _clock;
    private AudioSnapshot _snapshot = AudioSnapshot.Empty;
    private DeviceColumnViewModel? _selected;
    private string? _unavailable;
    private string? _hint;
    private bool _hasSnapshot;

    public MainWindowViewModel() : this(new SampleAudioFeed(), new PanelState()) { }

    /// <param name="post">
    /// How a feed event reaches the UI thread. Defaults to the dispatcher; the tests run it in
    /// place.
    /// </param>
    /// <param name="clock">Milliseconds, for the sliders' settling; the tests set it.</param>
    public MainWindowViewModel(IAudioFeed feed, PanelState state, Action<Action>? post = null,
        Func<long>? clock = null)
    {
        _feed = feed;
        _state = state;
        _clock = clock;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
    }

    /// <summary>Every device, inputs first, then outputs, in the order the server lists them.</summary>
    public ObservableCollection<DeviceColumnViewModel> Columns { get; } = [];

    /// <summary>The devices on screen, in the same order.</summary>
    public ObservableCollection<DeviceColumnViewModel> ShownColumns { get; } = [];

    /// <summary>The real inputs, for the Default menu: a switched-off card cannot be a default.</summary>
    public IEnumerable<DeviceColumnViewModel> Inputs =>
        Columns.Where(column => column is { IsInput: true, Device.IsPlaceholder: false });

    /// <summary>The real outputs, for the Default menu.</summary>
    public IEnumerable<DeviceColumnViewModel> Outputs =>
        Columns.Where(column => column is { IsOutput: true, Device.IsPlaceholder: false });

    public DeviceColumnViewModel? SelectedColumn
    {
        get => _selected;
        set
        {
            if (value is null && ShownColumns.Count > 0)
                return;
            if (_selected is not null)
                _selected.IsSelected = false;
            this.RaiseAndSetIfChanged(ref _selected, value);
            if (value is not null)
                value.IsSelected = true;
        }
    }

    /// <summary>View → Default Input.</summary>
    public bool ShowDefaultInput
    {
        get => _state.ShowDefaultInput;
        set
        {
            if (_state.ShowDefaultInput == value)
                return;
            _state.ShowDefaultInput = value;
            this.RaisePropertyChanged();
            RefreshShown();
        }
    }

    /// <summary>View → Default Output.</summary>
    public bool ShowDefaultOutput
    {
        get => _state.ShowDefaultOutput;
        set
        {
            if (_state.ShowDefaultOutput == value)
                return;
            _state.ShowDefaultOutput = value;
            this.RaisePropertyChanged();
            RefreshShown();
        }
    }

    /// <summary>Options → Show Quick Help: whether the hint line is there at all.</summary>
    public bool ShowQuickHelp
    {
        get => _state.ShowQuickHelp;
        set
        {
            if (_state.ShowQuickHelp == value)
                return;
            _state.ShowQuickHelp = value;
            this.RaisePropertyChanged();
        }
    }

    /// <summary>
    /// The line along the bottom: why there is no server when there is none, otherwise what
    /// the pointer is over.
    /// </summary>
    public string StatusText => _unavailable ?? _hint
        ?? (_hasSnapshot && Columns.Count == 0 ? Strings.NoDevices : "");

    /// <summary>What the pointer is over, from the views' <c>Hint.Text</c>.</summary>
    public string? Hint
    {
        get => _hint;
        set
        {
            this.RaiseAndSetIfChanged(ref _hint, value);
            this.RaisePropertyChanged(nameof(StatusText));
        }
    }

    public void Start()
    {
        _feed.SnapshotReceived += OnSnapshot;
        _feed.AvailabilityChanged += OnAvailability;
        _feed.PeaksReceived += OnPeaks;
        _feed.Start();
    }

    /// <summary>Whether a column is on screen, by its own toggle or by following a default.</summary>
    public bool IsShown(DeviceColumnViewModel column) =>
        column.Visible
        || column.Device.IsDefault
        && (column.IsInput ? ShowDefaultInput : ShowDefaultOutput);

    /// <summary>A device's own View menu toggle.</summary>
    public void SetVisible(DeviceColumnViewModel column, bool visible)
    {
        column.Visible = visible;
        RefreshShown();
    }

    /// <summary>The card a column's device belongs to, for its Preferences.</summary>
    public AudioCard? CardOf(DeviceColumnViewModel column) =>
        _snapshot.Cards.FirstOrDefault(card => card.Index == column.Device.CardIndex);

    public void SetCardProfile(AudioCard card, string profile)
    {
        if (profile != card.ActiveProfile)
            _feed.SetCardProfile(card.Index, profile);
    }

    private void OnSnapshot(AudioSnapshot snapshot) => _post(() => Merge(snapshot));

    private void OnAvailability(string? reason) => _post(() =>
    {
        _unavailable = reason;
        this.RaisePropertyChanged(nameof(StatusText));
    });

    private void OnPeaks(string key, float left, float right) => _post(() =>
    {
        foreach (var column in Columns)
        {
            if (column.Key == key && column.Metering)
                column.SetPeaks(left, right);
        }
    });

    internal void Merge(AudioSnapshot snapshot)
    {
        _snapshot = snapshot;
        var wanted = snapshot.Devices
            .OrderBy(device => device.Kind == DeviceKind.Input ? 0 : 1)
            .ToList();

        Sync(Columns, wanted, column => column.Key, device => device.Key,
            device => new DeviceColumnViewModel(device, _feed, _state, _clock));
        foreach (var column in Columns)
        {
            var device = wanted.First(d => d.Key == column.Key);
            column.Update(device, snapshot.IsPipeWire, snapshot.AllowedRates, snapshot.ForcedRate);
        }

        _hasSnapshot = true;
        RefreshShown();
        this.RaisePropertyChanged(nameof(StatusText));
    }

    /// <summary>
    /// Bring <see cref="ShownColumns"/> into line with the View settings, moving rather than
    /// recreating, so a column's controls survive another column coming and going; and keep a
    /// shown column selected.
    /// </summary>
    private void RefreshShown()
    {
        var shown = Columns.Where(IsShown).ToList();
        Sync(ShownColumns, shown, column => column, column => column, column => column);

        if (_selected is null || !ShownColumns.Contains(_selected))
        {
            if (_selected is not null)
                _selected.IsSelected = false;
            _selected = null;
            SelectedColumn = ShownColumns.FirstOrDefault(c => c.IsOutput && c.Device.IsDefault)
                ?? ShownColumns.FirstOrDefault(c => c.IsOutput)
                ?? ShownColumns.FirstOrDefault();
        }
    }

    /// <summary>
    /// Make <paramref name="target"/> hold an item for each of <paramref name="wanted"/>, in
    /// order, keeping the items it already has for the same keys.
    /// </summary>
    private static void Sync<TItem, TWanted, TKey>(ObservableCollection<TItem> target,
        IReadOnlyList<TWanted> wanted, Func<TItem, TKey> itemKey, Func<TWanted, TKey> wantedKey,
        Func<TWanted, TItem> create)
        where TKey : notnull
    {
        var keys = wanted.Select(wantedKey).ToHashSet();
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!keys.Contains(itemKey(target[i])))
                target.RemoveAt(i);
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            var key = wantedKey(wanted[i]);
            var existing = -1;
            for (var j = i; j < target.Count; j++)
            {
                if (EqualityComparer<TKey>.Default.Equals(itemKey(target[j]), key))
                {
                    existing = j;
                    break;
                }
            }

            if (existing < 0)
                target.Insert(i, create(wanted[i]));
            else if (existing != i)
                target.Move(existing, i);
        }
    }

    public void Dispose()
    {
        _feed.SnapshotReceived -= OnSnapshot;
        _feed.AvailabilityChanged -= OnAvailability;
        _feed.PeaksReceived -= OnPeaks;
        _feed.Dispose();
    }
}
