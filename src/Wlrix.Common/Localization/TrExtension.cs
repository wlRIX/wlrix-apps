namespace Wlrix.Common.Localization;

/// <summary>
/// XAML markup extension that resolves a key against the application's <see cref="Catalog"/>,
/// so static labels in AXAML are localizable without routing every one of them through a view
/// model property:
///
/// <code>
/// xmlns:loc="using:Wlrix.Common.Localization"
/// &lt;TextBlock Text="{loc:Tr AvailableSoftware}" /&gt;
/// </code>
///
/// It deliberately does not derive from <c>Avalonia.Markup.Xaml.MarkupExtension</c>: Avalonia
/// resolves markup extensions structurally — any type whose name ends in <c>Extension</c> and
/// that has a <c>ProvideValue</c> method will do — which lets this live in <c>Wlrix.Common</c>
/// without dragging Avalonia into a library that otherwise has no UI in it.
/// </summary>
/// <remarks>
/// The value is resolved once, when the XAML is loaded. That is correct while the UI culture is
/// fixed for the life of the process, which is the case today: .NET reads it from the locale at
/// startup and nothing changes it afterwards. Switching language live would want a binding
/// against a view model instead, and is not something wlRIX offers.
/// </remarks>
public sealed class TrExtension
{
    /// <summary>
    /// The catalog every <c>{loc:Tr}</c> in this process reads from. An app sets this in
    /// <c>App.Initialize</c>, before any XAML that uses the extension is loaded; until it does,
    /// the extension yields the key, the same as a missing resource.
    /// </summary>
    public static StringCatalog? Catalog { get; set; }

    /// <summary>Property syntax: <c>{loc:Tr Key=Lookup}</c>.</summary>
    public TrExtension()
    {
    }

    /// <summary>Positional syntax: <c>{loc:Tr Lookup}</c>.</summary>
    public TrExtension(string key) => Key = key;

    /// <summary>The resource key to look up.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The localized string. Avalonia calls this when the XAML is loaded.</summary>
    public object ProvideValue() =>
        Catalog is { } catalog && Key.Length > 0 ? catalog.Get(Key) : Key;
}
