// SPDX-License-Identifier: GPL-3.0-or-later

namespace Wlrix.Common;

/// <summary>Shared constants and helpers used across wlRIX apps.</summary>
public static class Branding
{
    public const string Name = "wlRIX";

    /// <summary>Standard scaffold banner so each app reports itself consistently.</summary>
    public static string Banner(string app, string version) =>
        $"{Name} {app} {version} — scaffold; Avalonia UI + Wayland client pending.";
}
