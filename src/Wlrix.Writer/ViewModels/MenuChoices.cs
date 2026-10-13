// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reactive;
using ReactiveUI;

namespace Wlrix.Writer.ViewModels;

/// <summary>Menu text with its underscores doubled, so a file named <c>a_b</c> is not read as a mnemonic.</summary>
internal static class MenuText
{
    public static string Literal(string text) => text.Replace("_", "__", StringComparison.Ordinal);
}

/// <summary>One file in File ▸ Open Previous.</summary>
public sealed class RecentFileItem(string path, ReactiveCommand<string, Unit> open)
{
    public string Path { get; } = path;

    public string Header { get; } = MenuText.Literal(path);

    public ReactiveCommand<string, Unit> Open { get; } = open;
}

/// <summary>One language in Preferences ▸ Language Mode.</summary>
public sealed class LanguageChoice(string? id, string name, ReactiveCommand<string?, Unit> choose) : ViewModelBase
{
    private bool _isChecked;

    /// <summary>The language id, or null for Plain.</summary>
    public string? Id { get; } = id;

    public string Header { get; } = MenuText.Literal(name);

    public ReactiveCommand<string?, Unit> Choose { get; } = choose;

    public bool IsChecked
    {
        get => _isChecked;
        set => this.RaiseAndSetIfChanged(ref _isChecked, value);
    }
}
