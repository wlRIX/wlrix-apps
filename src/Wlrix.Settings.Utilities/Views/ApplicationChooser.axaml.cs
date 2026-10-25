// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Wlrix.Settings.Utilities.ViewModels;

namespace Wlrix.Settings.Utilities.Views;

/// <summary>Every installed application, to pick one. Closes with the choice, or null.</summary>
public partial class ApplicationChooser : Window
{
    private readonly IReadOnlyList<AppChoice> _all = [];

    public ApplicationChooser() => InitializeComponent();

    public ApplicationChooser(IReadOnlyList<AppChoice> applications) : this()
    {
        _all = applications;
        List.ItemsSource = _all;
        Opened += (_, _) => Filter.Focus();
    }

    private void OnFilterChanged(object? sender, TextChangedEventArgs e)
    {
        var text = Filter.Text?.Trim() ?? string.Empty;
        List.ItemsSource = text.Length == 0
            ? _all
            : _all.Where(app => app.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                                || (app.Entry?.Id.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        Accept.IsEnabled = List.SelectedItem is not null;

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (List.SelectedItem is AppChoice choice)
            Close(choice);
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(List.SelectedItem as AppChoice);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
