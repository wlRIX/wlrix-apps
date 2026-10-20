using Wlrix.BluetoothPrompt.Protocol;
using Wlrix.BluetoothPrompt.ViewModels;
using Xunit;

namespace Wlrix.BluetoothPrompt.Tests;

public class ProtocolTests
{
    [Fact]
    public void EachModeTheDaemonSendsIsRead()
    {
        // The lines wlrix-bluetooth's prompt.rs writes, exactly.
        var confirm = PairingRequest.Parse("""{"mode":"confirm","device":"Headset","passkey":"012345"}""");
        Assert.Equal(new PairingRequest(PairingMode.Confirm, "Headset", "012345", null), confirm);
        var display = PairingRequest.Parse("""{"mode":"display","device":"K380","code":"004321"}""");
        Assert.Equal("004321", display.Code);
        Assert.Equal(PairingMode.Pin, PairingRequest.Parse("""{"mode":"pin","device":"x"}""").Mode);
        var authorize = PairingRequest.Parse("""{"mode":"authorize","device":"Phone","service":"audio"}""");
        Assert.Equal("audio", authorize.Service);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"mode":"shout","device":"x"}""")]
    [InlineData("""{"mode":"pin"}""")]
    [InlineData("""{"mode":"confirm","device":"x"}""")]
    public void AnythingElseIsRefused(string line) =>
        Assert.Throws<FormatException>(() => PairingRequest.Parse(line));

    [Fact]
    public void AnswersAreTheShapeTheDaemonParses()
    {
        Assert.Equal("""{"accept":true}""", new PairingAnswer(Accept: true).ToJson());
        Assert.Equal("""{"pin":"0000"}""", new PairingAnswer(Pin: "0000").ToJson());
        Assert.Equal("""{"passkey":123456}""", new PairingAnswer(Passkey: 123456).ToJson());
    }

    [Fact]
    public void CodesAreCheckedAndGrouped()
    {
        Assert.Equal("012 345", Codes.Grouped("012345"));
        Assert.Equal("0000", Codes.Grouped("0000"));
        Assert.Equal(123456u, Codes.Passkey("123456"));
        Assert.Null(Codes.Passkey("1234567"));
        Assert.Null(Codes.Passkey("12a"));
        Assert.False(Codes.PinFits(""));
        Assert.False(Codes.PinFits(new string('1', 17)));
    }
}

public class PairingViewModelTests
{
    [Fact]
    public async Task ConfirmingAnswersYes()
    {
        var model = new PairingViewModel(new PairingRequest(PairingMode.Confirm, "Headset", "012345", null));
        Assert.True(model.ShowsCode);
        Assert.Equal("012 345", model.Code);
        model.Accept();
        Assert.Equal(new PairingAnswer(Accept: true), await model.Completion);
    }

    [Fact]
    public async Task APinIsAnsweredOnlyOnceItFits()
    {
        var model = new PairingViewModel(new PairingRequest(PairingMode.Pin, "Keyboard", "", null));
        Assert.False(model.CanAccept);
        model.Entry = "0000";
        Assert.True(model.CanAccept);
        model.Accept();
        Assert.Equal(new PairingAnswer(Pin: "0000"), await model.Completion);
    }

    [Fact]
    public async Task ADisplayHasNothingToAcceptAndClosingIsNoAnswer()
    {
        var model = new PairingViewModel(new PairingRequest(PairingMode.Display, "K380", "004321", null));
        Assert.False(model.HasAccept);
        model.Accept();
        Assert.False(model.Completion.IsCompleted);
        model.Cancel();
        Assert.Null(await model.Completion);
    }
}
