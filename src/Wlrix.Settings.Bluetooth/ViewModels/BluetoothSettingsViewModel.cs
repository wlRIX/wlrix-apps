using System.Collections.ObjectModel;
using Avalonia.Threading;
using ReactiveUI;
using Tmds.DBus.Protocol;
using Wlrix.Settings.Bluetooth.Localization;
using Wlrix.Settings.Bluetooth.Services;
using Wlrix.Settings.Client;

namespace Wlrix.Settings.Bluetooth.ViewModels;

public abstract class ViewModelBase : ReactiveObject;

/// <summary>One row of the Devices list.</summary>
internal sealed record DeviceRow(string Path, string Name, string State, bool Paired, bool Connected)
{
    public static DeviceRow From(DeviceInfo device) => new(
        device.Path,
        device.Name,
        device switch
        {
            { Connected: true, Battery: { } battery } => Strings.Catalog.Format("StateConnectedBattery", battery),
            { Connected: true } => Strings.Catalog.Get("StateConnected"),
            { Paired: true } => Strings.Catalog.Get("StatePaired"),
            _ => Strings.Catalog.Get("StateNew"),
        },
        device.Paired,
        device.Connected);
}

/// <summary>
/// Backs the Bluetooth Settings window: the adapter, and the devices it knows or finds.
/// </summary>
/// <remarks>
/// Everything but the tray icon is BlueZ's, read and changed directly (see
/// <see cref="BluezClient"/>). The state is read again every second while the window is open --
/// one call, however many devices -- and a search for devices runs for as long as the window is
/// open and Bluetooth is on, the way every desktop's Bluetooth panel scans.
/// </remarks>
internal sealed class BluetoothSettingsViewModel : ViewModelBase, IDisposable
{
    /// <summary>The one wlRIX setting here; the rest is BlueZ's.</summary>
    internal const string IndicatorKey = "bluetooth.indicator";

    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(1);

    private readonly DispatcherTimer _timer = new() { Interval = RefreshEvery };
    private BluezClient? _bluez;
    private SettingsClient? _settings;
    private AdapterInfo? _adapter;
    private string? _scanningOn;
    private DeviceRow? _selected;
    private bool _hardwareOff;
    private bool _showInTray = true;
    private bool _isReady;
    private bool _busy;
    private string _status = string.Empty;

    /// <summary>Set while the view is being filled from BlueZ, so the setters do not write back.</summary>
    private bool _loading;

    public BluetoothSettingsViewModel()
    {
        _timer.Tick += async (_, _) => await RefreshAsync().ConfigureAwait(true);
    }

    public ObservableCollection<DeviceRow> Devices { get; } = [];

    public bool IsReady
    {
        get => _isReady;
        private set => this.RaiseAndSetIfChanged(ref _isReady, value);
    }

    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public bool HasAdapter => _adapter is not null;

    public bool Powered
    {
        get => _adapter?.Powered ?? false;
        set
        {
            if (_loading || _adapter is null || value == _adapter.Powered)
                return;
            _ = ChangeAsync(() => _bluez!.SetPoweredAsync(_adapter.Path, value));
        }
    }

    public bool CanSwitch => HasAdapter && !_hardwareOff && !_busy;

    public bool HardwareOff => _hardwareOff;

    public string ShownAs => Strings.Catalog.Format("ShownAs", _adapter?.Name ?? string.Empty);

    public bool Visible
    {
        get => _adapter?.Discoverable ?? false;
        set
        {
            if (_loading || _adapter is null || value == _adapter.Discoverable)
                return;
            _ = ChangeAsync(() => _bluez!.SetDiscoverableAsync(_adapter.Path, value));
        }
    }

    public bool ShowInTray
    {
        get => _showInTray;
        set
        {
            this.RaiseAndSetIfChanged(ref _showInTray, value);
            if (!_loading && _settings is not null)
                _ = SetIndicatorAsync(value);
        }
    }

    public bool CanSetTrayIcon => _settings is not null;

    public bool Searching => _adapter is { Powered: true, Discovering: true };

    public bool IsOn => _adapter?.Powered ?? false;

    public bool ShowNoDevices => IsOn && Devices.Count == 0;

    public bool ShowTurnedOff => HasAdapter && !IsOn;

    public DeviceRow? Selected
    {
        get => _selected;
        set
        {
            this.RaiseAndSetIfChanged(ref _selected, value);
            RaiseActions();
        }
    }

    public bool CanPair => _selected is { Paired: false } && IsOn && !_busy;

    public bool CanConnect => _selected is { Paired: true, Connected: false } && IsOn && !_busy;

    public bool CanDisconnect => _selected is { Connected: true } && !_busy;

    public bool CanForget => _selected is { Paired: true } && !_busy;

    public async Task InitializeAsync()
    {
        try
        {
            _bluez = await BluezClient.ConnectAsync().ConfigureAwait(true);
            await _bluez.ReadAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or InvalidOperationException)
        {
            Status = Strings.Catalog.Format("NoBluez", ex.Message);
            return;
        }

        // The tray setting is the one thing here that is wlRIX's, and the panel is still useful
        // without it.
        try
        {
            _settings = await SettingsClient.ConnectAsync().ConfigureAwait(true);
            var indicator = await _settings.GetAsync(IndicatorKey).ConfigureAwait(true) as string;
            _loading = true;
            ShowInTray = indicator != "hidden";
            _loading = false;
        }
        catch (Exception ex)
        {
            _loading = false;
            _settings = null;
            Status = Strings.Catalog.Format("NoSettingsService", ex.Message);
        }
        this.RaisePropertyChanged(nameof(CanSetTrayIcon));

        IsReady = true;
        await RefreshAsync().ConfigureAwait(true);
        _timer.Start();
    }

