using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Wlrix.Avalonia.Dialogs;
using Wlrix.Shutdown.Localization;
using Wlrix.Shutdown.ViewModels;

namespace Wlrix.Shutdown.Views;

public partial class ShutdownWindow : Window
{
    public ShutdownWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is not ShutdownViewModel vm)
            return;

        // The title carries this machine's hostname, which a .resx value cannot interpolate at
        // XAML load, so it is set here rather than in the markup.
        Title = vm.Title;

        vm.ShowError += OnShowError;

        // Shown first, and told what it may offer when logind answers. Asking the system bus is
        // not something to block a window's first paint on.
        _ = vm.InitializeAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (DataContext is ShutdownViewModel vm)
            vm.ShowError -= OnShowError;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// OK acts on the checkboxes. The window is deliberately not closed on the way: if the
    /// machine is going down, closing it changes nothing, and if it is not, the reason has to
    /// appear over a window that is still there.
    /// </summary>
    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShutdownViewModel vm)
            _ = vm.ConfirmAsync();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnShowError(string message) => _ = MessageDialog.ShowAsync(this, DialogType.Error,
        message, buttons: DialogButtons.Ok, title: Strings.ErrorTitle);
}
