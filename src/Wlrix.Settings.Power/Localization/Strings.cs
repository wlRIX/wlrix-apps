// SPDX-License-Identifier: GPL-3.0-or-later
using Wlrix.Common.Localization;

namespace Wlrix.Settings.Power.Localization;

/// <summary>
/// Strongly-typed access to the app's localized UI strings (backed by <c>Strings.resx</c> and
/// its culture satellites). The static labels come through <c>{loc:Tr}</c>; what is here is what
/// code has to build: the lid actions' names and the messages that carry the daemon's words.
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.Settings.Power.Localization.Strings", typeof(Strings).Assembly);

    public static string Suspend => Catalog.Get("Suspend");
    public static string Hibernate => Catalog.Get("Hibernate");
    public static string LockScreen => Catalog.Get("LockScreen");
    public static string TurnScreenOff => Catalog.Get("TurnScreenOff");
    public static string DoNothing => Catalog.Get("DoNothing");

    /// <summary>There is no settings service to write through; <paramref name="detail"/> is why.</summary>
    public static string NoSettingsService(string detail) =>
        Catalog.Format("NoSettingsService", detail);

    /// <summary>Somebody is halfway through hand-editing <paramref name="path"/>.</summary>
    public static string FileInvalid(string path) => Catalog.Format("FileInvalid", path);
}
