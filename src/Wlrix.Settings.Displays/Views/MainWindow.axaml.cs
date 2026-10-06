using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Wlrix.Settings.Displays.Controls;
using Wlrix.Settings.Displays.ViewModels;

namespace Wlrix.Settings.Displays.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// The form's row: under the arrangement when there is one, in its place when there is not.
    /// </summary>
    public static readonly FuncValueConverter<bool, int> FormRow = new(hasArrangement => hasArrangement ? 1 : 0);

    public MainWindow()
    {
        InitializeComponent();
        this.FindControl<DisplayArrangement>("Arrangement")!.DisplayDropped += OnDisplayDropped;
    }

    private MainWindowViewModel? Model => DataContext as MainWindowViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Model is { } model)
            model.ConfirmKeep = ConfirmKeepAsync;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Model?.Dispose();
    }

    private Task<bool> ConfirmKeepAsync() =>
        new ConfirmWindow { DataContext = new ConfirmViewModel() }.ShowDialog<bool>(this);

    private void OnDisplayDropped(DisplayViewModel display, int x, int y) => Model?.MoveDisplay(display, x, y);

    private async void OnApply(object? sender, RoutedEventArgs e)
    {
        if (Model is { } model)
            await model.ApplyAsync();
    }

    private void OnReset(object? sender, RoutedEventArgs e) => Model?.Reset();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
