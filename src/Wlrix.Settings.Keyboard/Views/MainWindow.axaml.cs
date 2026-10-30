using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Wlrix.Settings.Keyboard.ViewModels;
using Wlrix.Common;

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

    /// <summary>Open the International panel at its Keyboard dialog, where the layout is chosen.</summary>
    private void OnLayout(object? sender, RoutedEventArgs e)
    {
        try
        {
            var info = Executables.StartInfoFor("wlrix-settings-international");
            info.UseShellExecute = false;
            info.ArgumentList.Add("--page");
            info.ArgumentList.Add("keyboard");
            using var process = Process.Start(info);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            (DataContext as KeyboardSettingsViewModel)?.ReportLaunchFailure(ex.Message);
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
