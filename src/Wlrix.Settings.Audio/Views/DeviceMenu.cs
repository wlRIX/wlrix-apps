// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Wlrix.Settings.Audio.Controls;
using Wlrix.Settings.Audio.Localization;
using Wlrix.Settings.Audio.ViewModels;

namespace Wlrix.Settings.Audio.Views;

/// <summary>
/// The IRIX device menu: the device's name as a title, Sample Rate and the jack as radio
/// submenus, Group Sliders, Make Default, and Preferences. The same items are a column's
/// right-click menu and the menu bar's Selected menu, which is that menu for the outlined
/// device. Built fresh each time either opens, so it always shows the device as it is now.
/// </summary>
internal static class DeviceMenu
{
    public static void Fill(ItemCollection items, DeviceColumnViewModel model,
        MainWindowViewModel window, Window owner)
    {
        items.Clear();
        items.Add(new MenuItem { Header = model.Title, Classes = { "title" }, IsEnabled = false });

        var rates = new MenuItem { Header = Strings.MenuSampleRate, IsEnabled = model.CanChangeRate };
        Hint.SetText(rates, model.CanChangeRate ? Strings.HintRateGlobal : Strings.HintRateNotPipeWire);
        foreach (var choice in model.RateChoices)
            rates.Items.Add(Radio(choice, "rate", model.ChooseRate));
        items.Add(rates);

        var ports = new MenuItem { Header = model.PortMenuLabel, IsEnabled = model.CanChangePort };
        foreach (var choice in model.PortChoices)
            ports.Items.Add(Radio(choice, "port", model.ChoosePort));
        items.Add(ports);

        var group = Toggle(Strings.MenuGroupSliders, model.Grouped, Strings.HintGroup,
            () => model.Grouped = !model.Grouped);
        group.IsEnabled = model.IsStereo;
        items.Add(group);

        items.Add(Item(model.MakeDefaultLabel, model.MakeDefaultHint, model.MakeDefault, model.CanMakeDefault));

        if (model.CanEnable)
            items.Add(Item(Strings.MenuEnableDevice, Strings.HintEnableDevice, model.Enable));

        items.Add(new MenuItem { Header = "-" });
        items.Add(Item(Strings.MenuPreferences, Strings.HintPreferences, () =>
        {
            var preferences = new PreferencesWindow
            {
                DataContext = new PreferencesViewModel(model, window.CardOf(model), window.SetCardProfile),
            };
            _ = preferences.ShowDialog(owner);
        }));
    }

    /// <summary>A plain item that runs <paramref name="action"/>.</summary>
    public static MenuItem Item(string header, string? hint, Action action, bool isEnabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = isEnabled };
        Hint.SetText(item, hint);
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>An item with the square toggle indicator.</summary>
    public static MenuItem Toggle(string header, bool isChecked, string? hint, Action toggle)
    {
        var item = new MenuItem
        {
            Header = header,
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = isChecked,
        };
        Hint.SetText(item, hint);
        item.Click += (_, _) => toggle();
        return item;
    }

    /// <summary>An item with the diamond one-of-many indicator.</summary>
    public static MenuItem Radio(MenuChoice choice, string group, Action<string> choose)
    {
        var item = new MenuItem
        {
            Header = choice.Label,
            ToggleType = MenuItemToggleType.Radio,
            GroupName = group,
            IsChecked = choice.IsChecked,
            IsEnabled = choice.IsEnabled,
        };
        Hint.SetText(item, choice.Hint);
        item.Click += (_, _) => choose(choice.Value);
        return item;
    }
}
