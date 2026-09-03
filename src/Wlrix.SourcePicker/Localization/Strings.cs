using Wlrix.Common.Localization;

namespace Wlrix.SourcePicker.Localization;

/// <summary>
/// Strongly-typed access to the app's localized UI strings (backed by <c>Strings.resx</c> and
/// its culture satellites).
///
/// The static labels in the window come through <c>{loc:Tr}</c>, which reads the same
/// <see cref="Catalog"/>. What is here is what code has to build: the prompt and the hint, which
/// depend on what the portal asked for.
///
/// The tile titles are not here. They are the compositor's — a monitor's connector name and a
/// window's own title — and nothing this app could translate.
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.SourcePicker.Localization.Strings", typeof(Strings).Assembly);

    /// <summary>
    /// The instruction across the top, naming the application when the portal gave an id.
    /// </summary>
    /// <remarks>
    /// An application id is not a name — <c>org.mozilla.firefox</c> is what arrives — but it is
    /// what there is, and telling the user <em>something</em> asked is worth more than a
    /// sentence that names nobody. Hence two resources rather than one with an empty slot.
    /// </remarks>
    public static string Prompt(string? appId) =>
        string.IsNullOrWhiteSpace(appId)
            ? Catalog.Get("PromptAnonymous")
            : Catalog.Format("PromptForApp", appId);

    /// <summary>Whether the portal asked for one source or several.</summary>
    public static string Hint(bool multiple) =>
        Catalog.Get(multiple ? "HintMultiple" : "HintSingle");
}
