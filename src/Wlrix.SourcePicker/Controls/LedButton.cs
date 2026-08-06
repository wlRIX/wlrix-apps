using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Wlrix.SourcePicker.Controls;

/// <summary>
/// An IRIX LED button: a raised tile with a lamp and a title above a sunken picture well.
/// </summary>
/// <remarks>
/// <para>
/// The lamp is the state. Lit means chosen, exactly as an LED button reads on a 4Dwm panel, and
/// the whole tile is the hit target -- a picker asks the user to point at a picture of their
/// screen, not at a 14-pixel light.
/// </para>
/// <para>
/// A <see cref="ToggleButton"/> rather than a <see cref="Avalonia.Controls.RadioButton"/> even
/// though the lamp usually behaves like one. Whether the choice is exclusive comes from the
/// portal's <c>multiple</c> flag, which is data, not markup; Avalonia's radio grouping is
/// decided by the control tree. So exclusivity lives in the view model, which is the thing that
/// knows, and this control is a lamp that reflects it.
/// </para>
/// <para>
/// The tile is drawn from the same primitives as the Desks app's desk tiles -- the same lamp in
/// the same sunken well -- so the two read as one desktop. See <c>Themes/LedButton.axaml</c>.
/// </para>
/// </remarks>
public class LedButton : ToggleButton
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<LedButton, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<LedButton, string?>(nameof(Subtitle));

    public static readonly StyledProperty<IImage?> PreviewProperty =
        AvaloniaProperty.Register<LedButton, IImage?>(nameof(Preview));

    /// <summary>What is being offered: a monitor's make and connector, or a window's title.</summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>
    /// A second line under the title, for a window's application. Hidden when empty rather than
    /// left as a blank line, so monitor tiles and window tiles are the same height.
    /// </summary>
    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>
    /// The live thumbnail. Null until the portal has published a frame, which is the normal
    /// state for the first moment the dialog is up.
    /// </summary>
    public IImage? Preview
    {
        get => GetValue(PreviewProperty);
        set => SetValue(PreviewProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(LedButton);
}
