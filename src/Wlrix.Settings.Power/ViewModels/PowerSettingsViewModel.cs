// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using ReactiveUI;
using Wlrix.Settings.Client;
using Wlrix.Settings.Power.Localization;
using Wlrix.Settings.Power.Services;

namespace Wlrix.Settings.Power.ViewModels;

/// <summary>
/// Backs the Power Settings window, wlRIX's own: IRIX ran on workstations that never had a lid.
///
/// Everything it shows lives in <c>idle.toml</c> and goes through <c>wlrix-settings-daemon</c>:
/// <c>[lid]</c> for what closing the lid does, and <c>[before_sleep] lock</c> for locking before
/// the machine sleeps. <c>wlrix-idle</c> acts on both, holding logind's lid inhibitor so the
/// person's choice wins over <c>logind.conf</c>. A change is committed as soon as it is made --
/// there is nothing to drag -- and <see cref="Reset"/> puts back what was in force when the
/// window opened.
/// </summary>
public sealed class PowerSettingsViewModel : ViewModelBase, IDisposable
{
    private const string OnBatteryKey = "idle.lid.on_battery";
    private const string OnAcKey = "idle.lid.on_ac";
    private const string DockedKey = "idle.lid.docked";
    private const string LockKey = "idle.before_sleep.lock";

    private SettingsClient? _client;

    // True while loading or applying a change from elsewhere, so those do not write.
    private bool _loading;
    private bool _ready;
    private string _status = string.Empty;

    private LidChoice _onBattery = LidChoice.Find("suspend", "suspend");
    private LidChoice _onAc = LidChoice.Find("suspend", "suspend");
    private LidChoice _docked = LidChoice.Find("nothing", "nothing");
    private bool _lockBeforeSleep = true;

    // What was in force when the window opened, for Reset.
    private (LidChoice Battery, LidChoice Ac, LidChoice Docked, bool Lock) _open;

    /// <summary>The actions the three lists offer.</summary>
    public IReadOnlyList<LidChoice> Choices => LidChoice.All;

    /// <summary>Whether this computer has a lid. The settings are there either way.</summary>
    public bool HasLid { get; } = Lid.Present();

    public LidChoice OnBattery
    {
        get => _onBattery;
        set => Change(ref _onBattery, value, nameof(OnBattery));
    }

    public LidChoice OnAc
    {
        get => _onAc;
        set => Change(ref _onAc, value, nameof(OnAc));
    }

    public LidChoice Docked
    {
        get => _docked;
        set => Change(ref _docked, value, nameof(Docked));
    }

    /// <summary>Lock the session before every suspend, from the lid or anywhere else.</summary>
    public bool LockBeforeSleep
    {
        get => _lockBeforeSleep;
        set
        {
            if (_lockBeforeSleep == value)
                return;
            this.RaiseAndSetIfChanged(ref _lockBeforeSleep, value);
            Commit();
        }
    }

    /// <summary>Whether the settings have loaded and the controls mean anything yet.</summary>
    public bool IsReady
    {
        get => _ready;
        private set => this.RaiseAndSetIfChanged(ref _ready, value);
    }

    /// <summary>What happened to the last change, when it is worth saying. Empty when it applied.</summary>
    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    /// <summary>
    /// Ask the daemon what these settings hold, then listen for changes from anywhere else.
    /// Failing leaves the window drawn but not ready, with the reason in <see cref="Status"/>.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            _client = await SettingsClient.ConnectAsync().ConfigureAwait(true);
            var values = await _client.GetAllAsync("idle").ConfigureAwait(true);

            _loading = true;
            Apply(values);
            _loading = false;
            _open = (_onBattery, _onAc, _docked, _lockBeforeSleep);

            await _client.WatchAsync().ConfigureAwait(true);
            _client.Changed += OnChangedElsewhere;
            _client.FileInvalid += OnFileInvalid;

            IsReady = true;
        }
        catch (Exception e)
        {
            _loading = false;
            Status = Strings.NoSettingsService(e.Message);
        }
    }

    /// <summary>Restore the settings to how they were when the window opened, and apply them.</summary>
    public void Reset()
    {
        if (!IsReady)
            return;
        _loading = true;
        OnBattery = _open.Battery;
        OnAc = _open.Ac;
        Docked = _open.Docked;
        LockBeforeSleep = _open.Lock;
        _loading = false;
        Commit();
    }

    public void Dispose()
    {
        if (_client is null)
            return;
        _client.Changed -= OnChangedElsewhere;
        _client.FileInvalid -= OnFileInvalid;
        _client.Dispose();
        _client = null;
    }

    private void Change(ref LidChoice field, LidChoice? value, string property)
    {
        // A ComboBox briefly selects nothing while its items are replaced; that is not a choice.
        if (value is null || field == value)
            return;
        this.RaiseAndSetIfChanged(ref field, value, property);
        Commit();
    }

    private void Commit()
    {
        if (_loading || !IsReady)
            return;
        _ = CommitAsync();
    }

    private async Task CommitAsync()
    {
        if (_client is null)
            return;
        var values = new Dictionary<string, object>
        {
            [OnBatteryKey] = _onBattery.Value,
            [OnAcKey] = _onAc.Value,
            [DockedKey] = _docked.Value,
            [LockKey] = _lockBeforeSleep,
        };
        try
        {
            var result = await _client.SetManyAsync(values).ConfigureAwait(true);
            Status = result.Advice ?? string.Empty;
        }
        catch (Exception e)
        {
            Status = e.Message.Split('\n')[0];
        }
    }

    private void OnChangedElsewhere(object? sender, SettingsChangedEventArgs e)
    {
        // The echo of our own write, which the daemon sends to everyone including us.
        if (e.Origin == _client?.UniqueName)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            _loading = true;
            Apply(e.Values);
            _loading = false;
        });
    }

    private void OnFileInvalid(object? sender, SettingsFileInvalidEventArgs e) =>
        Dispatcher.UIThread.Post(() => Status = Strings.FileInvalid(e.Path));

    /// <summary>Put whatever of these settings is present into the controls.</summary>
    private void Apply(IReadOnlyDictionary<string, object?> values)
    {
        if (values.TryGetValue(OnBatteryKey, out var battery))
            OnBattery = LidChoice.Find(battery as string, "suspend");
        if (values.TryGetValue(OnAcKey, out var ac))
            OnAc = LidChoice.Find(ac as string, "suspend");
        if (values.TryGetValue(DockedKey, out var docked))
            Docked = LidChoice.Find(docked as string, "nothing");
        if (values.TryGetValue(LockKey, out var locks) && locks is bool lockBeforeSleep)
            LockBeforeSleep = lockBeforeSleep;
    }
}
