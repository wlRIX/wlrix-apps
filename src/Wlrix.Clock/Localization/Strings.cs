// SPDX-License-Identifier: GPL-3.0-or-later

using Wlrix.Common.Localization;

namespace Wlrix.Clock.Localization;

/// <summary>
/// Strongly-typed access to the app's localized UI strings (backed by <c>Strings.resx</c> and
/// its culture satellites).
///
/// The clock has no titlebar to show a title on, but the window still has one: it is what the
/// Desks overview, a minimized icon and the window list call it.
/// </summary>
public static class Strings
{
    public static StringCatalog Catalog { get; } =
        new("Wlrix.Clock.Localization.Strings", typeof(Strings).Assembly);

    public static string WindowTitle => Catalog.Get("WindowTitle");
}
