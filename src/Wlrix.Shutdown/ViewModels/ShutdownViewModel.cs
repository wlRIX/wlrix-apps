using Microsoft.Extensions.Logging;
using ReactiveUI;
using Wlrix.Shutdown.Localization;
using Wlrix.Shutdown.Services;
using ZLogger;

namespace Wlrix.Shutdown.ViewModels;

/// <summary>
/// Backs the Shut Down System window.
///
/// The window is a single question with one answer, so there is no committing and nothing to
/// reset: the checkboxes decide which of logind's verbs OK calls, and until OK is pressed this
/// model has done nothing to the machine. That is deliberate — the Toolchest hands the choice
/// to a dialog precisely so a mis-clicked menu item cannot power the box off, and a model that
/// acted on a checkbox would put that back.
///
/// <see cref="InitializeAsync"/> is what asks logind whether any of it is possible. Until it
/// answers, both actions are unavailable and the firmware row is hidden — a window that has not
/// yet heard from the bus should not offer an option it may have to take away.
/// </summary>
public sealed class ShutdownViewModel : ViewModelBase
{
    private readonly IPowerService _power;
    private readonly ILogger<ShutdownViewModel> _logger;

    private PowerCapabilities _capabilities = PowerCapabilities.None;
    private bool _restart;
    private bool _rebootToFirmwareSetup;
    private bool _busy;

    public ShutdownViewModel(IPowerService power, ILogger<ShutdownViewModel> logger, bool restart)
    {
        _power = power;
        _logger = logger;
        _restart = restart;
    }

    /// <summary>Design-time constructor: no bus, so nothing is available and nothing can be done.</summary>
    public ShutdownViewModel()
    {
        _power = null!;
        _logger = null!;
    }

    /// <summary>Raised (UI thread) with a user-facing reason why the machine is still running.</summary>
    public event Action<string>? ShowError;

    /// <summary>The window title, which names this machine the way IRIX's did.</summary>
    public string Title => Strings.WindowTitle(Environment.MachineName);

    /// <summary>
    /// Whether OK restarts rather than powers off.
    ///
    /// Clearing it clears <see cref="RebootToFirmwareSetup"/> too: that option only qualifies a
    /// restart, and leaving it checked under a hidden row would mean a later re-check of this
    /// box silently brought back a choice the user cannot see they made.
    /// </summary>
    public bool Restart
    {
        get => _restart;
        set
        {
            if (_restart == value)
                return;

            this.RaiseAndSetIfChanged(ref _restart, value);
            if (!value)
                RebootToFirmwareSetup = false;
            RaiseFirmwareSetupChanged();
        }
    }

    /// <summary>Whether a restart should stop at the machine's firmware setup screen.</summary>
    public bool RebootToFirmwareSetup
    {
        get => _rebootToFirmwareSetup;
        set => this.RaiseAndSetIfChanged(ref _rebootToFirmwareSetup, value);
    }

    /// <summary>
    /// Whether the firmware-setup row applies: only under a checked Restart, and only on a
    /// machine logind says can do it at all.
    /// </summary>
    public bool IsFirmwareSetupVisible => _restart && _capabilities.CanFirmwareSetup;

    /// <summary>
    /// The firmware row's opacity: 1 when it applies, 0 when it does not.
    ///
    /// The window hides that row by fading it rather than by taking it out of the layout, and
    /// the reason is the column beside it. Both labels share one auto-sized column, and the
    /// firmware label is the longer of the pair in every language this ships in, so a row that
    /// left the layout took the column's width with it and slid the Restart checkbox sideways
    /// under the pointer that had just clicked it. Faded, the row keeps its space and nothing
    /// moves. <see cref="IsFirmwareSetupVisible"/> drives the row's <c>IsEnabled</c> alongside
    /// this, so an invisible checkbox is neither clickable nor tab-reachable.
    /// </summary>
    public double FirmwareSetupOpacity => IsFirmwareSetupVisible ? 1 : 0;

    /// <summary>
    /// Whether the firmware row can be clicked: when it applies, and while the window is still
    /// asking. The <see cref="Busy"/> half is what the Restart checkbox gets straight from
    /// <c>!Busy</c>; this row needs both conditions in one place, because an invisible row must
    /// stay unclickable whatever the button state is.
    /// </summary>
    public bool IsFirmwareSetupEnabled => IsFirmwareSetupVisible && !_busy;

    /// <summary>
    /// True from OK until the machine goes down, which disables the buttons.
    ///
    /// It normally never goes back to false: on the happy path logind starts stopping the
    /// session while this window is still up, and the process is killed mid-property. It exists
    /// for the unhappy path, where the call comes back with a reason and the window has to be
    /// usable again.
    /// </summary>
    public bool Busy
    {
        get => _busy;
        private set
        {
            this.RaiseAndSetIfChanged(ref _busy, value);
            this.RaisePropertyChanged(nameof(IsFirmwareSetupEnabled));
        }
    }

    /// <summary>Asks logind what this session may do, and shows or hides the firmware row to match.</summary>
    public async Task InitializeAsync()
    {
        _capabilities = await _power.ProbeAsync().ConfigureAwait(true);
        RaiseFirmwareSetupChanged();
    }

    /// <summary>
    /// Acts on the checkboxes. Returns once the machine has been asked to go down, or once the
    /// reason it will not has been reported.
    /// </summary>
    public async Task ConfirmAsync()
    {
        if (Busy)
            return;

        Busy = true;
        try
        {
            // Checked against what the probe said rather than against the buttons: an
            // unavailable action is worth a sentence saying which one and why, and "OK did
            // nothing" is not an answer.
            var refusal = _restart
                ? _capabilities.CanRestart ? null : Strings.RestartUnavailable
                : _capabilities.CanPowerOff ? null : Strings.PowerOffUnavailable;

            var reason = refusal ?? await Act().ConfigureAwait(true);
            if (reason is null)
                return;

            _logger.ZLogWarning($"The machine is still running: {reason}");
            ShowError?.Invoke(reason);
        }
        finally
        {
            Busy = false;
        }
    }

    private void RaiseFirmwareSetupChanged()
    {
        this.RaisePropertyChanged(nameof(IsFirmwareSetupVisible));
        this.RaisePropertyChanged(nameof(FirmwareSetupOpacity));
        this.RaisePropertyChanged(nameof(IsFirmwareSetupEnabled));
    }

    private Task<string?> Act() => _restart
        ? _power.RestartAsync(_rebootToFirmwareSetup)
        : _power.PowerOffAsync();
}
