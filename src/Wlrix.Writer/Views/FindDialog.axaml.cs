// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Input;
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
            if ((DataContext as FindViewModel)?.Search() == true)
                Close();
        };
        CancelButton.Click += (_, _) => Close();

        SearchKeys.AttachRecall(Field);
        SearchKeys.AttachMnemonic(this, Key.S, Field);
        Opened += (_, _) => FocusField();
    }

    /// <summary>Puts the cursor in the field with its text selected, ready to be typed over.</summary>
    public void FocusField()
    {
        Field.Focus();
        Field.SelectAll();
    }
}
