using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Wlrix.Avalonia.Dialogs;
using Wlrix.Console.ViewModels;

namespace Wlrix.Console.Views;

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

        // Subscribe before starting so a missing file (reported during Start) surfaces the dialog.
        vm.LogFileMissing += OnLogFileMissing;
        vm.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (DataContext is MainWindowViewModel vm)
        {
            vm.LogFileMissing -= OnLogFileMissing;
            vm.Dispose();
        }
    }

    private void OnLogFileMissing() => _ = MessageDialog.ShowAsync(this, DialogType.Error,
        "The wlRIX log file could not be found in the temporary directory.\nThe compositor may not be running.",
        buttons: DialogButtons.Ok);

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
