using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Wlrix.Settings.Keyboard.ViewModels;

namespace Wlrix.Settings.Keyboard.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as KeyboardSettingsViewModel)?.Dispose();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnReset(object? sender, RoutedEventArgs e) =>
        (DataContext as KeyboardSettingsViewModel)?.Reset();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
