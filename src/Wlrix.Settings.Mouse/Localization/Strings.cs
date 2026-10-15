// SPDX-License-Identifier: GPL-3.0-or-later
using Wlrix.Common.Localization;

namespace Wlrix.Settings.Mouse.Localization;

/// <summary>
/// Strongly-typed access to the app's localized UI strings (backed by <c>Strings.resx</c> and
/// its culture satellites).
///
/// The static labels in the window come through <c>{loc:Tr}</c>, which reads the same
/// <see cref="Catalog"/>. What is here is what code has to build: the Test button's three
/// labels, the slider readouts, and the messages that carry the daemon's words.
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.Settings.Mouse.Localization.Strings", typeof(Strings).Assembly);

    /// <summary>The Test button at rest.</summary>
    public static string ClickHere => Catalog.Get("ClickHere");

    /// <summary>The Test button after a click that was not followed by another in time.</summary>
    public static string SingleClick => Catalog.Get("SingleClick");

    /// <summary>The Test button after two clicks within the double-click time.</summary>
    public static string DoubleClick => Catalog.Get("DoubleClick");

    /// <summary>A duration in milliseconds, as the click-speed slider's readout shows it.</summary>
    public static string Milliseconds(int value) => Catalog.Format("Milliseconds", value);

    /// <summary>A cursor theme the config names that is not installed here.</summary>
    public static string NotInstalled(string theme) => Catalog.Format("NotInstalled", theme);

    /// <summary>There is no settings service to write through; <paramref name="detail"/> is why.</summary>
    public static string NoSettingsService(string detail) =>
        Catalog.Format("NoSettingsService", detail);

    /// <summary>Somebody is halfway through hand-editing <paramref name="path"/>.</summary>
    public static string FileInvalid(string path) => Catalog.Format("FileInvalid", path);
}
