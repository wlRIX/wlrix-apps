using System.Collections.ObjectModel;
using Avalonia.Threading;
using ReactiveUI;
using Tmds.DBus.Protocol;
using Wlrix.Settings.Client;
using Wlrix.Settings.Network.Localization;
using Wlrix.Settings.Network.Services;

namespace Wlrix.Settings.Network.ViewModels;

public abstract class ViewModelBase : ReactiveObject;

/// <summary>One line of the Status list.</summary>
internal sealed record DeviceRow(string Interface, string Kind, string State, string Detail);

/// <summary>
/// Network Settings: what is connected, the radio, the tray icon, and the saved Wi-Fi networks.
/// </summary>
/// <remarks>
/// The panel shows saved networks rather than nearby ones. Joining a network is what the Wi-Fi
/// icon's menu is for, and it is one click away; this is where the networks already joined are
/// looked after.
/// </remarks>
internal sealed class NetworkSettingsViewModel : ViewModelBase, IDisposable
{
    /// <summary>The one wlRIX setting here; the rest is NetworkManager's.</summary>
    internal const string IndicatorKey = "network.indicator";

    /// <summary>How often the Status list and the radio switch are read again.</summary>
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(3);

    private readonly DispatcherTimer _timer = new() { Interval = RefreshEvery };
    private NetworkManagerClient? _nm;
    private SettingsClient? _settings;
    private KnownNetwork? _selected;
    private bool _wirelessEnabled;
    private bool _hardwareEnabled = true;
    private bool _showInTray = true;
    private bool _keyringAvailable;
    private bool _isReady;
    private bool _busy;
    private string _status = string.Empty;

    /// <summary>Set while the view is being filled from NetworkManager, so the setters do not write back.</summary>
    private bool _loading;

    public NetworkSettingsViewModel()
    {
        _timer.Tick += async (_, _) => await RefreshStatusAsync().ConfigureAwait(true);
    }

    public ObservableCollection<DeviceRow> Devices { get; } = [];

    public ObservableCollection<KnownNetwork> Known { get; } = [];

    public bool IsReady
    {
        get => _isReady;
        private set => this.RaiseAndSetIfChanged(ref _isReady, value);
    }

    /// <summary>A change is on its way to NetworkManager; the buttons wait for it.</summary>
    public bool IsBusy
    {
        get => _busy;
        private set
        {
            this.RaiseAndSetIfChanged(ref _busy, value);
            RaiseSelection();
        }
    }

    /// <summary>What became of the last change, when it is worth saying. Empty otherwise.</summary>
    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public bool HasDevices => Devices.Count > 0;

    public bool HasKnown => Known.Count > 0;

    public bool WirelessEnabled
    {
        get => _wirelessEnabled;
        set
        {
            if (_wirelessEnabled == value)
                return;
            this.RaiseAndSetIfChanged(ref _wirelessEnabled, value);
            if (!_loading)
                _ = RunAsync(nm => nm.SetWirelessAsync(value));
        }
    }

    /// <summary>The software switch cannot overrule the hardware one, so it is not offered then.</summary>
    public bool CanSwitchWireless => _hardwareEnabled && IsReady;

    public bool HardwareOff => !_hardwareEnabled;

    public bool ShowInTray
    {
        get => _showInTray;
        set
        {
            if (_showInTray == value)
                return;
            this.RaiseAndSetIfChanged(ref _showInTray, value);
            if (!_loading)
                _ = SetIndicatorAsync(value);
        }
    }

    public bool CanSetTrayIcon => _settings is not null;

    public KnownNetwork? Selected
    {
        get => _selected;
        set
        {
            this.RaiseAndSetIfChanged(ref _selected, value);
            RaiseSelection();
        }
    }

    public bool HasSelection => _selected is not null && !_busy;

    public bool AutoConnect
    {
        get => _selected?.AutoConnect ?? false;
        set
        {
            if (_selected is not { } network || network.AutoConnect == value)
                return;
            _ = EditAsync(network, settings => ConnectionSettings.WithAutoConnect(settings, value));
        }
    }

    public decimal Priority
    {
        get => _selected?.Priority ?? 0;
        set
        {
            if (_selected is not { } network || network.Priority == (int)value)
                return;
            _ = EditAsync(network, settings => ConnectionSettings.WithPriority(settings, (int)value));
        }
    }

