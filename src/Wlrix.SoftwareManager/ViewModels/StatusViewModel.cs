using ReactiveUI;

namespace Wlrix.SoftwareManager.ViewModels;

/// <summary>
/// The Status pane: a line of prose about what just happened, over the three-phase progress
/// strip. The phases are the ones the IRIX original showed — Initialize, Install, Post Install
/// — which map onto resolving the transaction, moving the files, and whatever the package
/// manager runs afterwards (hooks, triggers, an initramfs rebuild).
/// </summary>
public sealed class StatusViewModel : ViewModelBase
{
    private string _message = string.Empty;
    private double _initialize;
    private double _install;
    private double _postInstall;

    /// <summary>What the user is being told. Empty hides the line rather than leaving a gap.</summary>
    public string Message
    {
        get => _message;
        set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    /// <summary>Resolving the transaction: 0 to 100.</summary>
    public double Initialize
    {
        get => _initialize;
        set => this.RaiseAndSetIfChanged(ref _initialize, value);
    }

    /// <summary>Moving the files: 0 to 100.</summary>
    public double Install
    {
        get => _install;
        set => this.RaiseAndSetIfChanged(ref _install, value);
    }

    /// <summary>Hooks and triggers: 0 to 100.</summary>
    public double PostInstall
    {
        get => _postInstall;
        set => this.RaiseAndSetIfChanged(ref _postInstall, value);
    }

    /// <summary>Puts all three phases back to empty, between transactions.</summary>
    public void ResetProgress()
    {
        Initialize = 0;
        Install = 0;
        PostInstall = 0;
    }
}
