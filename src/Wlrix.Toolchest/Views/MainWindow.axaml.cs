using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Wlrix.Avalonia.Dialogs;
using Wlrix.Toolchest.Localization;
using Wlrix.Toolchest.ViewModels;

namespace Wlrix.Toolchest.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is not MainWindowViewModel vm)
            return;

        vm.ShowAbout += OnShowAbout;
        vm.LaunchError += OnLaunchError;
        _ = vm.LoadAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (DataContext is MainWindowViewModel vm)
        {
            vm.ShowAbout -= OnShowAbout;
            vm.LaunchError -= OnLaunchError;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnShowAbout(string message) => _ = MessageDialog.ShowAsync(this, DialogType.Information,
        message, buttons: DialogButtons.Ok, title: Strings.AboutToolchest);

    private void OnLaunchError(string message) =>
        _ = MessageDialog.ShowAsync(this, DialogType.Error, message, buttons: DialogButtons.Ok);
}
