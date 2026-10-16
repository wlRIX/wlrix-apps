using System.Runtime.InteropServices;
using Tmds.DBus.Protocol;

namespace Wlrix.PolkitAgent.Services;

/// <summary>
/// An identity polkit will accept a password for, as it sends one: <c>unix-user</c> with a
/// <c>uid</c>, or <c>unix-group</c> with a <c>gid</c>.
/// </summary>
internal sealed record PolkitIdentity(string Kind, uint Id);

/// <summary>The account names behind uids and the members of groups.</summary>
internal interface IAccountLookup
{
    /// <summary>The login name for <paramref name="uid"/>, or null if there is none.</summary>
    string? UserName(uint uid);

    /// <summary>The members of group <paramref name="gid"/>. Empty if it has none, or is unknown.</summary>
    IReadOnlyList<string> GroupMembers(uint gid);

    /// <summary>Who is running this agent.</summary>
    string CurrentUser { get; }
}

/// <summary>
/// Turning polkit's identities into the list of people the dialog offers.
///
/// polkit sends the administrator identities its rules name -- on most distributions the
/// <c>wheel</c> group, which arrives either as that group or as its members. Either way the user
/// picks a person and types that person's password, so groups are expanded, the list is
/// de-duplicated, and the person at the keyboard comes first when they are on it.
/// </summary>
internal static class Identities
{
    /// <summary>The user names to offer, in polkit's order, with the current user's preselected.</summary>
    public static (IReadOnlyList<string> Users, int Selected) Resolve(
        IEnumerable<PolkitIdentity> identities, IAccountLookup accounts)
    {
        var users = new List<string>();
        foreach (var identity in identities)
        {
            IEnumerable<string> names = identity.Kind switch
            {
                "unix-user" => accounts.UserName(identity.Id) is { } name ? [name] : [],
                "unix-group" => accounts.GroupMembers(identity.Id),
                _ => [],
            };
            foreach (var name in names)
                if (!users.Contains(name))
                    users.Add(name);
        }

        var selected = users.IndexOf(accounts.CurrentUser);
        return (users, selected < 0 ? 0 : selected);
    }

    /// <summary>Reads <c>a(sa{sv})</c>: polkit's identity list.</summary>
    public static List<PolkitIdentity> Read(ref Reader reader)
    {
        var identities = new List<PolkitIdentity>();
        var end = reader.ReadArrayStart(DBusType.Struct);
        while (reader.HasNext(end))
        {
            reader.AlignStruct();
            var kind = reader.ReadString();
            var details = reader.ReadDictionaryOfStringToVariantValue();
            var key = kind == "unix-group" ? "gid" : "uid";
            if (details.TryGetValue(key, out var id) && id.Type == VariantValueType.UInt32)
                identities.Add(new PolkitIdentity(kind, id.GetUInt32()));
        }

        return identities;
    }
}

/// <summary>
/// <see cref="IAccountLookup"/> through libc, so accounts from LDAP or systemd-homed are found as
/// well as those in <c>/etc/passwd</c>: the same NSS lookups <c>id</c> makes.
/// </summary>
internal sealed partial class LibcAccounts : IAccountLookup
{
    public string CurrentUser { get; } = Environment.UserName;

    public string? UserName(uint uid)
    {
        // struct passwd starts with char *pw_name.
        var entry = getpwuid(uid);
        return entry == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(entry));
    }

    public IReadOnlyList<string> GroupMembers(uint gid)
    {
        // struct group { char *gr_name; char *gr_passwd; gid_t gr_gid; char **gr_mem; }: on a
        // 64-bit system the gid is padded out, so the member list is the fourth pointer-sized field.
        var entry = getgrgid(gid);
        if (entry == IntPtr.Zero)
            return [];

        var members = new List<string>();
        var list = Marshal.ReadIntPtr(entry, 3 * IntPtr.Size);
        for (var i = 0; list != IntPtr.Zero; i++)
        {
            var member = Marshal.ReadIntPtr(list, i * IntPtr.Size);
            if (member == IntPtr.Zero)
                break;
            if (Marshal.PtrToStringUTF8(member) is { } name)
                members.Add(name);
        }

        return members;
    }

    [LibraryImport("libc", EntryPoint = "getpwuid")]
    private static partial IntPtr getpwuid(uint uid);

    [LibraryImport("libc", EntryPoint = "getgrgid")]
    private static partial IntPtr getgrgid(uint gid);
}
