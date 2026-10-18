using ReactiveUI;
using Wlrix.NetworkPrompt.Localization;
using Wlrix.NetworkPrompt.Protocol;

namespace Wlrix.NetworkPrompt.ViewModels;

/// <summary>
/// The password dialog, or Join Other Network. One answer, then done: <see cref="Completion"/>
/// resolves with what was entered, or with null for Cancel.
/// </summary>
internal sealed class PromptViewModel : ViewModelBase
{
    /// <summary>The choices Join Other Network offers, in the order the list shows them.</summary>
    /// <remarks>
    /// WEP is not among them: it has been broken for twenty years, and a hidden network still
    /// using it is not one to make easy to join. A visible WEP network the daemon asks about still
    /// gets a password field.
    /// </remarks>
    internal static readonly Security[] OtherChoices = [Security.WpaPsk, Security.Sae, Security.None];

    private readonly TaskCompletionSource<PromptAnswer?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _ssid;
    private int _securityIndex;
    private string _password = string.Empty;
    private bool _showPassword;

    /// <summary>For the XAML designer.</summary>
    public PromptViewModel() : this(new PromptRequest(PromptMode.Password, "HomeNet", Security.WpaPsk, Retry: true))
    {
    }

    public PromptViewModel(PromptRequest request)
    {
        Request = request;
        _ssid = request.Ssid;
        _securityIndex = Math.Max(0, Array.IndexOf(OtherChoices, request.Security));
    }

    public PromptRequest Request { get; }

    /// <summary>Resolves when the dialog is answered: what was entered, or null for Cancel.</summary>
    public Task<PromptAnswer?> Completion => _completion.Task;

    public bool IsOther => Request.Mode == PromptMode.Other;

    public string Title => IsOther ? Strings.OtherTitle : Strings.WindowTitle;

    public string Heading => IsOther ? Strings.OtherHeading : Strings.PasswordHeading;

    public string Description => IsOther
        ? Strings.OtherDescription
        : Strings.Catalog.Format("PasswordDescription", Request.Ssid);

    /// <summary>Said when NetworkManager asked again because the last key did not work.</summary>
    public bool ShowRetry => Request.Retry;

    public string Ssid
    {
        get => _ssid;
        set
        {
            this.RaiseAndSetIfChanged(ref _ssid, value);
            this.RaisePropertyChanged(nameof(CanConnect));
        }
    }

    public IReadOnlyList<string> SecurityChoices { get; } = OtherChoices.Select(Strings.SecurityName).ToList();

    public int SecurityIndex
    {
        get => _securityIndex;
        set
        {
            this.RaiseAndSetIfChanged(ref _securityIndex, value);
            this.RaisePropertyChanged(nameof(Security));
            this.RaisePropertyChanged(nameof(AsksForPassword));
            Revalidate();
        }
    }

    /// <summary>The network's security: what was asked about, or what was chosen in the list.</summary>
    public Security Security => IsOther
        ? OtherChoices[Math.Clamp(_securityIndex, 0, OtherChoices.Length - 1)]
        : Request.Security;

    public bool AsksForPassword => Security != Security.None;

    public string Password
    {
        get => _password;
        set
        {
            this.RaiseAndSetIfChanged(ref _password, value);
            Revalidate();
        }
    }

    public bool ShowPassword
    {
        get => _showPassword;
        set
        {
            this.RaiseAndSetIfChanged(ref _showPassword, value);
            this.RaisePropertyChanged(nameof(MaskChar));
        }
    }

    public char MaskChar => _showPassword ? '\0' : '•';

    /// <summary>What is wrong with the key as typed, or empty. Nothing is said about an empty field.</summary>
    public string Problem => Keys.Check(Security, Password) switch
    {
        KeyProblem.TooShort => Strings.KeyTooShort,
        KeyProblem.TooLong => Strings.KeyTooLong,
        KeyProblem.NotAscii => Strings.KeyNotAscii,
        KeyProblem.WepLength => Strings.KeyWepLength,
        _ => string.Empty,
    };

    public bool HasProblem => Problem.Length > 0;

    public bool CanConnect =>
        Keys.Check(Security, Password) == KeyProblem.None && (!IsOther || Keys.NameFits(Ssid));

    public void Connect()
    {
        if (!CanConnect)
            return;
        var answer = new PromptAnswer(IsOther ? Ssid : Request.Ssid, Security, AsksForPassword ? Password : string.Empty);
        Password = string.Empty;
        _completion.TrySetResult(answer);
    }

    public void Cancel()
    {
        Password = string.Empty;
        _completion.TrySetResult(null);
    }

    private void Revalidate()
    {
        this.RaisePropertyChanged(nameof(Problem));
        this.RaisePropertyChanged(nameof(HasProblem));
        this.RaisePropertyChanged(nameof(CanConnect));
    }
}
