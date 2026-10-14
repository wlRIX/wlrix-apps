using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Wlrix.Avalonia.Dialogs;
using Wlrix.Settings.Login.Localization;
using Wlrix.Settings.Login.ViewModels;

namespace Wlrix.Settings.Login.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private LoginSettingsViewModel? Model => DataContext as LoginSettingsViewModel;

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Model?.Dispose();
    }

    private void OnApply(object? sender, RoutedEventArgs e) => _ = Model?.ApplyAsync();

    private async void OnUseSystemDefaults(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;

        // Asked first: it throws away whatever the login screen was set to, not only the
        // changes in this window, and there is no undo short of setting it all again.
        var answer = await MessageDialog.ShowAsync(this, DialogType.Question, Strings.ConfirmReset,
            buttons: DialogButtons.OkCancel).ConfigureAwait(true);
        if (answer == DialogResult.Ok)
            await model.UseSystemDefaultsAsync().ConfigureAwait(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnCopyKeyboard(object? sender, RoutedEventArgs e) => _ = Model?.CopyKeyboardAsync();

    private void OnCopyScheme(object? sender, RoutedEventArgs e) => _ = Model?.CopySchemeAsync();

    private void OnCopyDisplays(object? sender, RoutedEventArgs e) => Model?.CopyDisplays();

    private void OnCopyAll(object? sender, RoutedEventArgs e) => _ = Model?.CopyAllAsync();

    private void OnAutomaticLayout(object? sender, RoutedEventArgs e) => Model?.AutomaticDisplays();

    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: OutputRowViewModel row })
            Model?.Remove(row);
    }

    private void OnHelp(object? sender, RoutedEventArgs e) =>
        _ = MessageDialog.ShowAsync(this, DialogType.Information, Strings.HelpText,
            title: Strings.HelpTitle, buttons: DialogButtons.Ok);

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
