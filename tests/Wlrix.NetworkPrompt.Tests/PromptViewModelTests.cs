using Wlrix.NetworkPrompt.Protocol;
using Wlrix.NetworkPrompt.ViewModels;
using Xunit;

namespace Wlrix.NetworkPrompt.Tests;

public class PromptViewModelTests
{
    [Fact]
    public async Task ConnectAnswersWithThePasswordAndClearsTheField()
    {
        var model = new PromptViewModel(new PromptRequest(PromptMode.Password, "HomeNet", Security.WpaPsk, false));
        Assert.False(model.CanConnect, "an empty password cannot be right");
        model.Password = "correct horse";
        Assert.True(model.CanConnect);
        model.Connect();
        var answer = await model.Completion;
        Assert.Equal(new PromptAnswer("HomeNet", Security.WpaPsk, "correct horse"), answer);
        Assert.Equal(string.Empty, model.Password);
    }

    [Fact]
    public async Task CancelAnswersWithNothing()
    {
        var model = new PromptViewModel(new PromptRequest(PromptMode.Password, "HomeNet", Security.WpaPsk, true));
        model.Password = "half typed";
        model.Cancel();
        Assert.Null(await model.Completion);
        Assert.Equal(string.Empty, model.Password);
    }

    [Fact]
    public void ATooShortKeySaysWhyAndCannotBeSent()
    {
        var model = new PromptViewModel(new PromptRequest(PromptMode.Password, "HomeNet", Security.WpaPsk, false))
        {
            Password = "abc",
        };
        Assert.True(model.HasProblem);
        Assert.False(model.CanConnect);
        model.Connect();
        Assert.False(model.Completion.IsCompleted);
    }

    [Fact]
    public async Task JoinOtherNetworkTakesTheNameAndTheChosenSecurity()
    {
        var model = new PromptViewModel(new PromptRequest(PromptMode.Other, "", Security.WpaPsk, false));
        Assert.True(model.IsOther);
        model.Password = "longenough";
        Assert.False(model.CanConnect, "no name yet");
        model.Ssid = "Hidden";
        model.SecurityIndex = Array.IndexOf(PromptViewModel.OtherChoices, Security.None);
        Assert.False(model.AsksForPassword);
        Assert.True(model.CanConnect);
        model.Connect();
        Assert.Equal(new PromptAnswer("Hidden", Security.None, string.Empty), await model.Completion);
    }
}