    /// <summary>Read BlueZ again, and keep the search running while Bluetooth is on.</summary>
    private async Task RefreshAsync()
    {
        if (_bluez is null)
            return;
        BluezSnapshot snapshot;
        try
        {
            snapshot = await _bluez.ReadAsync().ConfigureAwait(true);
        }
        catch (DBusExceptionBase ex)
        {
            Status = Strings.Catalog.Format("NoBluez", ex.Message);
            return;
        }

        _loading = true;
        _adapter = snapshot.Adapter;
        _hardwareOff = Rfkill.HardBlocked();
        if (_adapter is null && Status.Length == 0)
            Status = Strings.Catalog.Get("NoAdapter");

        // Paired devices, and found ones with a name: a scan also turns up a crowd of nameless
        // beacons -- watches, tags, a neighbor's television -- that nobody means to pair with.
        var rows = snapshot.Devices
            .Where(device => device.Paired || device.HasName)
            .Select(DeviceRow.From)
            .ToList();
        if (!rows.SequenceEqual(Devices))
        {
            var keep = _selected?.Path;
            Devices.Clear();
            foreach (var row in rows)
                Devices.Add(row);
            Selected = rows.FirstOrDefault(row => row.Path == keep);
        }
        foreach (var name in new[]
                 {
                     nameof(HasAdapter), nameof(Powered), nameof(CanSwitch), nameof(HardwareOff),
                     nameof(ShownAs), nameof(Visible), nameof(Searching), nameof(IsOn),
                     nameof(ShowNoDevices), nameof(ShowTurnedOff),
                 })
            this.RaisePropertyChanged(name);
        RaiseActions();
        _loading = false;

        await KeepSearchingAsync().ConfigureAwait(true);
    }

    /// <summary>Start the search once per adapter while it is on; BlueZ ends it when the adapter
    /// goes off, and when this window's connection closes.</summary>
    private async Task KeepSearchingAsync()
    {
        if (_bluez is null)
            return;
        if (_adapter is not { Powered: true } adapter)
        {
            _scanningOn = null;
            return;
        }
        if (_scanningOn == adapter.Path)
            return;
        _scanningOn = adapter.Path;
        try
        {
            await _bluez.StartDiscoveryAsync(adapter.Path).ConfigureAwait(true);
        }
        catch (DBusErrorReplyException)
        {
            // Already searching -- for another client, say -- which is as good.
        }
    }

    public Task PairAsync() => Act(device => _bluez!.PairAsync(device.Path), "Pairing", "PairFailed");

    public Task ConnectAsync() => Act(device => _bluez!.ConnectAsync(device.Path), "Connecting", "ConnectFailed");

    public Task DisconnectAsync() => Act(device => _bluez!.DisconnectAsync(device.Path), null, "DisconnectFailed");

    public Task ForgetAsync() => Act(device => _bluez!.RemoveAsync(_adapter!.Path, device.Path), null, "ForgetFailed");

    /// <summary>Do one thing to the selected device, saying so while it takes.</summary>
    private async Task Act(Func<DeviceRow, Task> action, string? doing, string failed)
    {
        if (_bluez is null || _selected is not { } device)
            return;
        SetBusy(true);
        if (doing is not null)
            Status = Strings.Catalog.Format(doing, device.Name);
        try
        {
            await action(device).ConfigureAwait(true);
            Status = string.Empty;
        }
        catch (DBusErrorReplyException ex)
        {
            Status = Strings.Catalog.Format(failed, device.Name, ex.ErrorMessage);
        }
        finally
        {
            SetBusy(false);
        }
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task ChangeAsync(Func<Task> change)
    {
        if (_bluez is null)
            return;
        try
        {
            await change().ConfigureAwait(true);
        }
        catch (DBusErrorReplyException ex)
        {
            Status = Strings.Catalog.Format("ChangeFailed", ex.ErrorMessage);
        }
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task SetIndicatorAsync(bool shown)
    {
        try
        {
            await _settings!.SetAsync(IndicatorKey, shown ? "auto" : "hidden").ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = ex.Message.Split('\n')[0];
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        this.RaisePropertyChanged(nameof(CanSwitch));
        RaiseActions();
    }

    private void RaiseActions()
    {
        this.RaisePropertyChanged(nameof(CanPair));
        this.RaisePropertyChanged(nameof(CanConnect));
        this.RaisePropertyChanged(nameof(CanDisconnect));
        this.RaisePropertyChanged(nameof(CanForget));
    }

    public void Dispose()
    {
        _timer.Stop();
        if (_bluez is not null && _scanningOn is { } adapter)
        {
            // Polite rather than needed: BlueZ ends a search whose client has gone.
            try
            {
                _bluez.StopDiscoveryAsync(adapter).Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
            }
        }
        _bluez?.Dispose();
        _settings?.Dispose();
    }
}