    /// <summary>
    /// Moving a network from everyone to "only me" needs a keyring to put the password in; the
    /// other way needs nothing. So the box is enabled when it is checked, or when there is a keyring.
    /// </summary>
    public bool ForEveryone
    {
        get => _selected?.ForEveryone ?? true;
        set
        {
            if (_selected is not { } network || network.ForEveryone == value)
                return;
            if (value)
            {
                _ = EditAsync(network, settings => ConnectionSettings.ForEveryone(settings, network.Security));
            }
            else if (UserName() is { } user)
            {
                _ = EditAsync(network, settings => ConnectionSettings.ForUser(settings, network.Security, user));
            }
        }
    }

    public bool CanChangeOwner => HasSelection && (_selected!.ForEveryone ? _keyringAvailable : true);

    /// <summary>Said when "only me" cannot be had, so the grayed box is not a mystery.</summary>
    public bool ShowNoKeyring => HasSelection && _selected!.ForEveryone && !_keyringAvailable;

    public bool CanChangePassword => HasSelection && _selected!.HasPassword;

    /// <summary>Open the connections, read everything, and start watching.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            _nm = await NetworkManagerClient.ConnectAsync().ConfigureAwait(true);
            await _nm.WatchAsync(() => Dispatcher.UIThread.Post(() => _ = RefreshKnownAsync())).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or InvalidOperationException)
        {
            Status = Strings.Catalog.Format("NoNetworkManager", ex.Message);
            return;
        }

        // The tray setting is the one thing here that is wlRIX's, and the panel is still useful
        // without it -- so a settings daemon that is not answering costs that checkbox only.
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
            // Answering, but not knowing the key, is a service started before this one was
            // installed -- it is still running the old binary. That wants a restart, not a report
            // that nothing is there.
            Status = ex is DBusErrorReplyException { ErrorName: var name } && name.EndsWith(".UnknownKey", StringComparison.Ordinal)
                ? Strings.Catalog.Get("OldSettingsService")
                : Strings.Catalog.Format("NoSettingsService", ex.Message);
        }

        this.RaisePropertyChanged(nameof(CanSetTrayIcon));
        _keyringAvailable = await Keyring.AvailableAsync().ConfigureAwait(true);
        IsReady = true;
        this.RaisePropertyChanged(nameof(CanSwitchWireless));
        await RefreshStatusAsync().ConfigureAwait(true);
        await RefreshKnownAsync().ConfigureAwait(true);
        _timer.Start();
    }

    /// <summary>Read the devices and the radio again.</summary>
    private async Task RefreshStatusAsync()
    {
        if (_nm is null)
            return;
        try
        {
            var devices = await _nm.ReadDevicesAsync().ConfigureAwait(true);
            var rows = devices.Select(Row).ToList();
            if (!rows.SequenceEqual(Devices))
            {
                Devices.Clear();
                foreach (var row in rows)
                    Devices.Add(row);
                this.RaisePropertyChanged(nameof(HasDevices));
            }

            var (enabled, hardware) = await _nm.ReadRadioAsync().ConfigureAwait(true);
            _loading = true;
            WirelessEnabled = enabled;
            _loading = false;
            if (_hardwareEnabled != hardware)
            {
                _hardwareEnabled = hardware;
                this.RaisePropertyChanged(nameof(CanSwitchWireless));
                this.RaisePropertyChanged(nameof(HardwareOff));
            }
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException)
        {
            _loading = false;
            Status = Strings.Catalog.Format("NoNetworkManager", ex.Message);
        }
    }

    /// <summary>Read the saved networks again, keeping the selection on the same network.</summary>
    private async Task RefreshKnownAsync()
    {
        if (_nm is null)
            return;
        try
        {
            var known = await _nm.ReadKnownAsync().ConfigureAwait(true);
            var selected = _selected?.Path;
            Known.Clear();
            foreach (var network in known)
                Known.Add(network);
            Selected = Known.FirstOrDefault(network => network.Path == selected) ?? Known.FirstOrDefault();
            this.RaisePropertyChanged(nameof(HasKnown));
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException)
        {
            Status = Strings.Catalog.Format("NoNetworkManager", ex.Message);
        }
    }

    public Task ConnectAsync() =>
        _selected is { } network ? RunAsync(nm => nm.ActivateAsync(network)) : Task.CompletedTask;

    public async Task ChangePasswordAsync()
    {
        if (_selected is not { } network)
            return;
        string? password;
        try
        {
            password = await PasswordPrompt.AskAsync(network.Ssid, network.Security).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status = Strings.Catalog.Format("NoPrompt", ex.Message);
            return;
        }

        if (password is not null)
            await RunAsync(nm => nm.SetPasswordAsync(network, password)).ConfigureAwait(true);
    }

    /// <summary>Forget the selected network. The window asks first.</summary>
    public Task ForgetAsync() =>
        _selected is { } network ? RunAsync(nm => nm.DeleteAsync(network)) : Task.CompletedTask;

    private Task EditAsync(KnownNetwork network, Func<SettingsMap, SettingsMap> edit) =>
        RunAsync(nm => nm.EditAsync(network, edit));

    /// <summary>
    /// Make one change, say what went wrong if anything did, and read the saved networks again --
    /// on failure too, so a refused change is seen to be undone rather than left looking done.
    /// </summary>
    private async Task RunAsync(Func<NetworkManagerClient, Task> change)
    {
        if (_nm is null || _busy)
            return;
        IsBusy = true;
        try
        {
            await change(_nm).ConfigureAwait(true);
            Status = string.Empty;
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException)
        {
            Status = Strings.Catalog.Format("ChangeFailed", Detail(ex));
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshKnownAsync().ConfigureAwait(true);
        await RefreshStatusAsync().ConfigureAwait(true);
    }

    private async Task SetIndicatorAsync(bool shown)
    {
        if (_settings is null)
            return;
        try
        {
            await _settings.SetAsync(IndicatorKey, shown ? "auto" : "hidden").ConfigureAwait(true);
            Status = string.Empty;
        }
        catch (Exception ex)
        {
            Status = Strings.Catalog.Format("ChangeFailed", ex.Message);
        }
    }

    private void RaiseSelection()
    {
        this.RaisePropertyChanged(nameof(HasSelection));
        this.RaisePropertyChanged(nameof(AutoConnect));
        this.RaisePropertyChanged(nameof(Priority));
        this.RaisePropertyChanged(nameof(ForEveryone));
        this.RaisePropertyChanged(nameof(CanChangeOwner));
        this.RaisePropertyChanged(nameof(ShowNoKeyring));
        this.RaisePropertyChanged(nameof(CanChangePassword));
    }

    /// <summary>A Status line for one device.</summary>
    internal static DeviceRow Row(DeviceStatus device)
    {
        var state = device.State switch
        {
            DeviceStateKind.Unmanaged => Strings.Catalog.Get("StateUnmanaged"),
            DeviceStateKind.Unavailable => Strings.Catalog.Get("StateUnavailable"),
            DeviceStateKind.Disconnected => Strings.Catalog.Get("StateDisconnected"),
            DeviceStateKind.Connecting => Strings.Catalog.Get("StateConnecting"),
            DeviceStateKind.Connected => Strings.Catalog.Get("StateConnected"),
            DeviceStateKind.Disconnecting => Strings.Catalog.Get("StateDisconnecting"),
            _ => Strings.Catalog.Get("StateFailed"),
        };
        var detail = string.Join("  ", new[] { device.Connection, device.Address }.Where(part => !string.IsNullOrEmpty(part)));
        return new DeviceRow(device.Interface,
            Strings.Catalog.Get(device.Wireless ? "KindWifi" : "KindEthernet"), state, detail);
    }

    /// <summary>A refusal's own words: NetworkManager's are better than a category.</summary>
    private static string Detail(Exception ex) => ex switch
    {
        DBusErrorReplyException reply => reply.ErrorMessage is { Length: > 0 } text ? text : reply.ErrorName,
        _ => ex.Message,
    };

    /// <summary>The login name a per-user connection is restricted to.</summary>
    private static string? UserName() => Environment.UserName is { Length: > 0 } name ? name : null;

    public void Dispose()
    {
        _timer.Stop();
        _nm?.Dispose();
        _settings?.Dispose();
    }
}
