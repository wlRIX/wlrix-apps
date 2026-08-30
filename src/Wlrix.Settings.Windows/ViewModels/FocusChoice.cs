using ReactiveUI;

namespace Wlrix.Settings.Windows.ViewModels;

/// <summary>
/// One option of the keyboard focus radio group: what the compositor calls it, what a person is
/// shown, and whether it is the one currently picked.
///
/// The value and the label both come from the daemon's schema rather than from here. That is
/// the point of <c>Describe</c> carrying choices at all: the compositor's <c>FocusPolicy</c>
/// rejects anything but the exact strings its serde enum accepts, and with
/// <c>deny_unknown_fields</c> everywhere a wrong one costs the whole config file, not just the
/// setting. A panel that spelled the values itself would be a second copy of that list.
/// </summary>
public sealed class FocusChoice(string value, string label) : ReactiveObject
{
    private bool _selected;

    /// <summary>The string written to <c>[focus] policy</c>.</summary>
    public string Value { get; } = value;

    /// <summary>The English label the daemon supplies for it.</summary>
    public string Label { get; } = label;

    /// <summary>
    /// Whether this is the current policy.
    ///
    /// Two-way bound to a <c>RadioButton</c>, so the group takes care of clearing the other
    /// one: picking B sets B here and Avalonia clears A. The view model watches for a choice
    /// turning <c>true</c> and ignores the clears, which would otherwise commit twice per click.
    /// </summary>
    public bool IsSelected
    {
        get => _selected;
        set => this.RaiseAndSetIfChanged(ref _selected, value);
    }
}
