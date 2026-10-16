using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ReactiveUI;
using Wlrix.PolkitAgent.ViewModels;

namespace Wlrix.PolkitAgent.Views;

public partial class AuthenticationWindow : Window
{
    private TextBox? _response;
    private bool _closingFromModel;

    public AuthenticationWindow()
    {
        InitializeComponent();
        _response = this.FindControl<TextBox>("ResponseBox");
    }

    private AuthenticationViewModel? Model => DataContext as AuthenticationViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Model is not { } model)
            return;

        // Each new question takes the keyboard, so a retry needs no click before typing again.
        model.WhenAnyValue(m => m.WaitingForAnswer)
            .Subscribe(waiting =>
            {
                if (waiting)
                    _response?.Focus();
            });
        model.Completion.ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            _closingFromModel = true;
            Close();
        }), TaskScheduler.Default);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Wlrix.Common.SystemSound.Play("dialog-question");
        Model?.Start();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        // Closed by the window manager: the same as Cancel, or polkit would wait for ever.
        if (!_closingFromModel)
            Model?.Cancel();
    }

    private void OnAuthenticate(object? sender, RoutedEventArgs e) => Model?.Submit();

    private void OnCancel(object? sender, RoutedEventArgs e) => Model?.Cancel();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
