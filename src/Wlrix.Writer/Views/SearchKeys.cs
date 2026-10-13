// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Wlrix.Writer.ViewModels;

namespace Wlrix.Writer.Views;

/// <summary>The keys the Find and Replace/Find dialogs handle themselves.</summary>
internal static class SearchKeys
{
    /// <summary>Up and Down in <paramref name="field"/> step through the search history.</summary>
    /// <remarks>
    /// On the tunnel: a TextBox takes Up and Down for itself, moving the caret to an end of the
    /// line, before a bubbling handler would ever see them.
    /// </remarks>
    public static void AttachRecall(TextBox field)
    {
        field.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (field.DataContext is not SearchDialogViewModel model || e.KeyModifiers != KeyModifiers.None)
                return;

            if (e.Key == Key.Up)
                model.RecallOlder();
            else if (e.Key == Key.Down)
                model.RecallNewer();
            else
                return;

            field.CaretIndex = field.Text?.Length ?? 0;
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>Alt+<paramref name="key"/> anywhere in <paramref name="window"/> focuses <paramref name="field"/>.</summary>
    /// <remarks>
    /// A field label's mnemonic. A Label would do this through its Target, but WlrixTheme has no
    /// Label template, so the labels are AccessText and the key is caught here.
    /// </remarks>
    public static void AttachMnemonic(Window window, Key key, TextBox field)
    {
        window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key != key || e.KeyModifiers != KeyModifiers.Alt)
                return;
            field.Focus();
            field.SelectAll();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }
}
