// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Wlrix.Settings.Sound.Controls;
using Wlrix.Settings.Sound.ViewModels;

namespace Wlrix.Settings.Sound.Views;

public partial class MainWindow : Window
{
    private bool _activatedOnce;

    public MainWindow()
    {
        InitializeComponent();
        Hint.Changed += OnHint;
        Activated += OnActivated;
    }

    /// <summary>
    /// Coming forward asks the sound server again: a headset plugged in, or a mute set in the
    /// audio panel, while this window was behind should show when it is looked at. Not the first
    /// time, which the model's own start-up already covers.
    /// </summary>
    private void OnActivated(object? sender, EventArgs e)
    {
        if (_activatedOnce && DataContext is SoundSettingsViewModel model)
            _ = model.RefreshSoundServerAsync();
        _activatedOnce = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Hint.Changed -= OnHint;
        (DataContext as SoundSettingsViewModel)?.Dispose();
    }

    private void OnHint(string? text) =>
        this.FindControl<TextBlock>("HintLine")!.Text = text ?? string.Empty;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnReset(object? sender, RoutedEventArgs e) =>
        (DataContext as SoundSettingsViewModel)?.Reset();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
