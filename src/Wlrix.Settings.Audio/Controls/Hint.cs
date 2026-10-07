// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Wlrix.Settings.Audio.Controls;

/// <summary>
/// The IRIX status line: a control carrying <c>Hint.Text</c> puts it on the line at the bottom
/// of the window while the pointer is over it, or while it is the highlighted menu item.
/// Leaving a control hands the line back to the nearest enclosing control with a hint of its
/// own, so leaving a slider shows its column's hint again rather than nothing.
/// </summary>
public static class Hint
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Text", typeof(Hint));

    public static string? GetText(Control control) => control.GetValue(TextProperty);
    public static void SetText(Control control, string? value) => control.SetValue(TextProperty, value);

    /// <summary>Raised with the text to show, or null to clear the line.</summary>
    public static event Action<string?>? Changed;

    static Hint()
    {
        InputElement.PointerEnteredEvent.AddClassHandler<Control>((control, _) => Enter(control));
        InputElement.PointerExitedEvent.AddClassHandler<Control>((control, _) => Exit(control));
        // A menu item highlighted with the keyboard says what it does too, as IRIX did.
        MenuItem.IsSelectedProperty.Changed.AddClassHandler<MenuItem>((item, e) =>
        {
            if (e.NewValue is true)
                Enter(item);
            else
                Exit(item);
        });
        // The text can change while the pointer is still over the control.
        TextProperty.Changed.AddClassHandler<Control>((control, _) =>
        {
            if (control.IsPointerOver)
                Enter(control);
        });
    }

    /// <summary>Make sure the class handlers are registered; call once at startup.</summary>
    public static void Install() { }

    private static void Enter(Control control)
    {
        if (GetText(control) is { } text)
            Changed?.Invoke(text);
    }

    private static void Exit(Control control)
    {
        if (GetText(control) is null)
            return;
        for (var parent = control.GetVisualParent(); parent is not null; parent = parent.GetVisualParent())
        {
            if (parent is Control enclosing && GetText(enclosing) is { } text)
            {
                Changed?.Invoke(text);
                return;
            }
        }
        Changed?.Invoke(null);
    }
}
