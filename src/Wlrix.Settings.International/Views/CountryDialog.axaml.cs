using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Wlrix.Settings.International.Services;
using Wlrix.Settings.International.ViewModels;

namespace Wlrix.Settings.International.Views;

/// <summary>Choose a country. Closes with it, or with null.</summary>
public partial class CountryDialog : Window
{
    private readonly IReadOnlyList<Country> _all = [];

    public CountryDialog() => InitializeComponent();

    public CountryDialog(InternationalViewModel model) : this()
    {
        _all = model.Countries;
        Show(string.Empty);
        List.SelectedItem = model.Country;
        if (model.Country is { } country)
            List.ScrollIntoView(country);
        Filter.TextChanged += (_, _) => Show(Filter.Text ?? string.Empty);
    }

    /// <summary>The countries whose name or code contains <paramref name="text"/>.</summary>
    private void Show(string text)
    {
        var selected = List.SelectedItem;
        List.ItemsSource = text.Length == 0
            ? _all
            : _all.Where(country => country.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                                    || country.Code.Equals(text, StringComparison.OrdinalIgnoreCase)).ToList();
        List.SelectedItem = selected;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(List.SelectedItem as Country);

    private void OnDoubleTapped(object? sender, TappedEventArgs e) => Close(List.SelectedItem as Country);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
