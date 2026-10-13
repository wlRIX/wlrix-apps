// SPDX-License-Identifier: GPL-3.0-or-later
using Wlrix.Common.Localization;

namespace Wlrix.Settings.Sound.Localization;

/// <summary>
/// Strongly-typed access to the app's localized UI strings (backed by <c>Strings.resx</c> and
/// its culture satellites). The static labels come through <c>{loc:Tr}</c>; what is here is what
/// code has to build.
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.Settings.Sound.Localization.Strings", typeof(Strings).Assembly);

    /// <summary>The first entry of the output menu: whatever the default output is.</summary>
    public static string DefaultOutput => Catalog.Get("DefaultOutput");

    /// <summary>The sound server would not change the mute.</summary>
    public static string MuteFailed => Catalog.Get("MuteFailed");

    /// <summary>An output that is the setting but is not plugged in.</summary>
    public static string NotConnected(string name) => Catalog.Format("NotConnected", name);

    /// <summary>A theme that is the setting but is not installed.</summary>
    public static string NotInstalled(string id) => Catalog.Format("NotInstalled", id);

    /// <summary>There is no settings service to write through; <paramref name="detail"/> is why.</summary>
    public static string NoSettingsService(string detail) =>
        Catalog.Format("NoSettingsService", detail);

    /// <summary>Somebody is halfway through hand-editing <paramref name="path"/>.</summary>
    public static string FileInvalid(string path) => Catalog.Format("FileInvalid", path);
}
