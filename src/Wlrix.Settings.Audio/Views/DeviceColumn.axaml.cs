// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Wlrix.Settings.Audio.Controls;
using Wlrix.Settings.Audio.Localization;
using Wlrix.Settings.Audio.ViewModels;

namespace Wlrix.Settings.Audio.Views;

/// <summary>
/// One device's column. Pressing anywhere in it selects it, sliders included, and the
/// right button opens its settings menu.
/// </summary>
public partial class DeviceColumn : UserControl
{
    private readonly ContextMenu _menu = new();

    public DeviceColumn()
    {
        InitializeComponent();
        var outline = this.FindControl<Border>("Outline")!;
        // Tunnel and handled-too: a slider takes the press for itself, and selecting the column
        // has to happen anyway.
        outline.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _menu.Opening += (_, _) => FillMenu();
        outline.ContextMenu = _menu;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private DeviceColumnViewModel? Model => DataContext as DeviceColumnViewModel;

    private MainWindowViewModel? Window =>
        this.FindAncestorOfType<Window>()?.DataContext as MainWindowViewModel;

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Model is { } model && Window is { } window)
            window.SelectedColumn = model;
    }

    /// <summary>
    /// The IRIX device menu: the device's name as a title, Sample Rate and the jack as radio
    /// submenus, Group Sliders, Make Default, and Preferences. Built fresh each time it opens,
    /// so it always shows the device as it is now.
    /// </summary>
    private void FillMenu()
    {
        _menu.Items.Clear();
        if (Model is not { } model)
            return;
        if (Window is { } window)
            window.SelectedColumn = model;

        _menu.Items.Add(new MenuItem { Header = model.Title, Classes = { "title" }, IsEnabled = false });

        var rates = new MenuItem { Header = Strings.MenuSampleRate, IsEnabled = model.CanChangeRate };
        Hint.SetText(rates, model.CanChangeRate ? Strings.HintRateGlobal : Strings.HintRateNotPipeWire);
        foreach (var choice in model.RateChoices)
            rates.Items.Add(Radio(choice, "rate", model.ChooseRate));
        _menu.Items.Add(rates);

        var ports = new MenuItem { Header = model.PortMenuLabel, IsEnabled = model.CanChangePort };
        foreach (var choice in model.PortChoices)
            ports.Items.Add(Radio(choice, "port", model.ChoosePort));
        _menu.Items.Add(ports);

        var group = new MenuItem
        {
            Header = Strings.MenuGroupSliders,
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = model.Grouped,
            IsEnabled = model.IsStereo,
        };
        Hint.SetText(group, Strings.HintGroup);
        group.Click += (_, _) => model.Grouped = !model.Grouped;
        _menu.Items.Add(group);

        var makeDefault = new MenuItem { Header = model.MakeDefaultLabel, IsEnabled = model.CanMakeDefault };
        Hint.SetText(makeDefault, model.MakeDefaultHint);
        makeDefault.Click += (_, _) => model.MakeDefault();
        _menu.Items.Add(makeDefault);

        if (model.CanEnable)
        {
            var enable = new MenuItem { Header = Strings.MenuEnableDevice };
            Hint.SetText(enable, Strings.HintEnableDevice);
            enable.Click += (_, _) => model.Enable();
            _menu.Items.Add(enable);
        }

        _menu.Items.Add(new MenuItem { Header = "-" });

        var preferences = new MenuItem { Header = Strings.MenuPreferences };
        Hint.SetText(preferences, Strings.HintPreferences);
        preferences.Click += (_, _) => ShowPreferences(model);
        _menu.Items.Add(preferences);
    }

    private static MenuItem Radio(MenuChoice choice, string group, Action<string> choose)
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

    private void ShowPreferences(DeviceColumnViewModel model)
    {
        if (Window is not { } window || this.FindAncestorOfType<Window>() is not { } owner)
            return;
        var preferences = new PreferencesWindow
        {
            DataContext = new PreferencesViewModel(model, window.CardOf(model), window.SetCardProfile),
        };
        _ = preferences.ShowDialog(owner);
    }
}
