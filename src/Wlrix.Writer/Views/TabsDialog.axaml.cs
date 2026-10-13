// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Wlrix.Writer.Localization;
using Wlrix.Writer.Models;
using Wlrix.Writer.ViewModels;

namespace Wlrix.Writer.Views;

/// <summary>Preferences ▸ Tabs: the active document's tab width and whether Tab inserts spaces.</summary>
public partial class TabsDialog : Window
{
    public TabsDialog()
    {
        InitializeComponent();
        TabWidth.Minimum = TabSettings.MinWidth;
        TabWidth.Maximum = TabSettings.MaxWidth;
        Accept.Click += (_, _) => Close(new TabsDialogResult(
            new TabSettings((int)(TabWidth.Value ?? TabSettings.DefaultWidth), UseSpaces.IsChecked == true),
            MakeDefault.IsChecked == true));
        Reject.Click += (_, _) => Close(null);
    }

    /// <summary>Asks for new settings, starting from <paramref name="current"/>. Null if canceled.</summary>
    public static Task<TabsDialogResult?> ShowAsync(Window owner, TabSettings current)
    {
        var dialog = new TabsDialog { Title = Strings.TabsTitle };
        dialog.TabWidth.Value = current.Width;
        dialog.UseSpaces.IsChecked = current.UseSpaces;
        dialog.Opened += (_, _) => dialog.TabWidth.Focus();
        return dialog.ShowDialog<TabsDialogResult?>(owner);
    }
}
