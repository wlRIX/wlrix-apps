using Wlrix.Common.Localization;
using Wlrix.Settings.Users.Services;

namespace Wlrix.Settings.Users.Localization;

/// <summary>
/// Strongly-typed access to the panel's strings (backed by <c>Strings.resx</c> and its culture
/// satellites). The static labels come through <c>{loc:Tr}</c>; what is here is what code builds.
/// </summary>
public static class Strings
{
    /// <summary>The resources behind these properties, for <c>{loc:Tr}</c> in XAML.</summary>
    public static StringCatalog Catalog { get; } =
        new("Wlrix.Settings.Users.Localization.Strings", typeof(Strings).Assembly);

    public static string Standard => Catalog.Get("Standard");
    public static string Administrator => Catalog.Get("Administrator");
    public static string YourAccount => Catalog.Get("YourAccount");
    public static string OtherAccounts => Catalog.Get("OtherAccounts");
    public static string NotAuthorized => Catalog.Get("NotAuthorized");

    /// <summary>What is wrong with a login name, as a sentence.</summary>
    internal static string Problem(UserNameProblem problem) => problem switch
    {
        UserNameProblem.Empty => Catalog.Get("NameEmpty"),
        UserNameProblem.TooLong => Catalog.Format("NameTooLong", AccountRules.MaxUserName),
        UserNameProblem.BadStart => Catalog.Get("NameBadStart"),
        UserNameProblem.BadCharacter => Catalog.Get("NameBadCharacter"),
        _ => Catalog.Get("NameTaken"),
    };

    /// <summary>A finger as people say it: fprintd's <c>right-index-finger</c> is "Right index finger".</summary>
    public static string Finger(string finger) => Catalog.Get("Finger_" + finger.Replace('-', '_'));

    /// <summary>What an <c>EnrollStatus</c> result asks of the person.</summary>
    public static string EnrollResult(string result) =>
        Catalog.Get("Enroll_" + result.Replace('-', '_')) is var text && text.StartsWith("Enroll_", StringComparison.Ordinal)
            ? result
            : text;
}
