using Wlrix.Common.Localization;

namespace Wlrix.Pinentry.Localization;

/// <summary>
/// Strongly-typed access to the pinentry's own strings (backed by <c>Strings.resx</c> and its
/// culture satellites). These are only the fallbacks: gpg-agent normally sends the description,
/// the prompt and the button labels itself, already in the user's language.
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.Pinentry.Localization.Strings", typeof(Strings).Assembly);

    public static string WindowTitle => Catalog.Get("WindowTitle");
    public static string PinHeading => Catalog.Get("PinHeading");
    public static string ConfirmHeading => Catalog.Get("ConfirmHeading");
    public static string PinPrompt => Catalog.Get("PinPrompt");
    public static string RepeatPrompt => Catalog.Get("RepeatPrompt");
    public static string RepeatMismatch => Catalog.Get("RepeatMismatch");
    public static string Ok => Catalog.Get("Ok");
    public static string Cancel => Catalog.Get("Cancel");
}
