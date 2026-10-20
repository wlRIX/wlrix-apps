// SPDX-License-Identifier: GPL-3.0-or-later
using Wlrix.Settings.Power.Localization;

namespace Wlrix.Settings.Power.ViewModels;

/// <summary>
/// One thing closing the lid can do: the value <c>idle.toml</c> spells it with, and what the
/// list shows.
/// </summary>
public sealed record LidChoice(string Value, string Label)
{
    /// <summary>Every action <c>wlrix-idle</c>'s <c>LidAction</c> accepts, in the list's order.</summary>
    public static IReadOnlyList<LidChoice> All { get; } =
    [
        new("suspend", Strings.Suspend),
        new("hibernate", Strings.Hibernate),
        new("lock", Strings.LockScreen),
        new("blank", Strings.TurnScreenOff),
        new("nothing", Strings.DoNothing),
    ];

    /// <summary>The choice for <paramref name="value"/>, or <paramref name="fallback"/>'s when it is
    /// not one wlRIX knows.</summary>
    public static LidChoice Find(string? value, string fallback) =>
        All.FirstOrDefault(choice => choice.Value == value) ?? All.First(choice => choice.Value == fallback);
}
