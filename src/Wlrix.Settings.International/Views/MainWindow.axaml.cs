using Avalonia.Controls;
using Avalonia.Interactivity;
using Wlrix.Settings.International.ViewModels;

namespace Wlrix.Settings.International.Views;

/// <summary>Which dialog <c>--page</c> asked for, opened as soon as the lists have loaded.</summary>
public enum StartPage
{
    None,
    Country,
    Language,
    Keyboard,
    TimeZone,
}

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private InternationalViewModel? Model => DataContext as InternationalViewModel;

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Model?.Dispose();
    }

    /// <summary>Open the dialog <paramref name="page"/> names, as <c>--page</c> asks.</summary>
    public Task OpenAsync(StartPage page) => page switch
    {
        StartPage.Country => CountryAsync(),
        StartPage.Language => LanguageAsync(),
        StartPage.Keyboard => KeyboardAsync(),
        StartPage.TimeZone => TimeZoneAsync(),
        _ => Task.CompletedTask,
    };

    private async void OnCountry(object? sender, RoutedEventArgs e) => await CountryAsync();
    private async void OnLanguage(object? sender, RoutedEventArgs e) => await LanguageAsync();
    private async void OnKeyboard(object? sender, RoutedEventArgs e) => await KeyboardAsync();
    private async void OnTimeZone(object? sender, RoutedEventArgs e) => await TimeZoneAsync();

    private async Task CountryAsync()
    {
        if (Model is { } model && await new CountryDialog(model).ShowDialog<Services.Country?>(this) is { } country)
            model.ChooseCountry(country);
    }

    private async Task LanguageAsync()
    {
        if (Model is { } model && await new LanguageDialog(model).ShowDialog<LanguageChoice?>(this) is { } choice)
            model.ChooseLanguage(choice.Language, choice.Formats);
    }

    private async Task KeyboardAsync()
    {
        if (Model is { } model && await new KeyboardDialog(model).ShowDialog<Services.KeyboardSetting?>(this) is { } keyboard)
            model.ChooseKeyboard(keyboard);
    }

    private async Task TimeZoneAsync()
    {
        if (Model is { } model && await new TimeZoneDialog(model).ShowDialog<Services.TimeZoneEntry?>(this) is { } zone)
            model.ChooseTimeZone(zone);
    }

    /// <summary>Cancel writes nothing: every choice here is staged until a Set button.</summary>
    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnSetSystem(object? sender, RoutedEventArgs e) => _ = Model?.SetSystemAsync();

    private void OnSetPersonal(object? sender, RoutedEventArgs e) => _ = Model?.SetPersonalAsync();
}
