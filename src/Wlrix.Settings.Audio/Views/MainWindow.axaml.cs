// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ReactiveUI;
using Wlrix.Avalonia.Dialogs;
using Wlrix.Settings.Audio.Controls;
using Wlrix.Settings.Audio.Localization;
using Wlrix.Settings.Audio.ViewModels;

namespace Wlrix.Settings.Audio.Views;

public partial class MainWindow : Window
{
    private readonly MenuItem _selectedMenu;
    private readonly MenuItem _viewMenu;
    private readonly MenuItem _defaultInputMenu;
    private readonly MenuItem _defaultOutputMenu;
    private MainWindowViewModel? _model;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _selectedMenu = this.FindControl<MenuItem>("SelectedMenu")!;
        _viewMenu = this.FindControl<MenuItem>("ViewMenu")!;
        _defaultInputMenu = this.FindControl<MenuItem>("DefaultInputMenu")!;
        _defaultOutputMenu = this.FindControl<MenuItem>("DefaultOutputMenu")!;

        // Rebuilt as each opens, so the checks and radio marks are the server's latest. They
        // are also built whenever the devices change, since a menu with no items does not
        // open at all.
        _selectedMenu.SubmenuOpened += (_, _) => FillSelected();
        _viewMenu.SubmenuOpened += (_, _) => FillView();
        _defaultInputMenu.SubmenuOpened += (_, _) => FillDefault(_defaultInputMenu, input: true);
        _defaultOutputMenu.SubmenuOpened += (_, _) => FillDefault(_defaultOutputMenu, input: false);

        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Q, KeyModifiers.Control),
            Command = ReactiveCommand.Create(Close),
        });
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.H, KeyModifiers.Control),
            Command = ReactiveCommand.Create(() =>
            {
                if (_model is not null)
                    _model.ShowQuickHelp = !_model.ShowQuickHelp;
            }),
        });

        Hint.Changed += OnHint;
        FillAll();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_model is not null)
        {
            _model.Columns.CollectionChanged -= OnColumnsChanged;
            _model.PropertyChanged -= OnModelChanged;
        }
        _model = DataContext as MainWindowViewModel;
        if (_model is not null)
        {
            _model.Columns.CollectionChanged += OnColumnsChanged;
            _model.PropertyChanged += OnModelChanged;
        }
        FillAll();
        base.OnDataContextChanged(e);
    }

    private void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e) => FillAll();

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedColumn) && !_selectedMenu.IsSubMenuOpen)
            FillSelected();
    }

    private void FillAll()
    {
        if (!_selectedMenu.IsSubMenuOpen)
            FillSelected();
        if (!_viewMenu.IsSubMenuOpen)
            FillView();
        if (!_defaultInputMenu.IsSubMenuOpen)
            FillDefault(_defaultInputMenu, input: true);
        if (!_defaultOutputMenu.IsSubMenuOpen)
            FillDefault(_defaultOutputMenu, input: false);
    }

    /// <summary>Selected: the outlined device's own menu.</summary>
    private void FillSelected()
    {
        if (_model?.SelectedColumn is { } column)
        {
            DeviceMenu.Fill(_selectedMenu.Items, column, _model, this);
            return;
        }
        _selectedMenu.Items.Clear();
        _selectedMenu.Items.Add(new MenuItem { Header = Strings.NoDeviceSelected, IsEnabled = false });
    }

    /// <summary>
    /// View: whether to follow the defaults, then a toggle for every device, as IRIX's
    /// apanel had.
    /// </summary>
    private void FillView()
    {
        _viewMenu.Items.Clear();
        if (_model is not { } model)
            return;

        _viewMenu.Items.Add(DeviceMenu.Toggle(Strings.MenuViewDefaultInput, model.ShowDefaultInput,
            Strings.HintViewDefaultInput, () => model.ShowDefaultInput = !model.ShowDefaultInput));
        _viewMenu.Items.Add(DeviceMenu.Toggle(Strings.MenuViewDefaultOutput, model.ShowDefaultOutput,
            Strings.HintViewDefaultOutput, () => model.ShowDefaultOutput = !model.ShowDefaultOutput));
        if (model.Columns.Count > 0)
            _viewMenu.Items.Add(new MenuItem { Header = "-" });
        foreach (var column in model.Columns)
        {
            _viewMenu.Items.Add(DeviceMenu.Toggle(column.Title, column.Visible,
                Strings.ViewDeviceHint(column.Title), () => model.SetVisible(column, !column.Visible)));
        }
    }

    /// <summary>Default → Input or Output: every real device of that kind, the default marked.</summary>
    private void FillDefault(MenuItem menu, bool input)
    {
        menu.Items.Clear();
        if (_model is not { } model)
            return;

        var columns = (input ? model.Inputs : model.Outputs).ToList();
        if (columns.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = Strings.NoDevicesOfKind, IsEnabled = false });
            return;
        }
        foreach (var column in columns)
        {
            var choice = new MenuChoice(column.Key, column.Title, column.Device.IsDefault,
                column.IsAvailable, column.MakeDefaultHint);
            menu.Items.Add(DeviceMenu.Radio(choice, input ? "default-input" : "default-output",
                _ => column.MakeDefault()));
        }
    }

    private void OnHint(string? text)
    {
        if (_model is not null)
            _model.Hint = text;
    }

    protected override void OnClosed(EventArgs e)
    {
        Hint.Changed -= OnHint;
        // Stops the meters and the loopbacks Monitor started, and closes the connection.
        _model?.Dispose();
        base.OnClosed(e);
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private void OnHelp(object? sender, RoutedEventArgs e) =>
        _ = MessageDialog.ShowAsync(this, DialogType.Information, Strings.HelpText,
            title: Strings.HelpTitle, buttons: DialogButtons.Ok);

    private void OnAbout(object? sender, RoutedEventArgs e)
    {
        var version = GetType().Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        _ = MessageDialog.ShowAsync(this, DialogType.Information, Strings.About(version),
            title: Strings.AboutTitle, buttons: DialogButtons.Ok);
    }
}
