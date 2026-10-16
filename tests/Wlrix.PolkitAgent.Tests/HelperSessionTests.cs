using System.Text;
using Wlrix.PolkitAgent.Services;
using Xunit;

namespace Wlrix.PolkitAgent.Tests;

/// <summary>The helper protocol, as polkit's own PolkitAgentSession speaks it.</summary>
public class HelperSessionTests
{
    [Fact]
    public async Task TheSocketIsToldTheUserThenTheCookie()
    {
        var output = new MemoryStream();
        await using var session = new HelperSession(new StringReader(string.Empty), output);
        await session.SendAsync("vic", "1234-cookie", CancellationToken.None);
        Assert.Equal("vic\n1234-cookie\n", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task ASpawnedHelperIsToldOnlyTheCookie()
    {
        var output = new MemoryStream();
        await using var session = new HelperSession(new StringReader(string.Empty), output);
        await session.SendAsync(user: null, "1234-cookie", CancellationToken.None);
        Assert.Equal("1234-cookie\n", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task AnAnswerIsOneLine()
    {
        var output = new MemoryStream();
        await using var session = new HelperSession(new StringReader(string.Empty), output);
        await session.RespondAsync("hunter2", CancellationToken.None);
        Assert.Equal("hunter2\n", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Theory]
    [InlineData("PAM_PROMPT_ECHO_OFF Password: ", "Password: ", false)]
    [InlineData("PAM_PROMPT_ECHO_ON Login: ", "Login: ", true)]
    public void PromptsAreRead(string line, string text, bool echo) =>
        Assert.Equal(new HelperMessage.Prompt(text, echo), HelperSession.Parse(line));

    [Fact]
    public void NoticesAreRead()
    {
        Assert.Equal(new HelperMessage.Info("Hello"), HelperSession.Parse("PAM_TEXT_INFO Hello"));
        Assert.Equal(new HelperMessage.Error("Bad"), HelperSession.Parse("PAM_ERROR_MSG Bad"));
    }

    [Theory]
    [InlineData("SUCCESS", true)]
    [InlineData("FAILURE", false)]
    [InlineData("something else entirely", false)]
    public void TheEndIsRead(string line, bool success) =>
        Assert.Equal(new HelperMessage.Done(success), HelperSession.Parse(line));

    [Fact]
    public async Task TheHelperGoingAwayIsAFailure()
    {
        await using var session = new HelperSession(new StringReader(string.Empty), new MemoryStream());
        Assert.Equal(new HelperMessage.Done(false), await session.ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(@"plain", "plain")]
    [InlineData(@"two\nlines", "two\nlines")]
    [InlineData(@"tab\there", "tab\there")]
    [InlineData(@"quote \""x\"" and \\", "quote \"x\" and \\")]
    [InlineData(@"octal \101\60", "octal A0")]
    [InlineData(@"\q is q", "q is q")]
    public void LinesAreUnescapedAsGLibDoes(string escaped, string text) =>
        Assert.Equal(text, HelperSession.Unescape(escaped));
}
