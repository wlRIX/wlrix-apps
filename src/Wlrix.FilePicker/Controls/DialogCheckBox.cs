using Avalonia.Controls;
using Avalonia.Input;

namespace Wlrix.FilePicker.Controls;

/// <summary>
/// A check box that leaves Return for the dialog's default button.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Avalonia.Controls.Button.OnKeyDown"/> clicks on Return and marks the event
/// handled, and <see cref="CheckBox"/> is a button — so a focused check box toggles itself and
/// the dialog's accept button is never told. <c>IsDefault</c> is served by a handler the button
/// adds to the <b>window</b> on the bubble route with <c>handledEventsToo: false</c>, so
/// anything that marks Return handled on the way up silences it.
/// </para>
/// <para>
/// That matters more here than it looks. Every one of these sits beside the accept button and
/// answers a question the user is about to submit, so the toolkit's reading of Return — flip
/// this and stay — changes the answer at the moment somebody meant to send it, and does it
/// quietly. Space still toggles, which is what a check box is for.
/// </para>
/// <para>
/// <b>The combo boxes are deliberately left alone.</b> A closed <see cref="ComboBox"/> also
/// takes Return, but it opens its list — visible, and undone with Escape — rather than silently
/// changing an answer. Matching the toolkit where it behaves reasonably is worth more than
/// making every control here bespoke.
/// </para>
/// <para>
/// <c>wlrix-source-picker</c>'s <c>LedButton</c> carries the same override for the same reason.
/// Two three-line overrides rather than a shared base class, because the two controls have
/// nothing else in common; a third would be the time to think again.
/// </para>
/// </remarks>
public class DialogCheckBox : CheckBox
{
    /// <summary>
    /// Draws as an ordinary check box.
    /// </summary>
    /// <remarks>
    /// Without this the theme is looked up by this type, finds nothing, and the control renders
    /// as empty space with no error and nothing in the log — the standing trap with
    /// <c>Wlrix.Avalonia</c>, which themes a fixed set of types.
    /// </remarks>
    protected override Type StyleKeyOverride => typeof(CheckBox);

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Deliberately not calling base for this one key: the base is what would handle it.
        if (e.Key == Key.Enter)
            return;

        base.OnKeyDown(e);
    }
}
