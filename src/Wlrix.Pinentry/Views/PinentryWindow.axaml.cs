using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Wlrix.Pinentry.ViewModels;

namespace Wlrix.Pinentry.Views;

public partial class PinentryWindow : Window
{
    private bool _answered;

    public PinentryWindow()
    {
        InitializeComponent();
    }

    private PinentryViewModel? Model => DataContext as PinentryViewModel;

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
        Wlrix.Common.SystemSound.Play(Model?.AsksForPin == true ? "dialog-question" : "dialog-information");
        // Straight to the passphrase: the dialog exists to be typed into.
        this.FindControl<TextBox>("PinBox")?.Focus();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        // Closed from the frame: gpg-agent hears Cancel, rather than waiting on a dialog that is gone.
        if (!_answered)
            Model?.Cancel();
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Model?.Ok();

    private void OnNotOk(object? sender, RoutedEventArgs e) => Model?.NotOk();

    private void OnCancel(object? sender, RoutedEventArgs e) => Model?.Cancel();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
