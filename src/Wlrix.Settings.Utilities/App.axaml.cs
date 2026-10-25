// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Wlrix.Common.Localization;
using Wlrix.Settings.Utilities.Controls;
using Wlrix.Settings.Utilities.Localization;
using Wlrix.Settings.Utilities.ViewModels;
using Wlrix.Settings.Utilities.Views;
using Wlrix.Theme;

namespace Wlrix.Settings.Utilities;

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
            // `--page types` opens on File Types, for a launcher that means file associations.
            var args = desktop.Args ?? [];
            var page = Array.IndexOf(args, "--page");
            var model = new UtilitiesViewModel
            {
                SelectedTab = page >= 0 && page + 1 < args.Length && args[page + 1] == "types" ? 1 : 0,
            };
            desktop.MainWindow = new MainWindow { DataContext = model };

            // Shown first and filled in once the entries and the type database are read.
            _ = model.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
