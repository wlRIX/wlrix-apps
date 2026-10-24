using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Wlrix.Settings.International.Localization;
using Wlrix.Settings.International.Services;
using Wlrix.Settings.International.ViewModels;

namespace Wlrix.Settings.International.Views;

/// <summary>
/// Choose the time zone, and set the clock. Closes with the zone, or null; the clock changes
/// happen as they are made and are not undone by Cancel.
/// </summary>
public partial class TimeZoneDialog : Window
{
    /// <summary>How the date and time fields are written and read: unambiguous in every language.</summary>
    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "HH:mm:ss";

    private readonly InternationalViewModel? _model;

    public TimeZoneDialog() => InitializeComponent();

    public TimeZoneDialog(InternationalViewModel model) : this()
    {
        _model = model;
        DataContext = model;
        Scope.ItemsSource = ScopeChoices.For(model.Country);
        Scope.SelectedIndex = 0;

        var now = DateTime.Now;
        DateBox.Text = now.ToString(DateFormat, CultureInfo.InvariantCulture);
        TimeBox.Text = now.ToString(TimeFormat, CultureInfo.InvariantCulture);
    }

    private void OnScope(object? sender, SelectionChangedEventArgs e)
    {
        if (_model is null)
            return;
        var country = Scope.SelectedItem is ScopeChoice { Country: { } code } ? code : null;
        var zones = _model.TimeZones
            .Where(zone => country is null || zone.Country == country)
            .OrderBy(zone => zone.Id, StringComparer.Ordinal)
            .ToList();
        if (_model.TimeZone is { } current && !zones.Contains(current))
            zones.Insert(0, current);
        Zones.ItemsSource = zones;
        Zones.SelectedItem = _model.TimeZone;
        if (_model.TimeZone is { } selected)
            Zones.ScrollIntoView(selected);
    }

    private async void OnNtp(object? sender, RoutedEventArgs e)
    {
        // The binding moving the box to what timedated says is not the user asking for anything.
        if (_model is null || NtpBox.IsChecked is not { } wanted || wanted == _model.Ntp)
            return;
        await _model.SetNtpAsync(wanted);
        ClockProblem.Text = _model.Status;
        ClockProblem.IsVisible = _model.Ntp != wanted;
    }

    private async void OnSetClock(object? sender, RoutedEventArgs e)
    {
        if (_model is null)
            return;
        if (!DateTime.TryParseExact($"{DateBox.Text?.Trim()} {TimeBox.Text?.Trim()}", $"{DateFormat} {TimeFormat}",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            ClockProblem.Text = Strings.BadDateTime;
            ClockProblem.IsVisible = true;
            return;
        }
        await _model.SetClockAsync(local);
        ClockProblem.Text = _model.Status;
        ClockProblem.IsVisible = true;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(Zones.SelectedItem as TimeZoneEntry);

    private void OnDoubleTapped(object? sender, TappedEventArgs e) => Close(Zones.SelectedItem as TimeZoneEntry);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
