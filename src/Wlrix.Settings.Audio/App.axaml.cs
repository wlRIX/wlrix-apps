// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Wlrix.Common.Localization;
using Wlrix.Settings.Audio.Controls;
using Wlrix.Settings.Audio.Localization;
using Wlrix.Settings.Audio.Services;
using Wlrix.Settings.Audio.ViewModels;
using Wlrix.Settings.Audio.Views;
using Wlrix.Theme;

namespace Wlrix.Settings.Audio;

public partial class App : Application
{
    public override void Initialize()
    {
        // Before any XAML is loaded: {loc:Tr} in the window resolves against this, and a
        // catalog set afterwards would leave every static label showing its own key.
        TrExtension.Catalog = Strings.Catalog;
        Hint.Install();

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Draw in the session's color scheme, and follow it when it changes. Not awaited: a
        // window's first paint does not wait on a color.
        _ = SessionScheme.FollowAsync(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // --demo shows the IRIX screenshot's three made-up devices, for working on the
            // panel without touching the real mixer, or without a sound server at all. It
            // remembers nothing.
            var demo = desktop.Args?.Contains("--demo") == true;
            IAudioFeed feed = demo ? new SampleAudioFeed() : new PulseAudioFeed();
            var model = new MainWindowViewModel(feed, demo ? new PanelState() : PanelState.Load());
            desktop.MainWindow = new MainWindow { DataContext = model };

            // Shown first and filled in when the server answers.
            model.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
