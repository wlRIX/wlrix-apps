// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Wlrix.Common.Localization;
using Wlrix.Settings.Mouse.Controls;
using Wlrix.Settings.Mouse.Localization;
using Wlrix.Settings.Mouse.ViewModels;
using Wlrix.Settings.Mouse.Views;
using Wlrix.Theme;

namespace Wlrix.Settings.Mouse;

public partial class App : Application
{
    public override void Initialize()
    {
        // Before any XAML is loaded: {loc:Tr} in the window resolves against this.
        TrExtension.Catalog = Strings.Catalog;
        Hint.Install();

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Draw in the session's color scheme, and follow it when it changes.
        _ = SessionScheme.FollowAsync(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // `--touchpad` opens on the Touchpad tab, for a launcher that means the touchpad.
            var model = new MouseSettingsViewModel
            {
                SelectedTab = desktop.Args?.Contains("--touchpad") == true ? 1 : 0,
            };
            desktop.MainWindow = new MainWindow { DataContext = model };

            // Shown first and filled in when the daemon answers, as the other panels do.
            _ = model.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
