// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Wlrix.Settings.Power.Controls;
using Wlrix.Settings.Power.ViewModels;

namespace Wlrix.Settings.Power.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Hint.Changed += OnHint;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Hint.Changed -= OnHint;
        (DataContext as PowerSettingsViewModel)?.Dispose();
    }

    private void OnHint(string? text) =>
        this.FindControl<TextBlock>("HintLine")!.Text = text ?? string.Empty;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnReset(object? sender, RoutedEventArgs e) =>
        (DataContext as PowerSettingsViewModel)?.Reset();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
