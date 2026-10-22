using System.Collections.ObjectModel;
using ReactiveUI;
using Wlrix.Settings.Users.Localization;
using Wlrix.Settings.Users.Services;

namespace Wlrix.Settings.Users.ViewModels;

/// <summary>A new password, typed twice. Done only when both agree and are not empty.</summary>
internal class PasswordModel : ViewModelBase
{
    private string _password = string.Empty;
    private string _confirm = string.Empty;
    private bool _show;

    public string Password
    {
        get => _password;
        set
        {
            this.RaiseAndSetIfChanged(ref _password, value);
            RaisePassword();
        }
    }

    public string Confirm
    {
        get => _confirm;
        set
        {
            this.RaiseAndSetIfChanged(ref _confirm, value);
            RaisePassword();
        }
    }

    public bool ShowPassword
    {
        get => _show;
        set
        {
            this.RaiseAndSetIfChanged(ref _show, value);
            this.RaisePropertyChanged(nameof(MaskChar));
        }
    }

    public char MaskChar => _show ? '\0' : '•';

    /// <summary>What is wrong, once there is something to say: nothing while the second field is empty.</summary>
    public string? PasswordProblem =>
        _confirm.Length == 0 ? null
        : _password != _confirm ? Strings.Catalog.Get("PasswordsDiffer")
        : null;

    public bool PasswordValid => _password.Length > 0 && _password == _confirm;

    public virtual bool CanFinish => PasswordValid;

    /// <summary>Forget what was typed, once it has been used or abandoned.</summary>
    public void Clear()
    {
        Password = string.Empty;
        Confirm = string.Empty;
    }

    protected virtual void RaisePassword()
    {
        this.RaisePropertyChanged(nameof(PasswordProblem));
        this.RaisePropertyChanged(nameof(PasswordValid));
        this.RaisePropertyChanged(nameof(CanFinish));
    }
}

/// <summary>
/// Add User: a full name, a login name suggested from it until it is typed over, the account type,
/// and the password twice.
/// </summary>
internal sealed class AddUserModel : PasswordModel
{
    private string _fullName = string.Empty;
    private string _userName = string.Empty;
    private bool _userNameTyped;
    private int _accountType;
    private bool _taken;

    public IReadOnlyList<string> AccountTypes { get; } = [Strings.Standard, Strings.Administrator];

    public string FullName
    {
        get => _fullName;
        set
        {
            this.RaiseAndSetIfChanged(ref _fullName, value);
            if (!_userNameTyped)
                SetUserName(AccountRules.SuggestUserName(value));
        }
    }

    public string UserName
    {
        get => _userName;
        set
        {
            if (value == _userName)
                return;
            // Typed over: the suggestion stops following the full name from here on.
            _userNameTyped = true;
            SetUserName(value);
        }
    }

    public int AccountType
    {
        get => _accountType;
        set => this.RaiseAndSetIfChanged(ref _accountType, value);
    }

    /// <summary>Set once the name was found to be in use; cleared by any change to it.</summary>
    public bool Taken
    {
        get => _taken;
        set
        {
            this.RaiseAndSetIfChanged(ref _taken, value);
            RaisePassword();
        }
    }

    public string? UserNameProblem =>
        _userName.Length == 0 ? null
        : _taken ? Strings.Problem(Services.UserNameProblem.Taken)
        : AccountRules.CheckUserName(_userName) is { } problem ? Strings.Problem(problem) : null;

    public override bool CanFinish =>
        PasswordValid && _userName.Length > 0 && AccountRules.CheckUserName(_userName) is null && !_taken;

    private void SetUserName(string value)
    {
        _userName = value;
        _taken = false;
        this.RaisePropertyChanged(nameof(UserName));
        RaisePassword();
    }

    protected override void RaisePassword()
    {
        base.RaisePassword();
        this.RaisePropertyChanged(nameof(UserNameProblem));
    }
}

/// <summary>One enrolled finger in the Fingerprints list.</summary>
internal sealed record FingerRow(string Finger)
{
    public string Label => Strings.Finger(Finger);
}

/// <summary>
/// The Fingerprints dialog: the fingers enrolled for the panel's user, deleting them, and enrolling
/// another, a touch at a time.
/// </summary>
internal sealed class FingerprintModel : ViewModelBase, IAsyncDisposable
{
    private Fingerprints? _reader;
    private IDisposable? _watch;
    private string _readerName = string.Empty;
    private int _stages;
    private int _passed;
    private string _message = string.Empty;
    private bool _enrolling;
    private bool _choosing;
    private FingerRow? _selected;
    private string? _toEnroll;

    public ObservableCollection<FingerRow> Enrolled { get; } = [];

    /// <summary>The fingers not enrolled yet, for Add.</summary>
    public ObservableCollection<FingerRow> Available { get; } = [];

    public string ReaderLine => Strings.Catalog.Format("Reader", _readerName);

    public bool HasNone => Enrolled.Count == 0;

    public FingerRow? Selected
    {
        get => _selected;
        set
        {
            this.RaiseAndSetIfChanged(ref _selected, value);
            Raise();
        }
    }

    public FingerRow? ToEnroll
    {
        get => _toEnroll is null ? null : Available.FirstOrDefault(row => row.Finger == _toEnroll);
        set
        {
            _toEnroll = value?.Finger;
            this.RaisePropertyChanged();
            Raise();
        }
    }

    /// <summary>Showing the finger list and Start, between Add and the first touch.</summary>
    public bool Choosing
    {
        get => _choosing;
        set
        {
            this.RaiseAndSetIfChanged(ref _choosing, value);
            Raise();
        }
    }

    public bool Enrolling
    {
        get => _enrolling;
        private set
        {
            this.RaiseAndSetIfChanged(ref _enrolling, value);
            Raise();
        }
    }

