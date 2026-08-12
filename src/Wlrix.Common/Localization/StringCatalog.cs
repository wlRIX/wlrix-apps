using System.Globalization;
using System.Reflection;
using System.Resources;

namespace Wlrix.Common.Localization;

/// <summary>
/// Localized UI strings for one application, backed by a <c>.resx</c> and its culture
/// satellites. Values resolve against <see cref="CultureInfo.CurrentUICulture"/> — which .NET
/// seeds from the system locale — so no designer/codegen file is needed.
///
/// An app owns one of these, usually behind a static <c>Strings</c> class of named properties,
/// and hands the same instance to <see cref="TrExtension.Catalog"/> so its XAML can reach the
/// same resources.
/// </summary>
/// <param name="baseName">
/// The root name of the resource, without the culture or the <c>.resources</c> extension —
/// e.g. <c>Wlrix.SoftwareManager.Localization.Strings</c>.
/// </param>
/// <param name="assembly">The assembly holding the resource.</param>
public sealed class StringCatalog(string baseName, Assembly assembly)
{
    private readonly ResourceManager _manager = new(baseName, assembly);

    /// <summary>
    /// The string for <paramref name="key"/>, or the key itself if there is no such resource.
    ///
    /// Returning the key rather than throwing keeps a missing string a visible blemish in the
    /// window instead of a crash on the way to showing it, which is the right trade for text.
    /// </summary>
    public string Get(string key) => _manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    /// <summary>
    /// <see cref="Get"/> for a composite-format resource, with <paramref name="args"/>
    /// substituted in. Formatting uses <see cref="CultureInfo.CurrentCulture"/> — the user's
    /// number and date conventions — while the string itself came from the UI culture.
    /// </summary>
    public string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
