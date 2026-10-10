// SPDX-License-Identifier: GPL-3.0-or-later

using Wlrix.Common.Localization;

namespace Wlrix.ClipboardHistory.Localization;

/// <summary>
/// Strongly-typed access to the app's localized UI strings (backed by <c>Strings.resx</c> and
/// its culture satellites).
///
/// The static labels in the window come through <c>{loc:Tr}</c>, which reads the same
/// <see cref="Catalog"/>. What is here is what code has to choose between or build.
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.ClipboardHistory.Localization.Strings", typeof(Strings).Assembly);

    public static string ClearConfirm => Catalog.Get("ClearConfirm");
    public static string ClearOk => Catalog.Get("ClearOk");
    public static string Empty => Catalog.Get("Empty");
    public static string EmptyStarred => Catalog.Get("EmptyStarred");
    public static string ErrorTitle => Catalog.Get("ErrorTitle");
    public static string Image => Catalog.Get("Image");
    public static string NoMatches => Catalog.Get("NoMatches");
    public static string NotRunning => Catalog.Get("NotRunning");

    /// <summary>An image entry's caption, with its size in pixels.</summary>
    public static string ImageCaption(int width, int height) => Catalog.Format("ImageCaption", width, height);

    /// <summary>A request the daemon refused; <paramref name="detail"/> is what it said.</summary>
    public static string Failed(string detail) => Catalog.Format("Failed", detail);
}
