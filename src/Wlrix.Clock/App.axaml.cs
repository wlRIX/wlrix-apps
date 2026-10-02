// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Wlrix.Clock.Views;
using Wlrix.Theme;

namespace Wlrix.Clock;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // The face keeps its own colors, but the tooltip is drawn by the theme and should be in
        // the session's scheme. Not awaited, and nothing fails without a settings service.
        _ = SessionScheme.FollowAsync(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new ClockWindow();

        base.OnFrameworkInitializationCompleted();
    }
}
