// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Input;
using Wlrix.Writer.ViewModels;

namespace Wlrix.Writer.Views;

/// <summary>Search ▸ Replace: the Replace/Find dialog.</summary>
public partial class ReplaceDialog : Window
{
    public ReplaceDialog()
    {
        InitializeComponent();

        Act(ReplaceButton, model => model.Replace());
        Act(FindButton, model => model.Find());
        Act(ReplaceAndFindButton, model => model.ReplaceAndFind());
        Act(AllInTab, model => model.ReplaceAll(ReplaceScope.Tab));
        Act(AllInSelection, model => model.ReplaceAll(ReplaceScope.Selection));
        Act(AllInTabs, model => model.ReplaceAll(ReplaceScope.AllTabs));
        CancelButton.Click += (_, _) => Close();

        // Recall is on the string to find only, which is all the hint above the field promises.
        SearchKeys.AttachRecall(Field);
        SearchKeys.AttachMnemonic(this, Key.S, Field);
        SearchKeys.AttachMnemonic(this, Key.W, ReplaceField);
        Opened += (_, _) => FocusField();
    }

    /// <summary>Puts the cursor in the string to find, with its text selected.</summary>
    public void FocusField()
    {
        Field.Focus();
        Field.SelectAll();
    }

    /// <summary>Runs <paramref name="action"/> on a click, and closes if it says to.</summary>
    private void Act(Button button, Func<ReplaceViewModel, bool> action) =>
        button.Click += (_, _) =>
        {
            if (DataContext is ReplaceViewModel model && action(model))
                Close();
        };
}
