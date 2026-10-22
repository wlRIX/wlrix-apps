using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ReactiveUI;
using Tmds.DBus.Protocol;
using Wlrix.Settings.Users.Localization;
using Wlrix.Settings.Users.Services;

namespace Wlrix.Settings.Users.ViewModels;

public abstract class ViewModelBase : ReactiveObject;

/// <summary>One account in the list: its picture or initials, its names, and the heading above it.</summary>
internal sealed class AccountRow : ViewModelBase
{
    public AccountRow(AccountInfo info, bool own, string? heading)
    {
        Info = info;
        IsOwn = own;
        Heading = heading;
        Initials = AccountRules.Initials(info.RealName, info.UserName);
        InitialsBrush = new SolidColorBrush(Pictures.InitialsColor(info.UserName));
        Picture = LoadPicture(info.IconFile);
    }

    public AccountInfo Info { get; }

    public bool IsOwn { get; }

    /// <summary>"Your Account" over the first row, "Other Accounts" over the first of the rest.</summary>
    public string? Heading { get; }

    public bool HasHeading => Heading is not null;

    public string Initials { get; }

    public IBrush InitialsBrush { get; }

    /// <summary>The account's picture, or null for initials.</summary>
    public Bitmap? Picture { get; }

    public bool HasPicture => Picture is not null;

    public string DisplayName => Info.DisplayName;

    public string UserName => Info.UserName;

