using Wlrix.Common.Localization;

namespace Wlrix.PolkitAgent.Localization;

/// <summary>
/// Strongly-typed access to the agent's localized UI strings (backed by <c>Strings.resx</c> and
/// its culture satellites). The labels in the dialog come through <c>{loc:Tr}</c>; what is here
/// is what code has to build.
///
/// polkit's own message for the action is not here: polkitd translates it, into the locale this
/// agent registered with.
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.PolkitAgent.Localization.Strings", typeof(Strings).Assembly);

    public static string PasswordPrompt => Catalog.Get("PasswordPrompt");
    public static string TryAgain => Catalog.Get("TryAgain");
    public static string CouldNotAuthenticate => Catalog.Get("CouldNotAuthenticate");
    public static string ActionLabel => Catalog.Get("ActionLabel");

    public static string AuthenticatingAs(string user) => Catalog.Format("AuthenticatingAs", user);
    public static string HelperUnavailable(string detail) => Catalog.Format("HelperUnavailable", detail);
}
