// SPDX-License-Identifier: GPL-3.0-or-later

namespace Wlrix.Writer.Models;

/// <summary>How the Tab key behaves in one document.</summary>
/// <param name="Width">Columns per tab stop, which is also how far an emulated tab indents.</param>
/// <param name="UseSpaces">Whether the Tab key inserts spaces instead of a tab character.</param>
/// <remarks>
/// NEdit's defaults: a real tab every eight columns, the width every terminal and every
/// <c>cat</c> agrees on.
/// </remarks>
public sealed record TabSettings(int Width = TabSettings.DefaultWidth, bool UseSpaces = false)
{
    public const int DefaultWidth = 8;
    public const int MinWidth = 1;
    public const int MaxWidth = 16;

    public static TabSettings Default { get; } = new();

    /// <summary>The same settings with the width pulled into range.</summary>
    /// <remarks>A hand-edited state file is the only way to get one out of range.</remarks>
    public TabSettings Normalized() => this with { Width = Math.Clamp(Width, MinWidth, MaxWidth) };
}
