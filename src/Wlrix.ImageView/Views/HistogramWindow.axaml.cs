// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Wlrix.ImageView.Views;

public partial class HistogramWindow : Window
{
    public HistogramWindow() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
