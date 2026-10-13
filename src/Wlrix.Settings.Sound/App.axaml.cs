// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Wlrix.Common.Localization;
using Wlrix.Settings.Sound.Controls;
using Wlrix.Settings.Sound.Localization;
using Wlrix.Settings.Sound.ViewModels;
using Wlrix.Settings.Sound.Views;
using Wlrix.Theme;

namespace Wlrix.Settings.Sound;

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
            var model = new SoundSettingsViewModel();
            desktop.MainWindow = new MainWindow { DataContext = model };

            // Shown first and filled in when the daemons answer, as the other panels do.
            _ = model.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
