using System.Text;

namespace Wlrix.Settings.Users.Services;

/// <summary>
/// The rules about accounts that do not need a bus: who must stay an administrator, what a login
/// name may be, and what one is suggested from a full name. Kept apart so they are unit-tested.
/// </summary>
internal static class AccountRules
{
    /// <summary>The longest login name the shadow tools accept.</summary>
    public const int MaxUserName = 32;

    /// <summary>
    /// Whether <paramref name="account"/> may stop being an administrator -- demoted, or deleted --
    /// given every account there is.
    /// </summary>
    /// <remarks>
    /// The machine has to keep somebody able to administer it. Another administrator is enough,
    /// and so is a root account that can be logged in to; but "can be" is only trusted when
    /// AccountsService says so plainly (<paramref name="rootUsable"/>), because a root whose
    /// password is <c>*</c> -- Ubuntu's -- may not report as locked, and guessing wrong here leaves
    /// a machine nobody can administer.
    /// </remarks>
    public static bool MayStopBeingAdmin(AccountInfo account, IEnumerable<AccountInfo> all, bool rootUsable)
    {
        if (!account.IsAdmin || rootUsable)
            return true;
        return all.Any(other => other.Uid != account.Uid && other.IsAdmin && !other.Locked);
    }

    /// <summary>What is wrong with a login name, or null: the rule <c>useradd</c> enforces.</summary>
    public static UserNameProblem? CheckUserName(string name)
    {
        if (name.Length == 0)
            return UserNameProblem.Empty;
        if (name.Length > MaxUserName)
            return UserNameProblem.TooLong;
        if (!(char.IsAsciiLetterLower(name[0]) || name[0] == '_'))
            return UserNameProblem.BadStart;
        if (!name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-'))
            return UserNameProblem.BadCharacter;
        return null;
    }

    /// <summary>
    /// A login name suggested from a full name: its first word, lowercased, accents dropped, and
    /// anything a login name may not hold left out -- "Zoë Ångström" becomes <c>zoe</c>.
    /// </summary>
    public static string SuggestUserName(string fullName)
    {
        var first = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var plain = new StringBuilder();
        foreach (var c in first.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-')
                plain.Append(c);
        }
        var name = plain.ToString().TrimStart('-', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return name.Length > MaxUserName ? name[..MaxUserName] : name;
    }

    /// <summary>
    /// What stands in for a picture: the initials of the first and last words of the name, or
    /// of the login name when there is no full name.
    /// </summary>
    public static string Initials(string realName, string userName)
    {
        var words = realName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return userName.Length > 0 ? char.ToUpperInvariant(userName[0]).ToString() : "?";
        var first = char.ToUpperInvariant(words[0][0]).ToString();
        return words.Length == 1 ? first : first + char.ToUpperInvariant(words[^1][0]);
    }
}

/// <summary>Why a login name will not do.</summary>
public enum UserNameProblem
{
    Empty,
    TooLong,
    BadStart,
    BadCharacter,
    Taken,
}
