// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Wlrix.Writer.ViewModels;

namespace Wlrix.Writer.Views;

/// <summary>Search ▸ Find.</summary>
public partial class FindDialog : Window
{
    public FindDialog()
    {
        InitializeComponent();

        FindButton.Click += (_, _) =>
        {
            if (Model?.Search() == true)
                Close();
        };
        CancelButton.Click += (_, _) => Close();

        // On the tunnel: a TextBox takes Up and Down for itself, moving the caret to an end of
        // the line, before a bubbling handler would ever see them.
        Field.AddHandler(KeyDownEvent, OnFieldKeyDown, RoutingStrategies.Tunnel);

        // The label's mnemonic. A Label would do this through its Target, but the theme has no
        // Label, so the key is caught here.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Alt)
            {
                FocusField();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        Opened += (_, _) => FocusField();
    }

    private FindViewModel? Model => DataContext as FindViewModel;

    /// <summary>Puts the cursor in the field with its text selected, ready to be typed over.</summary>
    public void FocusField()
    {
        Field.Focus();
        Field.SelectAll();
    }

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { } model || e.KeyModifiers != KeyModifiers.None)
            return;

        if (e.Key == Key.Up)
            model.RecallOlder();
        else if (e.Key == Key.Down)
            model.RecallNewer();
        else
            return;

        Field.CaretIndex = Field.Text?.Length ?? 0;
        e.Handled = true;
    }
}
