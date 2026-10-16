using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Wlrix.PolkitAgent.Services;
using Wlrix.PolkitAgent.ViewModels;
using Xunit;

namespace Wlrix.PolkitAgent.Tests;

/// <summary>The dialog's conversation with the helper, and the agent's queue, without a bus or a window.</summary>
public class AuthenticationTests
{
    private static readonly AgentRequest Request = new(
        "com.wlrix.settings.login.configure", "Authentication is required.", string.Empty,
        new Dictionary<string, string> { ["polkit.program"] = "/usr/bin/wlrix-login-helper" },
        "cookie-1", [new PolkitIdentity("unix-user", 1000)]);

    /// <summary>A helper that says <paramref name="script"/>, and records who was asked for and what was answered.</summary>
    private sealed class FakeHelper(params string[] scripts)
    {
        private int _next;
        public List<string> Users { get; } = [];
        public List<MemoryStream> Outputs { get; } = [];

        public Task<HelperSession> Start(string user, string cookie, CancellationToken _)
        {
            Users.Add(user);
            var output = new MemoryStream();
            Outputs.Add(output);
            return Task.FromResult(new HelperSession(new StringReader(scripts[_next++]), output));
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task TheRightPasswordAuthenticates()
    {
        var helper = new FakeHelper("PAM_PROMPT_ECHO_OFF Password: \nSUCCESS\n");
        var model = new AuthenticationViewModel(Request, ["vic"], 0, helper.Start);
        model.Start();
        await WaitFor(() => model.WaitingForAnswer);
        Assert.Equal("Password:", model.Prompt);

        model.Response = "hunter2";
        model.Submit();
        Assert.True(await model.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("hunter2\n", Encoding.UTF8.GetString(helper.Outputs[0].ToArray()));
        Assert.Empty(model.Response);
    }

    [Fact]
    public async Task AWrongPasswordAsksAgainInANewSession()
    {
        var helper = new FakeHelper(
            "PAM_PROMPT_ECHO_OFF Password: \nPAM_ERROR_MSG Authentication failure\nFAILURE\n",
            "PAM_PROMPT_ECHO_OFF Password: \nSUCCESS\n");
        var model = new AuthenticationViewModel(Request, ["vic"], 0, helper.Start);
        model.Start();
        await WaitFor(() => model.WaitingForAnswer);
        model.Response = "wrong";
        model.Submit();

        await WaitFor(() => helper.Users.Count == 2 && model.WaitingForAnswer);
        Assert.True(model.NoticeIsError);
        model.Response = "right";
        model.Submit();
        Assert.True(await model.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ARefusalBeforeAnyQuestionDoesNotLoop()
    {
        var helper = new FakeHelper("FAILURE\n", "FAILURE\n");
        var model = new AuthenticationViewModel(Request, ["vic"], 0, helper.Start);
        model.Start();
        await WaitFor(() => model.NoticeIsError);
        await Task.Delay(50);
        Assert.Single(helper.Users);
        Assert.False(model.Completion.IsCompleted);
    }

    [Fact]
    public async Task ChoosingSomeoneElseStartsTheirSession()
    {
        var helper = new FakeHelper("PAM_PROMPT_ECHO_OFF Password: \n", "PAM_PROMPT_ECHO_OFF Password: \nSUCCESS\n");
        var model = new AuthenticationViewModel(Request, ["root", "vic"], 1, helper.Start);
        model.Start();
        await WaitFor(() => model.WaitingForAnswer);
        model.SelectedUser = 0;
        await WaitFor(() => helper.Users.Count == 2 && model.WaitingForAnswer);
        Assert.Equal(["vic", "root"], helper.Users);
    }

    [Fact]
    public async Task CancelAnswersFalse()
    {
        var helper = new FakeHelper("PAM_PROMPT_ECHO_OFF Password: \nSUCCESS\n");
        var model = new AuthenticationViewModel(Request, ["vic"], 0, helper.Start);
        model.Start();
        await WaitFor(() => model.WaitingForAnswer);
        model.Cancel();
        Assert.False(await model.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class Presenter : IAuthenticationPresenter
    {
        public List<string> Shown { get; } = [];
        public TaskCompletionSource<bool> Answer { get; set; } = new();

        public async Task<bool> AuthenticateAsync(AgentRequest request, CancellationToken cancellationToken)
        {
            Shown.Add(request.Cookie);
            await using var _ = cancellationToken.Register(() => Answer.TrySetResult(false));
            return await Answer.Task;
        }
    }

    [Fact]
    public async Task RequestsArePutOneAtATime()
    {
        var presenter = new Presenter();
        var agent = new AuthenticationAgent(presenter, NullLogger<AuthenticationAgent>.Instance);
        var first = agent.RunAsync(Request, CancellationToken.None);
        var second = agent.RunAsync(Request with { Cookie = "cookie-2" }, CancellationToken.None);
        await WaitFor(() => presenter.Shown.Count == 1);
        await Task.Delay(50);
        Assert.Single(presenter.Shown);

        var answer = presenter.Answer;
        presenter.Answer = new TaskCompletionSource<bool>();
        answer.SetResult(true);
        Assert.True(await first);
        await WaitFor(() => presenter.Shown.Count == 2);
        presenter.Answer.SetResult(true);
        Assert.True(await second);
    }

    [Fact]
    public async Task ACanceledRequestStillInTheQueueIsNeverShown()
    {
        var presenter = new Presenter();
        var agent = new AuthenticationAgent(presenter, NullLogger<AuthenticationAgent>.Instance);
        var first = agent.RunAsync(Request, CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var second = agent.RunAsync(Request with { Cookie = "cookie-2" }, cancel.Token);
        await cancel.CancelAsync();
        Assert.False(await second);
        presenter.Answer.SetResult(true);
        await first;
        Assert.Equal(["cookie-1"], presenter.Shown);
    }
}
