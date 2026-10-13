// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Wlrix.Writer.ViewModels;

namespace Wlrix.Writer.Views;

/// <summary>One half of the window: a tab strip and the editor under it.</summary>
public partial class TabGroupView : UserControl
{
    public TabGroupView()
    {
        InitializeComponent();

        // Middle-click closes a tab, as in Wlrix.Files and every browser. On the tunnel and marked
        // handled, so the press never reaches the TabItem: otherwise a background tab would be
        // selected on its way to being closed.
        TabStrip.AddHandler(PointerPressedEvent, OnTabPointerPressed, RoutingStrategies.Tunnel);
    }

    /// <summary>A tab was middle-clicked. The window decides, since closing may need to ask.</summary>
    public event Action<EditorTabViewModel>? CloseTabRequested;

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(TabStrip).Properties.IsMiddleButtonPressed)
            return;
        if ((e.Source as Visual)?.FindAncestorOfType<TabItem>(includeSelf: true)?.DataContext is not EditorTabViewModel tab)
            return;

        e.Handled = true;
        CloseTabRequested?.Invoke(tab);
    }
}
