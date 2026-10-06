using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Wlrix.Settings.Displays.ViewModels;

namespace Wlrix.Settings.Displays.Views;

/// <summary>
/// "Keep these display settings?", closing with true for Keep and false for anything else --
/// Revert, the close button, or the countdown running out.
/// </summary>
public partial class ConfirmWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public ConfirmWindow()
    {
        InitializeComponent();
        _timer.Tick += OnTick;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _timer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if ((DataContext as ConfirmViewModel)?.Tick() != false)
            Close(false);
    }

    private void OnKeep(object? sender, RoutedEventArgs e) => Close(true);

    private void OnRevert(object? sender, RoutedEventArgs e) => Close(false);

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
