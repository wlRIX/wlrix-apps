// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ReactiveUI;
using Wlrix.Common.Desktop;
using Wlrix.Settings.Utilities.Localization;
using Wlrix.Settings.Utilities.Services;

namespace Wlrix.Settings.Utilities.ViewModels;

/// <summary>An application in a menu or a list: its icon and its name.</summary>
public sealed class AppChoice : ViewModelBase
{
    private Bitmap? _icon;

    private AppChoice(DesktopEntry? entry, string name)
    {
        Entry = entry;
        Name = name;
    }

    /// <summary>The application, or null for the menu's Other… and None items.</summary>
    public DesktopEntry? Entry { get; }

    public string Name { get; }

    public bool IsOther { get; private init; }

    public Bitmap? Icon
    {
        get => _icon;
        private set => this.RaiseAndSetIfChanged(ref _icon, value);
    }

    internal static AppChoice For(DesktopEntry entry, IconCache? icons)
    {
        var choice = new AppChoice(entry, Associations.NameOf(entry));
        if (icons is not null && entry.Icon is { Length: > 0 } icon)
            _ = choice.LoadAsync(icons, [icon]);
        return choice;
    }

    /// <summary>The item that opens the chooser of every application.</summary>
    internal static AppChoice Other() => new(null, Strings.Other) { IsOther = true };

    /// <summary>What a menu shows when nothing is chosen for its role.</summary>
    internal static AppChoice None() => new(null, Strings.None);

    private async Task LoadAsync(IconCache icons, IReadOnlyList<string> names)
    {
        var bitmap = await icons.Get(names).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() => Icon = bitmap);
    }
}
