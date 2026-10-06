using ReactiveUI;
using Wlrix.Settings.Displays.Localization;

namespace Wlrix.Settings.Displays.ViewModels;

/// <summary>
/// The "Keep these display settings?" countdown. The window ticks it once a second; reaching
/// zero is the same as choosing Revert, since a person who cannot see the screen cannot click
/// either button.
/// </summary>
public sealed class ConfirmViewModel(int seconds = ConfirmViewModel.DefaultSeconds) : ViewModelBase
{
    public const int DefaultSeconds = 15;

    private int _secondsLeft = seconds;

    public int SecondsLeft
    {
        get => _secondsLeft;
        private set
        {
            this.RaiseAndSetIfChanged(ref _secondsLeft, value);
            this.RaisePropertyChanged(nameof(Countdown));
        }
    }

    /// <summary>"Reverting in 15 seconds."</summary>
    public string Countdown => Strings.RevertingIn(_secondsLeft);

    /// <summary>One second has passed. True when time is up.</summary>
    public bool Tick()
    {
        if (_secondsLeft > 0)
            SecondsLeft = _secondsLeft - 1;
        return _secondsLeft == 0;
    }
}
