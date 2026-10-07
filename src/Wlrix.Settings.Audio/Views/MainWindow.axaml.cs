// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Wlrix.Avalonia.Dialogs;
using Wlrix.Settings.Audio.Controls;
using Wlrix.Settings.Audio.Localization;
using Wlrix.Settings.Audio.ViewModels;

namespace Wlrix.Settings.Audio.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Hint.Changed += OnHint;
    }

    private void OnHint(string? text)
    {
        if (DataContext is MainWindowViewModel model)
            model.Hint = text;
    }

    protected override void OnClosed(EventArgs e)
    {
        Hint.Changed -= OnHint;
        // Stops the meters and the loopbacks Monitor started, and closes the connection.
        (DataContext as MainWindowViewModel)?.Dispose();
        base.OnClosed(e);
    }

    private void OnHelp(object? sender, RoutedEventArgs e) =>
        _ = MessageDialog.ShowAsync(this, DialogType.Information, Strings.HelpText,
            title: Strings.HelpTitle, buttons: DialogButtons.Ok);

    private void OnAbout(object? sender, RoutedEventArgs e)
    {
        var version = GetType().Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        _ = MessageDialog.ShowAsync(this, DialogType.Information, Strings.About(version),
            title: Strings.AboutTitle, buttons: DialogButtons.Ok);
    }
}
