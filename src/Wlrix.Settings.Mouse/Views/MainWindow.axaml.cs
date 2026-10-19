// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
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
        Opened += (_, _) => EvenOutPages();
    }

    /// <summary>
    /// Give every tab's page the height of the tallest, so switching tabs does not resize the
    /// window and move the buttons out from under the pointer -- IRIX's tabbed panels kept one
    /// size. A tab control measures only the page showing, so the others are measured here, at
    /// the width the showing one was given.
    /// </summary>
    private void EvenOutPages()
    {
        var tabs = this.FindControl<TabControl>("Pages")!;
        var pages = tabs.Items.OfType<TabItem>().Select(item => item.Content).OfType<Control>().ToList();
        var width = (tabs.SelectedContent as Control)?.Bounds.Width ?? 0;
        if (width <= 0)
            return;
        var tallest = 0.0;
        foreach (var page in pages)
        {
            page.Measure(new Size(width, double.PositiveInfinity));
            tallest = Math.Max(tallest, page.DesiredSize.Height);
        }
        foreach (var page in pages)
            page.MinHeight = tallest;
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
