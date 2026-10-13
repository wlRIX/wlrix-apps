// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Wlrix.ImageView.ViewModels;

namespace Wlrix.ImageView.Views;

public partial class PropertiesWindow : Window
{
    public PropertiesWindow()
    {
        InitializeComponent();

        // Every press inside the tabs is one undo step, however much it changes before it is
        // released: a thumbwheel drag, a dial drag, a spin button held down. Watched here, on
        // the way down and including presses the controls handle themselves, so no control has
        // to know about undo. The menu bar is outside the tabs, so its popups cannot leave a
        // gesture open.
        Tabs.AddHandler(PointerPressedEvent, (_, _) => Model?.BeginGesture(), RoutingStrategies.Tunnel,
            handledEventsToo: true);
        Tabs.AddHandler(PointerReleasedEvent, (_, _) => Model?.EndGesture(), RoutingStrategies.Tunnel,
            handledEventsToo: true);

        // The footer says what the control under the pointer does: the nearest one that carries
        // a hint in its Tag.
        AddHandler(PointerMovedEvent, OnPointerMovedOver, RoutingStrategies.Tunnel, handledEventsToo: true);

        // A press whose release never arrived, because the window lost the pointer partway
        // through, must not leave every later change uncommitted.
        Deactivated += (_, _) => Model?.EndGesture();
    }

    private MainWindowViewModel? Model => DataContext as MainWindowViewModel;

    private void OnPointerMovedOver(object? sender, PointerEventArgs e)
    {
        if (Model is not { } model)
            return;

        for (var visual = e.Source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Control { Tag: string hint })
            {
                model.Properties.Hint = hint;
                return;
            }
        }
        model.Properties.Hint = string.Empty;
    }
}
