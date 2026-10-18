using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Wlrix.NetworkPrompt.ViewModels;

namespace Wlrix.NetworkPrompt.Views;

public partial class PromptWindow : Window
{
    private bool _answered;

    public PromptWindow()
    {
        InitializeComponent();
    }

    private PromptViewModel? Model => DataContext as PromptViewModel;

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
        // Straight to what has to be typed: the name for Join Other Network, else the password.
        var first = Model?.IsOther == true ? "NameBox" : "PasswordBox";
        this.FindControl<TextBox>(first)?.Focus();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        // Closed from the frame: the daemon hears Cancel, rather than waiting on a dialog that is gone.
        if (!_answered)
            Model?.Cancel();
    }

    private void OnConnect(object? sender, RoutedEventArgs e) => Model?.Connect();

    private void OnCancel(object? sender, RoutedEventArgs e) => Model?.Cancel();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
