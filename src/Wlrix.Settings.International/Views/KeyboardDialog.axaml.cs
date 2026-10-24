using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Wlrix.Settings.International.Localization;
using Wlrix.Settings.International.Services;
using Wlrix.Settings.International.ViewModels;

namespace Wlrix.Settings.International.Views;

/// <summary>One layout in use, as the list shows it: "Japanese (Kana)".</summary>
public sealed record LayoutRow(LayoutChoice Choice, string Text);

/// <summary>Choose the keyboard. Closes with a <see cref="KeyboardSetting"/>, or null.</summary>
public partial class KeyboardDialog : Window
{
    private readonly InternationalViewModel? _model;
    private readonly ObservableCollection<LayoutRow> _inUse = [];
    private readonly IReadOnlyList<string> _otherOptions = [];

    /// <summary>Set while the variant list is refilled, so refilling it is not taken as a choice.</summary>
    private bool _filling;

    public KeyboardDialog() => InitializeComponent();

    public KeyboardDialog(InternationalViewModel model) : this()
    {
        _model = model;
        var keyboard = model.Keyboard;
        _otherOptions = keyboard.OtherOptions;

        foreach (var choice in keyboard.Layouts)
            _inUse.Add(Row(choice));
        InUse.ItemsSource = _inUse;
        InUse.SelectedIndex = 0;

        Model.ItemsSource = model.Xkb.Models;
        Model.SelectedItem = model.Xkb.Models.FirstOrDefault(item => item.Name == keyboard.Model);

        var none = new XkbItem(string.Empty, Strings.NoSwitchKey);
        var keys = model.Xkb.SwitchKeys.Prepend(none).ToList();
        SwitchKey.ItemsSource = keys;
        SwitchKey.SelectedItem = keys.FirstOrDefault(item => item.Name == keyboard.SwitchKey) ?? none;

        Scope.ItemsSource = ScopeChoices.For(model.Country);
        Scope.SelectedIndex = 0;
        UpdateSwitchKey();
    }

    private LayoutRow Row(LayoutChoice choice)
    {
        var layout = _model?.Xkb.Layout(choice.Layout);
        var name = layout?.Description ?? choice.Layout;
        var variant = layout?.Variants.FirstOrDefault(entry => entry.Name == choice.Variant)?.Description;
        return new LayoutRow(choice, variant is null ? name : variant);
    }

    private void OnScope(object? sender, SelectionChangedEventArgs e)
    {
        if (_model is null)
            return;
        var country = Scope.SelectedItem is ScopeChoice { Country: { } code } ? code : null;
        Available.ItemsSource = country is null
            ? _model.Xkb.Layouts
            : _model.Xkb.Layouts.Where(layout => layout.IsFor(country)).ToList();
    }

    /// <summary>The selected layout's variants, its default first.</summary>
    private void OnInUseSelected(object? sender, SelectionChangedEventArgs e)
    {
        _filling = true;
        try
        {
            if (InUse.SelectedItem is not LayoutRow row || _model?.Xkb.Layout(row.Choice.Layout) is not { } layout)
            {
                Variant.ItemsSource = null;
                return;
            }
            var standard = new XkbVariant(string.Empty, Strings.DefaultVariant, []);
            var variants = layout.Variants.Prepend(standard).ToList();
            Variant.ItemsSource = variants;
            Variant.SelectedItem = variants.FirstOrDefault(variant => variant.Name == row.Choice.Variant) ?? standard;
        }
        finally
        {
            _filling = false;
        }
    }

    private void OnVariant(object? sender, SelectionChangedEventArgs e)
    {
        if (_filling || InUse.SelectedItem is not LayoutRow row || Variant.SelectedItem is not XkbVariant variant)
            return;
        var index = _inUse.IndexOf(row);
        _inUse[index] = Row(row.Choice with { Variant = variant.Name });
        InUse.SelectedIndex = index;
    }

    private void OnAdd(object? sender, RoutedEventArgs e) => Add();

    private void OnAvailableDoubleTapped(object? sender, TappedEventArgs e) => Add();

    private void Add()
    {
        if (Available.SelectedItem is not XkbLayout layout)
            return;
        // A layout with the same variant twice would be a switch key that does nothing.
        if (_inUse.Any(row => row.Choice.Layout == layout.Name && row.Choice.Variant.Length == 0))
            return;
        _inUse.Add(Row(new LayoutChoice(layout.Name, string.Empty)));
        InUse.SelectedIndex = _inUse.Count - 1;
        UpdateSwitchKey();
    }

    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        // The last layout stays: a keyboard with none would type nothing at all.
        if (InUse.SelectedItem is not LayoutRow row || _inUse.Count <= 1)
            return;
        var index = _inUse.IndexOf(row);
        _inUse.RemoveAt(index);
        InUse.SelectedIndex = Math.Min(index, _inUse.Count - 1);
        UpdateSwitchKey();
    }

    private void OnUp(object? sender, RoutedEventArgs e) => Move(-1);

    private void OnDown(object? sender, RoutedEventArgs e) => Move(1);

    /// <summary>The first layout is the one a session starts in, so order matters.</summary>
    private void Move(int by)
    {
        if (InUse.SelectedItem is not LayoutRow row)
            return;
        var index = _inUse.IndexOf(row);
        var target = index + by;
        if (target < 0 || target >= _inUse.Count)
            return;
        _inUse.Move(index, target);
        InUse.SelectedIndex = target;
    }

    /// <summary>A switch key means nothing with one layout, so the menu is only usable with two or more.</summary>
    private void UpdateSwitchKey() => SwitchKey.IsEnabled = _inUse.Count > 1;

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var model = (Model.SelectedItem as XkbItem)?.Name ?? KeyboardSetting.Default.Model;
        var key = (SwitchKey.SelectedItem as XkbItem)?.Name;
        Close(new KeyboardSetting(
            [.. _inUse.Select(row => row.Choice)],
            model,
            string.IsNullOrEmpty(key) ? null : key,
            _otherOptions));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