    public string Message
    {
        get => _message;
        private set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    public string Progress => _stages > 0 ? Strings.Catalog.Format("EnrollProgress", _passed, _stages) : string.Empty;

    public double ProgressValue => _stages > 0 ? (double)_passed / _stages : 0;

    public bool CanAdd => _reader is not null && !_enrolling && !_choosing && Available.Count > 0;

    public bool CanDelete => _reader is not null && !_enrolling && !_choosing && _selected is not null;

    public bool CanDeleteAll => _reader is not null && !_enrolling && !_choosing && Enrolled.Count > 0;

    public bool CanStart => _choosing && !_enrolling && _toEnroll is not null;

    /// <summary>Claim the reader and read what is enrolled. False, with <see cref="Message"/> saying why, if it is busy.</summary>
    public async Task<bool> OpenAsync()
    {
        try
        {
            _reader = await Fingerprints.OpenAsync().ConfigureAwait(true);
            _readerName = await _reader.NameAsync().ConfigureAwait(true);
            _stages = await _reader.StagesAsync().ConfigureAwait(true);
            this.RaisePropertyChanged(nameof(ReaderLine));
            await ReloadAsync().ConfigureAwait(true);
            return true;
        }
        catch (Exception error) when (error is Tmds.DBus.Protocol.DBusExceptionBase or IOException or InvalidOperationException)
        {
            Message = Strings.Catalog.Format("ReaderBusy", AccountsClient.Refusal(error));
            Raise();
            return false;
        }
    }

    private async Task ReloadAsync()
    {
        if (_reader is null)
            return;
        var enrolled = await _reader.EnrolledAsync().ConfigureAwait(true);
        Enrolled.Clear();
        foreach (var finger in Fingerprints.Fingers.Where(enrolled.Contains))
            Enrolled.Add(new FingerRow(finger));
        // A finger fprintd knows that this list does not, from a newer fprintd, still shows.
        foreach (var finger in enrolled.Where(finger => !Fingerprints.Fingers.Contains(finger)))
            Enrolled.Add(new FingerRow(finger));
        Available.Clear();
        foreach (var finger in Fingerprints.Fingers.Where(finger => !enrolled.Contains(finger)))
            Available.Add(new FingerRow(finger));
        _toEnroll = Available.FirstOrDefault()?.Finger;
        this.RaisePropertyChanged(nameof(ToEnroll));
        this.RaisePropertyChanged(nameof(HasNone));
        Raise();
    }

    public void Add()
    {
        Message = string.Empty;
        Choosing = true;
    }

    public async Task StartAsync()
    {
        if (_reader is null || _toEnroll is not { } finger)
            return;
        _passed = 0;
        Enrolling = true;
        Message = Strings.Catalog.Format("TouchReader", Strings.Finger(finger).ToLowerInvariant());
        RaiseProgress();
        try
        {
            _watch = await _reader.EnrollAsync(finger, (result, done) =>
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = OnStatusAsync(result, done))).ConfigureAwait(true);
        }
        catch (Tmds.DBus.Protocol.DBusExceptionBase error)
        {
            Message = AccountsClient.Refusal(error);
            Enrolling = false;
        }
    }

    /// <summary>One <c>EnrollStatus</c> from fprintd.</summary>
    internal async Task OnStatusAsync(string result, bool done)
    {
        if (result == "enroll-stage-passed")
        {
            _passed = Math.Min(_passed + 1, Math.Max(_stages, 1));
            RaiseProgress();
        }
        Message = Strings.EnrollResult(result);
        if (!done)
            return;
        if (result == "enroll-completed")
        {
            _passed = _stages;
            RaiseProgress();
        }
        await FinishEnrollAsync().ConfigureAwait(true);
    }

    /// <summary>Give up on the enrollment under way.</summary>
    public Task StopAsync() => FinishEnrollAsync();

    private async Task FinishEnrollAsync()
    {
        _watch?.Dispose();
        _watch = null;
        if (_enrolling && _reader is not null)
        {
            try
            {
                // fprintd wants EnrollStop after every enrollment, finished or not.
                await _reader.StopEnrollAsync().ConfigureAwait(true);
            }
            catch (Tmds.DBus.Protocol.DBusExceptionBase)
            {
            }
        }
        Enrolling = false;
        Choosing = false;
        await ReloadAsync().ConfigureAwait(true);
    }

    public async Task DeleteAsync()
    {
        if (_reader is null || _selected is not { } row)
            return;
        await Run(() => _reader.DeleteAsync(row.Finger)).ConfigureAwait(true);
    }

    public async Task DeleteAllAsync()
    {
        if (_reader is null)
            return;
        await Run(_reader.DeleteAllAsync).ConfigureAwait(true);
    }

    private async Task Run(Func<Task> change)
    {
        try
        {
            await change().ConfigureAwait(true);
            Message = string.Empty;
        }
        catch (Tmds.DBus.Protocol.DBusExceptionBase error)
        {
            Message = AccountsClient.Refusal(error);
        }
        await ReloadAsync().ConfigureAwait(true);
    }

    private void RaiseProgress()
    {
        this.RaisePropertyChanged(nameof(Progress));
        this.RaisePropertyChanged(nameof(ProgressValue));
    }

    private void Raise()
    {
        foreach (var name in new[] { nameof(CanAdd), nameof(CanDelete), nameof(CanDeleteAll), nameof(CanStart) })
            this.RaisePropertyChanged(name);
    }

    public async ValueTask DisposeAsync()
    {
        if (_enrolling)
            await StopAsync().ConfigureAwait(true);
        if (_reader is not null)
            await _reader.DisposeAsync().ConfigureAwait(true);
        _reader = null;
    }
}
