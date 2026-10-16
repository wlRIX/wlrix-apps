using System.Text;
using ReactiveUI;
using Wlrix.PolkitAgent.Localization;
using Wlrix.PolkitAgent.Services;

namespace Wlrix.PolkitAgent.ViewModels;

/// <summary>
/// One authentication dialog: the conversation with the helper behind it, and what the dialog
/// shows of it.
///
/// The helper asks; the dialog shows its question and waits for the person; the answer goes back
/// and the helper asks again or finishes. A wrong password ends that helper session, so a retry
/// starts a new one for the same cookie -- polkit keeps the request open until this agent answers
/// it, which happens only on success or Cancel. Choosing another person does the same.
/// </summary>
internal sealed class AuthenticationViewModel : ViewModelBase
{
    /// <summary>Starts a helper conversation; the real one is <see cref="HelperSession.StartAsync"/>.</summary>
    internal delegate Task<HelperSession> SessionFactory(string user, string cookie, CancellationToken cancellationToken);

    private readonly AgentRequest _request;
    private readonly SessionFactory _start;
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _conversation;
    private TaskCompletionSource<string>? _answer;

    private int _selectedUser;
    private string _prompt = Strings.PasswordPrompt;
    private bool _echo;
    private string _response = string.Empty;
    private string _notice = string.Empty;
    private bool _noticeIsError;
    private bool _waitingForAnswer;

    /// <summary>For the XAML designer.</summary>
    public AuthenticationViewModel() : this(
        new AgentRequest("org.example.action", "Authentication is required to change the login screen's settings.",
            string.Empty, new Dictionary<string, string> { ["polkit.program"] = "/usr/bin/wlrix-login-helper" },
            "cookie", [new PolkitIdentity("unix-user", 0)]),
        ["root", "vic"], 1, static (_, _, _) => Task.FromException<HelperSession>(new NotSupportedException()))
    {
    }

    public AuthenticationViewModel(AgentRequest request, IReadOnlyList<string> users, int selected, SessionFactory start)
    {
        _request = request;
        _start = start;
        Users = users;
        _selectedUser = selected;
        Details = DescribeDetails(request);
    }

    /// <summary>Resolves when polkit has its answer: true authenticated, false canceled.</summary>
    public Task<bool> Completion => _completion.Task;

    /// <summary>polkit's message for the action, in the session's language.</summary>
    public string Message => _request.Message;

    /// <summary>The action and what the caller attached, for the Details expander.</summary>
    public string Details { get; }

    /// <summary>Everyone polkit will take a password from.</summary>
    public IReadOnlyList<string> Users { get; }

    /// <summary>The user list is worth showing only when there is a choice to make.</summary>
    public bool ShowUsers => Users.Count > 1;

    /// <summary>The single person, named in the prompt when there is no list to pick from.</summary>
    public string OnlyUser => Users.Count == 1 ? Strings.AuthenticatingAs(Users[0]) : string.Empty;

    public int SelectedUser
    {
        get => _selectedUser;
        set
        {
            if (value == _selectedUser || value < 0)
                return;
            this.RaiseAndSetIfChanged(ref _selectedUser, value);
            Notice = string.Empty;
            _ = ConverseAsync();
        }
    }

    /// <summary>What PAM asked, usually "Password:".</summary>
    public string Prompt
    {
        get => _prompt;
        private set => this.RaiseAndSetIfChanged(ref _prompt, value);
    }

    /// <summary>Whether the answer may be shown as typed. Passwords are not.</summary>
    public bool Echo
    {
        get => _echo;
        private set
        {
            this.RaiseAndSetIfChanged(ref _echo, value);
            this.RaisePropertyChanged(nameof(MaskChar));
        }
    }

    public char MaskChar => _echo ? '\0' : '•';

    /// <summary>What the person has typed. Cleared the moment it is sent.</summary>
    public string Response
    {
        get => _response;
        set => this.RaiseAndSetIfChanged(ref _response, value);
    }

