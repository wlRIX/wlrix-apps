using System.Runtime.InteropServices;
using Tmds.DBus.Protocol;
using Wlrix.Settings.Users.DBus;

namespace Wlrix.Settings.Users.Services;

/// <summary>One account, as the list and the form show it.</summary>
internal sealed record AccountInfo(
    string Path,
    ulong Uid,
    string UserName,
    string RealName,
    string Email,
    bool IsAdmin,
    string IconFile,
    bool Locked,
    string HomeDirectory)
{
    /// <summary>The full name, or the login name when there is none -- what a list row leads with.</summary>
    public string DisplayName => RealName.Length > 0 ? RealName : UserName;
}

/// <summary>
/// AccountsService on the system bus: the accounts, and every change this panel makes to them.
/// </summary>
/// <remarks>
/// <para>
/// AccountsService is the authority, not a mirror: it runs as root, edits <c>/etc/passwd</c>,
/// <c>/etc/shadow</c> and <c>/etc/group</c> itself, and asks polkit whether each caller may. The
/// active session changes its own name, email and picture without a prompt, its own password with
/// its own, and anything about anyone else with an administrator's -- which comes up in
/// <c>wlrix-polkit-agent</c>. So no call here has a timeout shorter than a person takes to type.
/// </para>
/// <para>
/// "Administrator" is AccountsService's account type 1: membership of the group it was built to
/// treat as administrators (<c>wheel</c> on Arch, <c>sudo</c> on Debian), which is what <c>sudo</c>
/// and polkit go by.
/// </para>
/// </remarks>
internal sealed partial class AccountsClient : IDisposable
{
    private const string BusName = "org.freedesktop.Accounts";
    private const string RootPath = "/org/freedesktop/Accounts";

    private readonly DBusConnection _connection;
    private readonly Accounts _accounts;

    private AccountsClient(DBusConnection connection)
    {
        _connection = connection;
        _accounts = new Accounts(connection, BusName, RootPath);
    }

    public static async Task<AccountsClient> ConnectAsync()
    {
        var address = DBusAddress.System ?? throw new InvalidOperationException("no system bus");
        var connection = new DBusConnection(new DBusConnectionOptions(address) { AutoConnect = false });
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
        return new AccountsClient(connection);
    }

    public void Dispose() => _connection.Dispose();

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint GetUid();

    /// <summary>The account this panel is running as.</summary>
    public static ulong OwnUid { get; } = GetUid();

    /// <summary>Every ordinary account, sorted by name. System accounts are AccountsService's to hide.</summary>
    public async Task<IReadOnlyList<AccountInfo>> ListAsync()
    {
        var list = new List<AccountInfo>();
        foreach (var path in await _accounts.ListCachedUsersAsync().ConfigureAwait(false))
        {
            try
            {
                list.Add(await ReadAsync(path).ConfigureAwait(false));
            }
            catch (DBusErrorReplyException)
            {
                // Deleted between the list and the read; the next refresh will not list it.
            }
        }
        // AccountsService only caches an account once somebody has logged in to it or asked for
        // it by name, so the panel's own user is looked up too, in case this is its first login.
        if (list.All(account => account.Uid != OwnUid))
        {
            try
            {
                list.Add(await ReadAsync(await _accounts.FindUserByIdAsync((long)OwnUid).ConfigureAwait(false)).ConfigureAwait(false));
            }
            catch (DBusErrorReplyException)
            {
            }
        }
        list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }

    private async Task<AccountInfo> ReadAsync(ObjectPath path)
    {
        var user = await new User(_connection, BusName, path).GetPropertiesAsync().ConfigureAwait(false);
        return new AccountInfo(
            path.ToString(),
            user.Uid,
            user.UserName,
            user.RealName,
            user.Email,
            user.AccountType == 1,
            user.IconFile,
            user.Locked,
            user.HomeDirectory);
    }

    /// <summary>
    /// Whether root can be logged in to, as far as AccountsService says plainly: not locked, and an
    /// ordinary password. Anything else -- including not being able to ask -- is false, which is
    /// the direction that keeps the administrator rule (see <see cref="AccountRules"/>).
    /// </summary>
    public async Task<bool> RootUsableAsync()
    {
        try
        {
            var path = await _accounts.FindUserByIdAsync(0).ConfigureAwait(false);
            var root = await new User(_connection, BusName, path).GetPropertiesAsync().ConfigureAwait(false);
            return !root.Locked && root.PasswordMode == 0;
        }
        catch (DBusErrorReplyException)
        {
            return false;
        }
    }

    private User Proxy(string path) => new(_connection, BusName, new ObjectPath(path));

    public Task SetRealNameAsync(string path, string name) => Proxy(path).SetRealNameAsync(name);

    public Task SetUserNameAsync(string path, string name) => Proxy(path).SetUserNameAsync(name);

    public Task SetEmailAsync(string path, string email) => Proxy(path).SetEmailAsync(email);

    public Task SetAdminAsync(string path, bool admin) => Proxy(path).SetAccountTypeAsync(admin ? 1 : 0);

    /// <summary>Set the picture to a file AccountsService copies; an empty name removes it.</summary>
    public Task SetIconFileAsync(string path, string file) => Proxy(path).SetIconFileAsync(file);

    /// <summary>Set a new password, hashed here; see <see cref="Crypt"/>.</summary>
    public async Task SetPasswordAsync(string path, string password)
    {
        var user = Proxy(path);
        // Mode 0, "a password is set": a new account starts in another, and an account whose
        // password was cleared would otherwise keep logging in without one.
        await user.SetPasswordModeAsync(0).ConfigureAwait(false);
        await user.SetPasswordAsync(Crypt.Hash(password), string.Empty).ConfigureAwait(false);
    }

    /// <summary>Create an account and give it its password. Returns its path.</summary>
    public async Task<string> CreateAsync(string userName, string realName, bool admin, string password)
    {
        var path = await _accounts.CreateUserAsync(userName, realName, admin ? 1 : 0).ConfigureAwait(false);
        await SetPasswordAsync(path.ToString(), password).ConfigureAwait(false);
        return path.ToString();
    }

    /// <summary>Delete an account; <paramref name="removeFiles"/> takes its home folder with it.</summary>
    public Task DeleteAsync(ulong uid, bool removeFiles) => _accounts.DeleteUserAsync((long)uid, removeFiles);

    /// <summary>Whether a login name is in use already, by any account, system ones included.</summary>
    public async Task<bool> UserNameTakenAsync(string name)
    {
        try
        {
            await _accounts.FindUserByNameAsync(name).ConfigureAwait(false);
            return true;
        }
        catch (DBusErrorReplyException)
        {
            return false;
        }
    }

    /// <summary>What AccountsService said when it refused, without the error name in front.</summary>
    public static string Refusal(Exception error) => error switch
    {
        DBusErrorReplyException reply when reply.ErrorName.EndsWith(".PermissionDenied", StringComparison.Ordinal)
            || reply.ErrorName.EndsWith(".NotAuthorized", StringComparison.Ordinal) => Localization.Strings.NotAuthorized,
        DBusErrorReplyException reply => reply.ErrorMessage,
        _ => error.Message,
    };
}
