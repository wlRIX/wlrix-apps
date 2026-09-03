using Wlrix.Common.Localization;

namespace Wlrix.Settings.Windows.Localization;

/// <summary>
/// Strongly-typed access to the app's localized UI strings (backed by <c>Strings.resx</c> and
/// its culture satellites).
///
/// The static labels in the window come through <c>{loc:Tr}</c>, which reads the same
/// <see cref="Catalog"/>. What is here is what code has to build: the focus-policy labels, which
/// are chosen per value at runtime, and the messages that carry the daemon's words.
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.Settings.Windows.Localization.Strings", typeof(Strings).Assembly);

    /// <summary>
    /// 4Dwm's own name for a focus policy, or <c>null</c> for a value this panel has no words
    /// of its own for.
    /// </summary>
    /// <remarks>
    /// Null rather than the key, unlike <see cref="StringCatalog.Get"/>: the caller falls back
    /// to the label the settings daemon supplies, which is real English for a policy this build
    /// predates, and is a better answer than the resource key.
    /// </remarks>
    public static string? FocusPolicy(string value) => value switch
    {
        "click" => Catalog.Get("FocusClick"),
        "pointer" => Catalog.Get("FocusPointer"),
        _ => null,
    };

    /// <summary>There is no settings service to write through; <paramref name="detail"/> is why.</summary>
    public static string NoSettingsService(string detail) =>
        Catalog.Format("NoSettingsService", detail);

    /// <summary>Somebody is halfway through hand-editing <paramref name="path"/>.</summary>
    public static string FileInvalid(string path) => Catalog.Format("FileInvalid", path);
}
