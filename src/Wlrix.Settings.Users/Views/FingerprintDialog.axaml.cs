using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Wlrix.Settings.Users.Localization;
using Wlrix.Settings.Users.ViewModels;

namespace Wlrix.Settings.Users.Views;

public abstract class FingerprintDialogBase : DialogShell<object?>;

/// <summary>
/// Fingerprints: claims the reader while open and lets go of it on close, so the lock screen and
/// <c>sudo</c> can have it back.
/// </summary>
public partial class FingerprintDialog : FingerprintDialogBase
{
    private readonly FingerprintModel _model = new();

    public FingerprintDialog() : this(string.Empty)
    {
    }

    public FingerprintDialog(string account)
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = _model;
        this.FindControl<TextBlock>("Heading")!.Text = Strings.Catalog.Format("FingerprintHeading", account);
        Opened += async (_, _) =>
        {
            await _model.OpenAsync();
            await _model.Uses.LoadAsync();
        };
        Closed += async (_, _) => await _model.DisposeAsync();
    }

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        _model.Add();
        // Add has just grayed itself out; Start is what comes next.
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() => this.FindControl<Button>("Start")?.Focus());
    }

    private async void OnStart(object? sender, RoutedEventArgs e) => await _model.StartAsync();

    private async void OnStop(object? sender, RoutedEventArgs e)
    {
        if (_model.Enrolling)
            await _model.StopAsync();
        else
            _model.Choosing = false;
    }

    private async void OnDelete(object? sender, RoutedEventArgs e) => await _model.DeleteAsync();

    private async void OnDeleteAll(object? sender, RoutedEventArgs e) => await _model.DeleteAllAsync();
}
