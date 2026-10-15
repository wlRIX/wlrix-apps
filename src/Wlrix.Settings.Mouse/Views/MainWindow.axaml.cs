// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Wlrix.Settings.Mouse.Controls;
using Wlrix.Settings.Mouse.ViewModels;

namespace Wlrix.Settings.Mouse.Views;

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
        (DataContext as MouseSettingsViewModel)?.Dispose();
    }

    private void OnHint(string? text) =>
        this.FindControl<TextBlock>("HintLine")!.Text = text ?? string.Empty;

    private void OnTest(object? sender, RoutedEventArgs e) =>
        (DataContext as MouseSettingsViewModel)?.TestClicked();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnReset(object? sender, RoutedEventArgs e) =>
        (DataContext as MouseSettingsViewModel)?.Reset();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
