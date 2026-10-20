using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Wlrix.BluetoothPrompt.ViewModels;

namespace Wlrix.BluetoothPrompt.Views;

public partial class PairingWindow : Window
{
    private bool _answered;

    public PairingWindow()
    {
        InitializeComponent();
    }

    private PairingViewModel? Model => DataContext as PairingViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Model?.Completion.ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            _answered = true;
            Close();
        }), TaskScheduler.Default);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Wlrix.Common.SystemSound.Play("dialog-question");
        if (Model?.AsksForEntry == true)
            this.FindControl<TextBox>("EntryBox")?.Focus();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        // Closed from the frame: bluetoothd hears no, rather than waiting on a dialog that is gone.
        if (!_answered)
            Model?.Cancel();
    }

    private void OnAccept(object? sender, RoutedEventArgs e) => Model?.Accept();

    private void OnCancel(object? sender, RoutedEventArgs e) => Model?.Cancel();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
