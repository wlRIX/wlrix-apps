// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Wlrix.Settings.Audio.ViewModels;

namespace Wlrix.Settings.Audio.Views;

/// <summary>
/// One device's column. Pressing anywhere in it selects it, sliders included, and the
/// right button opens its device menu (<see cref="DeviceMenu"/>).
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

    private Window? Owner => this.FindAncestorOfType<Window>();

    private MainWindowViewModel? Window => Owner?.DataContext as MainWindowViewModel;

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Model is { } model && Window is { } window)
            window.SelectedColumn = model;
    }

    private void FillMenu()
    {
        if (Model is not { } model || Window is not { } window || Owner is not { } owner)
        {
            _menu.Items.Clear();
            return;
        }
        // Right-clicking a device selects it, so the Selected menu and this one agree.
        window.SelectedColumn = model;
        DeviceMenu.Fill(_menu.Items, model, window, owner);
    }
}
