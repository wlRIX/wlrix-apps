using Avalonia.Data.Converters;
using ReactiveUI;
using Wlrix.Packages.Backends.Parsing;

namespace Wlrix.SoftwareManager.ViewModels;

/// <summary>What one of the Status pane's three lights shows.</summary>
public enum PhaseLight
{
    /// <summary>Not started yet: the color of the panel.</summary>
    Off,

    /// <summary>In progress: yellow.</summary>
    InProgress,

    /// <summary>Complete: green.</summary>
    Complete,
}

/// <summary>
/// The Status pane: a line of prose about what just happened, over the overall progress bar and
/// three status lights. The lights are the ones the IRIX original showed — Initialize, Install,
/// Post Install — which map onto resolving the transaction, moving the files, and whatever the
/// package manager runs afterwards (hooks, triggers, an initramfs rebuild).
/// </summary>
public sealed class StatusViewModel : ViewModelBase
{
    /// <summary>For the view: whether a light is yellow.</summary>
    public static FuncValueConverter<PhaseLight, bool> IsInProgress { get; } = new(light => light == PhaseLight.InProgress);

    /// <summary>For the view: whether a light is green.</summary>
    public static FuncValueConverter<PhaseLight, bool> IsComplete { get; } = new(light => light == PhaseLight.Complete);

    private string _message = string.Empty;
    private string _restartNotice = string.Empty;
    private PhaseLight _initialize;
    private PhaseLight _install;
    private PhaseLight _postInstall;
    private double _overall;

    /// <summary>What the user is being told. Empty hides the line rather than leaving a gap.</summary>
    public string Message
    {
        get => _message;
        set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    /// <summary>
    /// A restart still owed. Its own line, since <see cref="Message"/> changes with every listing
    /// and this has to stay up until the machine restarts.
    /// </summary>
    public string RestartNotice
    {
        get => _restartNotice;
        set => this.RaiseAndSetIfChanged(ref _restartNotice, value);
    }

    /// <summary>Resolving the transaction.</summary>
    public PhaseLight Initialize
    {
        get => _initialize;
        private set => this.RaiseAndSetIfChanged(ref _initialize, value);
    }

    /// <summary>Moving the files.</summary>
    public PhaseLight Install
    {
        get => _install;
        private set => this.RaiseAndSetIfChanged(ref _install, value);
    }

    /// <summary>Hooks and triggers.</summary>
    public PhaseLight PostInstall
    {
        get => _postInstall;
        private set => this.RaiseAndSetIfChanged(ref _postInstall, value);
    }

    /// <summary>How far along the whole run is: 0 to 100.</summary>
    public double Overall
    {
        get => _overall;
        set => this.RaiseAndSetIfChanged(ref _overall, value);
    }

    /// <summary>
    /// A software list has been read, so there is something to act on. The original keeps Initialize
    /// green while it waits for Start. Once anything further along is lit, this changes nothing.
    /// </summary>
    public void ListReady()
    {
        if (Initialize == PhaseLight.Off)
            Initialize = PhaseLight.Complete;
    }

    /// <summary>A run is starting: only Initialize is lit, in yellow.</summary>
    public void Begin()
    {
        Overall = 0;
        Initialize = PhaseLight.InProgress;
        Install = PhaseLight.Off;
        PostInstall = PhaseLight.Off;
    }

    /// <summary>
    /// Moves the lights to <paramref name="phase"/>. The phases before it are complete, whether or
    /// not their own last line was seen. A line from an earlier phase changes nothing: pacman checks
    /// its keyring after downloading, which does not take the lights back to Initialize.
    /// </summary>
    public void EnterPhase(TransactionPhase phase)
    {
        if (phase == TransactionPhase.Install && Install == PhaseLight.Off && PostInstall == PhaseLight.Off)
        {
            Initialize = PhaseLight.Complete;
            Install = PhaseLight.InProgress;
        }
        else if (phase == TransactionPhase.PostInstall && PostInstall == PhaseLight.Off)
        {
            Initialize = PhaseLight.Complete;
            Install = PhaseLight.Complete;
            PostInstall = PhaseLight.InProgress;
        }
    }

    /// <summary>Everything worked: all three are green.</summary>
    public void Succeed()
    {
        Initialize = PhaseLight.Complete;
        Install = PhaseLight.Complete;
        PostInstall = PhaseLight.Complete;
    }

    /// <summary>The run ended early: the phase it was in did not complete, so its light goes out.</summary>
    public void Abandon()
    {
        Initialize = Finished(Initialize);
        Install = Finished(Install);
        PostInstall = Finished(PostInstall);
    }

    private static PhaseLight Finished(PhaseLight light) =>
        light == PhaseLight.Complete ? PhaseLight.Complete : PhaseLight.Off;
}