    /// <summary>The last thing PAM said, or why the last attempt failed.</summary>
    public string Notice
    {
        get => _notice;
        private set => this.RaiseAndSetIfChanged(ref _notice, value);
    }

    public bool NoticeIsError
    {
        get => _noticeIsError;
        private set => this.RaiseAndSetIfChanged(ref _noticeIsError, value);
    }

    /// <summary>True while a question is on screen, which is the only time Authenticate does anything.</summary>
    public bool WaitingForAnswer
    {
        get => _waitingForAnswer;
        private set => this.RaiseAndSetIfChanged(ref _waitingForAnswer, value);
    }

    /// <summary>Begins asking. Called once the dialog is up.</summary>
    public void Start() => _ = ConverseAsync();

    /// <summary>Sends what was typed as the answer to the question on screen.</summary>
    public void Submit()
    {
        if (!WaitingForAnswer || _answer is not { } answer)
            return;
        WaitingForAnswer = false;
        var response = Response;
        Response = string.Empty;
        answer.TrySetResult(response);
    }

    /// <summary>The person gave up, or polkit withdrew the request.</summary>
    public void Cancel()
    {
        _conversation?.Cancel();
        _answer?.TrySetCanceled();
        _completion.TrySetResult(false);
    }

    /// <summary>
    /// One helper conversation, for the person selected now. Starting another (a different person
    /// chosen, or a retry) ends this one first.
    /// </summary>
    private async Task ConverseAsync()
    {
        _conversation?.Cancel();
        _answer?.TrySetCanceled();
        var conversation = new CancellationTokenSource();
        _conversation = conversation;
        var token = conversation.Token;
        WaitingForAnswer = false;

        var user = Users.Count > 0 ? Users[_selectedUser] : Environment.UserName;
        var answered = false;
        try
        {
            await using var session = await _start(user, _request.Cookie, token).ConfigureAwait(true);
            while (!token.IsCancellationRequested)
            {
                switch (await session.ReadAsync(token).ConfigureAwait(true))
                {
                    case HelperMessage.Prompt prompt:
                        Prompt = prompt.Text.Trim();
                        Echo = prompt.Echo;
                        _answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                        WaitingForAnswer = true;
                        var response = await _answer.Task.WaitAsync(token).ConfigureAwait(true);
                        answered = true;
                        await session.RespondAsync(response, token).ConfigureAwait(true);
                        break;

                    case HelperMessage.Info info:
                        Say(info.Text, error: false);
                        break;

                    case HelperMessage.Error error:
                        Say(error.Text, error: true);
                        break;

                    case HelperMessage.Done { Success: true }:
                        _completion.TrySetResult(true);
                        return;

                    case HelperMessage.Done:
                        if (token.IsCancellationRequested)
                            return;
                        if (!answered)
                        {
                            // Refused before anything was asked: the helper could not be reached,
                            // or PAM would not start. Another round would refuse the same way, so
                            // say so and leave Cancel as the way out.
                            Say(Strings.CouldNotAuthenticate, error: true);
                            return;
                        }

                        Say(Strings.TryAgain, error: true);
                        Wlrix.Common.SystemSound.Play("dialog-error");
                        _ = ConverseAsync();
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by another conversation, or the dialog was canceled.
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.ComponentModel.Win32Exception)
        {
            if (!token.IsCancellationRequested)
                Say(Strings.HelperUnavailable(ex.Message), error: true);
        }
    }

    private void Say(string text, bool error)
    {
        Notice = text;
        NoticeIsError = error;
    }

    private static string DescribeDetails(AgentRequest request)
    {
        var text = new StringBuilder();
        text.Append(Strings.ActionLabel).Append(' ').Append(request.ActionId);
        foreach (var (key, value) in request.Details.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            text.Append('\n').Append(key).Append(": ").Append(value);
        return text.ToString();
    }
}
