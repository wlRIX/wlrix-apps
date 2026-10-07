// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using Avalonia.Threading;
using ReactiveUI;
using Wlrix.Settings.Audio.Localization;
using Wlrix.Settings.Audio.Models;
using Wlrix.Settings.Audio.Services;

namespace Wlrix.Settings.Audio.ViewModels;

/// <summary>
/// The Audio Panel: a column per device, the one selected, and the line along the bottom.
///
/// The selected device is what the Selected menu will act on, as in IRIX's apanel, and what
/// a right-click opens the settings of. Columns are matched to devices by key across updates,
/// so selection and a slider being dragged survive the server reporting a change.
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

    /// <summary>Inputs first, then outputs, in the order the server lists them.</summary>
    public ObservableCollection<DeviceColumnViewModel> Columns { get; } = [];

    public DeviceColumnViewModel? SelectedColumn
    {
        get => _selected;
        set
        {
            if (value is null && Columns.Count > 0)
                return;
            if (_selected is not null)
                _selected.IsSelected = false;
            this.RaiseAndSetIfChanged(ref _selected, value);
            if (value is not null)
                value.IsSelected = true;
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

        // Gone devices first, then each wanted device into its place, reusing its column.
        for (var i = Columns.Count - 1; i >= 0; i--)
        {
            if (wanted.All(device => device.Key != Columns[i].Key))
                Columns.RemoveAt(i);
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            var device = wanted[i];
            var existing = -1;
            for (var j = i; j < Columns.Count; j++)
            {
                if (Columns[j].Key == device.Key)
                {
                    existing = j;
                    break;
                }
            }

            if (existing < 0)
            {
                var column = new DeviceColumnViewModel(device, _feed, _state, _clock);
                column.Update(device, snapshot.IsPipeWire, snapshot.AllowedRates, snapshot.ForcedRate);
                Columns.Insert(i, column);
            }
            else
            {
                if (existing != i)
                    Columns.Move(existing, i);
                Columns[i].Update(device, snapshot.IsPipeWire, snapshot.AllowedRates, snapshot.ForcedRate);
            }
        }

        if (_selected is null || !Columns.Contains(_selected))
        {
            _selected = null;
            SelectedColumn = Columns.FirstOrDefault(c => c.IsOutput && c.Device.IsDefault)
                ?? Columns.FirstOrDefault(c => c.IsOutput)
                ?? Columns.FirstOrDefault();
        }

        _hasSnapshot = true;
        this.RaisePropertyChanged(nameof(StatusText));
    }

    public void Dispose()
    {
        _feed.SnapshotReceived -= OnSnapshot;
        _feed.AvailabilityChanged -= OnAvailability;
        _feed.PeaksReceived -= OnPeaks;
        _feed.Dispose();
    }
}