    /// <summary>
    /// The picture AccountsService names, if it is there and readable. Its own copies are
    /// world-readable; a <c>~/.face</c> in somebody else's private home is not, and that account
    /// shows initials here, as it would to any other user.
    /// </summary>
    internal static Bitmap? LoadPicture(string file)
    {
        if (file.Length == 0 || !File.Exists(file))
            return null;
        try
        {
            using var stream = File.OpenRead(file);
            return Bitmap.DecodeToWidth(stream, Pictures.Size);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>
/// Backs the User Accounts window: the accounts on the left, the selected one's details on the
/// right, and Apply and Reset for the details.
/// </summary>
/// <remarks>
/// Name, username, account type and email are edits that collect until <see cref="ApplyAsync"/>,
/// as KDE's panel does: changing somebody else's account is an administrator's act, and one Apply
/// is one password prompt rather than one per field. The picture, the password, the fingerprints,
/// adding and deleting each act at once from their own dialogs.
/// </remarks>
internal sealed class UsersViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(5);

    private readonly DispatcherTimer _timer = new() { Interval = RefreshEvery };
    private AccountsClient? _client;
    private AccountRow? _selected;
    private bool _rootUsable;
    private bool _fingerprints;
    private bool _ready;
    private bool _busy;
    private string _status = string.Empty;

    /// <summary>Edits left behind by selecting another account, until the window asks about them.</summary>
    private Edits? _pending;

    private string _name = string.Empty;
    private string _userName = string.Empty;
    private int _accountType;
    private string _email = string.Empty;

    public UsersViewModel()
    {
        _timer.Tick += async (_, _) => await RefreshAsync().ConfigureAwait(true);
    }

    public ObservableCollection<AccountRow> Accounts { get; } = [];

    /// <summary>"Standard" and "Administrator", for the Account type list.</summary>
    public IReadOnlyList<string> AccountTypes { get; } = [Strings.Standard, Strings.Administrator];

    public bool IsReady
    {
        get => _ready;
        private set => this.RaiseAndSetIfChanged(ref _ready, value);
    }

    public string Status
    {
        get => _status;
        set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public AccountRow? Selected
    {
        get => _selected;
        set
        {
            if (value is null && _selected is not null && Accounts.Contains(_selected))
                return;
            if (value != _selected && _selected is { } leaving && IsDirty)
                _pending = new Edits(leaving.Info, _name, _userName, _accountType == 1, _email);
            this.RaiseAndSetIfChanged(ref _selected, value);
            LoadForm();
        }
    }

    public bool HasSelection => _selected is not null;

    // The form.

    public string Name
    {
        get => _name;
        set
        {
            this.RaiseAndSetIfChanged(ref _name, value);
            RaiseForm();
        }
    }

    public string UserName
    {
        get => _userName;
        set
        {
            this.RaiseAndSetIfChanged(ref _userName, value);
            RaiseForm();
        }
    }

    public int AccountType
    {
        get => _accountType;
        set
        {
            this.RaiseAndSetIfChanged(ref _accountType, value);
            RaiseForm();
        }
    }

    public string Email
    {
        get => _email;
        set
        {
            this.RaiseAndSetIfChanged(ref _email, value);
            RaiseForm();
        }
    }

    public string Heading => _selected?.DisplayName ?? string.Empty;

    public string Initials => _selected?.Initials ?? string.Empty;

    public IBrush? InitialsBrush => _selected?.InitialsBrush;

    public Bitmap? Picture => _selected?.Picture;

    public bool HasPicture => _selected?.HasPicture == true;

    public bool IsOwn => _selected?.IsOwn == true;

    /// <summary>A login name is not changed from inside the session that is logged in with it.</summary>
    public bool CanEditUserName => HasSelection && !IsOwn && !_busy;

    /// <summary>Not for one's own account, and not for the last administrator; see <see cref="AccountRules"/>.</summary>
    public bool CanEditAccountType => HasSelection && !IsOwn && !_busy && MayStopBeingAdmin;

    public bool CanDelete => HasSelection && !IsOwn && !_busy && MayStopBeingAdmin;

    /// <summary>Why the account type and Delete are grayed out, when the reason is the last-administrator rule.</summary>
    public bool ShowLastAdmin => HasSelection && !IsOwn && !MayStopBeingAdmin;

    public bool CanEdit => HasSelection && !_busy;

    /// <summary>Only for one's own account: fprintd enrolls for the person whose finger is on the reader.</summary>
    public bool ShowFingerprints => IsOwn && _fingerprints;

    public bool IsDirty =>
        _selected is { } row
        && (Name != row.Info.RealName
            || UserName != row.Info.UserName
            || (AccountType == 1) != row.Info.IsAdmin
            || Email != row.Info.Email);

    public bool CanApply => IsDirty && !_busy && UserNameProblem is null;

    public string? UserNameProblem =>
        _selected is { } row && UserName != row.Info.UserName
            ? AccountRules.CheckUserName(UserName) is { } problem ? Strings.Problem(problem) : null
            : null;

    private bool MayStopBeingAdmin =>
        _selected is not { } row
        || AccountRules.MayStopBeingAdmin(row.Info, Accounts.Select(account => account.Info), _rootUsable);

    public async Task InitializeAsync()
    {
        try
        {
            _client = await AccountsClient.ConnectAsync().ConfigureAwait(true);
        }
        catch (Exception error) when (error is DBusExceptionBase or IOException or InvalidOperationException)
        {
            Status = Strings.Catalog.Format("NoAccountsService", error.Message);
            return;
        }
        _fingerprints = await Fingerprints.AvailableAsync().ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        IsReady = true;
        _timer.Start();
    }

    /// <summary>Read the accounts again, keeping the selection, and the form when it has edits.</summary>
    public async Task RefreshAsync()
    {
        if (_client is null)
            return;
        IReadOnlyList<AccountInfo> accounts;
        try
        {
            accounts = await _client.ListAsync().ConfigureAwait(true);
            _rootUsable = await _client.RootUsableAsync().ConfigureAwait(true);
        }
        catch (DBusExceptionBase error)
        {
            Status = Strings.Catalog.Format("NoAccountsService", error.Message);
            return;
        }

        var own = accounts.Where(account => account.Uid == AccountsClient.OwnUid).ToList();
        var others = accounts.Where(account => account.Uid != AccountsClient.OwnUid).ToList();
        var rows = own.Select((info, i) => new AccountRow(info, true, i == 0 ? Strings.YourAccount : null))
            .Concat(others.Select((info, i) => new AccountRow(info, false, i == 0 ? Strings.OtherAccounts : null)))
            .ToList();
        if (rows.Select(row => row.Info).SequenceEqual(Accounts.Select(row => row.Info)))
            return;

        var keep = _selected?.Info.Uid;
        var dirty = IsDirty;
        Accounts.Clear();
        foreach (var row in rows)
            Accounts.Add(row);
        var again = rows.FirstOrDefault(row => row.Info.Uid == keep) ?? rows.FirstOrDefault();
        _selected = again;
        this.RaisePropertyChanged(nameof(Selected));
        if (!dirty || again?.Info.Uid != keep)
            LoadForm();
        else
            RaiseForm();
    }

    /// <summary>Put back what the account holds, dropping the edits.</summary>
    public void Reset() => LoadForm();

    /// <summary>Write the edits. One prompt, for somebody else's account: polkit keeps the answer.</summary>
    public async Task ApplyAsync()
    {
        if (_selected is not { } row || !CanApply)
            return;
        await WriteAsync(new Edits(row.Info, Name, UserName, AccountType == 1, Email)).ConfigureAwait(true);
        LoadForm();
    }

    /// <summary>Whether <paramref name="row"/> was left with edits not yet applied.</summary>
    public bool PendingFor(AccountRow row) => _pending?.Account.Uid == row.Info.Uid;

    public async Task ApplyPendingAsync()
    {
        if (_pending is not { } pending)
            return;
        _pending = null;
        await WriteAsync(pending).ConfigureAwait(true);
    }

    public void DropPending() => _pending = null;

    private async Task WriteAsync(Edits edits)
    {
        if (_client is null)
            return;
        var info = edits.Account;
        if (edits.UserName != info.UserName)
        {
            if (AccountRules.CheckUserName(edits.UserName) is { } problem)
            {
                Status = Strings.Problem(problem);
                return;
            }
            if (await _client.UserNameTakenAsync(edits.UserName).ConfigureAwait(true))
            {
                Status = Strings.Problem(Services.UserNameProblem.Taken);
                return;
            }
        }
        await Act(async client =>
        {
            if (edits.RealName != info.RealName)
                await client.SetRealNameAsync(info.Path, edits.RealName).ConfigureAwait(true);
            if (edits.Email != info.Email)
                await client.SetEmailAsync(info.Path, edits.Email).ConfigureAwait(true);
            if (edits.UserName != info.UserName)
                await client.SetUserNameAsync(info.Path, edits.UserName).ConfigureAwait(true);
            if (edits.Admin != info.IsAdmin
                && (edits.Admin || AccountRules.MayStopBeingAdmin(info, Accounts.Select(account => account.Info), _rootUsable)))
                await client.SetAdminAsync(info.Path, edits.Admin).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    /// <summary>A picture was chosen in the Picture dialog.</summary>
    public async Task SetPictureAsync(PictureChoice choice)
    {
        if (_selected is not { } row)
            return;
        var own = row.IsOwn;
        await Act(async client =>
        {
            if (choice.Remove)
            {
                await client.SetIconFileAsync(row.Info.Path, string.Empty).ConfigureAwait(true);
                if (own)
                    Pictures.RemoveOwnFace();
                return;
            }
            var square = Pictures.MakeSquare(choice.File!);
            try
            {
                await client.SetIconFileAsync(row.Info.Path, square).ConfigureAwait(true);
                if (own)
                    Pictures.WriteOwnFace(square);
            }
            finally
            {
                File.Delete(square);
            }
        }).ConfigureAwait(true);
    }

    public async Task SetPasswordAsync(string password)
    {
        if (_selected is not { } row)
            return;
        await Act(client => client.SetPasswordAsync(row.Info.Path, password)).ConfigureAwait(true);
    }

    public async Task<bool> CreateAsync(string userName, string realName, bool admin, string password)
    {
        if (_client is null)
            return false;
        if (await _client.UserNameTakenAsync(userName).ConfigureAwait(true))
        {
            Status = Strings.Problem(Services.UserNameProblem.Taken);
            return false;
        }
        string? created = null;
        await Act(async client => created = await client.CreateAsync(userName, realName, admin, password).ConfigureAwait(true))
            .ConfigureAwait(true);
        if (created is not null)
            Selected = Accounts.FirstOrDefault(account => account.Info.Path == created) ?? Selected;
        return created is not null;
    }

    public async Task DeleteAsync(bool removeFiles)
    {
        if (_selected is not { } row || !CanDelete)
            return;
        await Act(client => client.DeleteAsync(row.Info.Uid, removeFiles)).ConfigureAwait(true);
    }

    /// <summary>Whether a login name is in use, for the Add User dialog.</summary>
    public async Task<bool> UserNameTakenAsync(string name) =>
        _client is not null && await _client.UserNameTakenAsync(name).ConfigureAwait(true);

    /// <summary>Run one change, saying why if it was refused, and read the accounts again.</summary>
    private async Task Act(Func<AccountsClient, Task> change)
    {
        if (_client is null)
            return;
        SetBusy(true);
        try
        {
            await change(_client).ConfigureAwait(true);
            Status = string.Empty;
        }
        catch (Exception error) when (error is DBusExceptionBase or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Status = Strings.Catalog.Format("ChangeFailed", AccountsClient.Refusal(error));
        }
        finally
        {
            SetBusy(false);
        }
        await RefreshAsync().ConfigureAwait(true);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RaiseForm();
    }

    private void LoadForm()
    {
        var info = _selected?.Info;
        _name = info?.RealName ?? string.Empty;
        _userName = info?.UserName ?? string.Empty;
        _accountType = info?.IsAdmin == true ? 1 : 0;
        _email = info?.Email ?? string.Empty;
        foreach (var name in new[] { nameof(Name), nameof(UserName), nameof(AccountType), nameof(Email) })
            this.RaisePropertyChanged(name);
        RaiseForm();
    }

    private void RaiseForm()
    {
        foreach (var name in new[]
                 {
                     nameof(HasSelection), nameof(Heading), nameof(Initials), nameof(InitialsBrush),
                     nameof(Picture), nameof(HasPicture), nameof(IsOwn), nameof(CanEditUserName),
                     nameof(CanEditAccountType), nameof(CanDelete), nameof(ShowLastAdmin), nameof(CanEdit),
                     nameof(ShowFingerprints), nameof(IsDirty), nameof(CanApply), nameof(UserNameProblem),
                 })
            this.RaisePropertyChanged(name);
    }

    public void Dispose()
    {
        _timer.Stop();
        _client?.Dispose();
    }
}

/// <summary>The four fields an Apply writes, for one account.</summary>
internal sealed record Edits(AccountInfo Account, string RealName, string UserName, bool Admin, string Email);

/// <summary>What the Picture dialog came back with: a file to use, or Remove.</summary>
public sealed record PictureChoice(string? File, bool Remove);
