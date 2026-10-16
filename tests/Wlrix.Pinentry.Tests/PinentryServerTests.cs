using Wlrix.Pinentry.Services;
using Wlrix.Pinentry.ViewModels;
using Xunit;

namespace Wlrix.Pinentry.Tests;

/// <summary>The Assuan conversation, as gpg-agent holds it with GnuPG's own pinentry.</summary>
public class PinentryServerTests
{
    private sealed class FakeUi : IPinentryUi
    {
        public PinResult Pin { get; init; } = new(DialogOutcome.Ok, "secret");
        public DialogOutcome Confirm { get; init; } = DialogOutcome.Ok;
        public List<DialogText> Asked { get; } = [];
        public List<bool> OneButton { get; } = [];

        public Task<PinResult> GetPinAsync(DialogText text)
        {
            Asked.Add(text);
            return Task.FromResult(Pin);
        }

        public Task<DialogOutcome> ConfirmAsync(DialogText text, bool oneButton)
        {
            Asked.Add(text);
            OneButton.Add(oneButton);
            return Task.FromResult(Confirm);
        }
    }

    private static async Task<string[]> Converse(FakeUi ui, params string[] commands)
    {
        var output = new StringWriter { NewLine = "\n" };
        await new PinentryServer(new StringReader(string.Join("\n", commands) + "\n"), output, ui).RunAsync();
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public async Task ItGreetsAndSaysGoodbye()
    {
        var lines = await Converse(new FakeUi(), "BYE");
        Assert.Equal(["OK Pleased to meet you", "OK closing connection"], lines);
    }

    [Fact]
    public async Task APassphraseIsSentAsData()
    {
        var ui = new FakeUi { Pin = new PinResult(DialogOutcome.Ok, "a%b\nc") };
        var lines = await Converse(ui, "SETDESC Unlock%0Athe key", "SETPROMPT Passphrase:", "GETPIN", "BYE");
        Assert.Equal(["OK Pleased to meet you", "OK", "OK", "D a%25b%0Ac", "OK", "OK closing connection"], lines);
        Assert.Equal("Unlock\nthe key", ui.Asked[0].Description);
        Assert.Equal("Passphrase:", ui.Asked[0].Prompt);
    }

    [Fact]
    public async Task AnEmptyPassphraseSendsNoData()
    {
        var lines = await Converse(new FakeUi { Pin = new PinResult(DialogOutcome.Ok, "") }, "GETPIN");
        Assert.Equal(["OK Pleased to meet you", "OK"], lines);
    }

    [Fact]
    public async Task CancelIsGnuPGsCanceledError()
    {
        var lines = await Converse(new FakeUi { Pin = new PinResult(DialogOutcome.Canceled) }, "GETPIN");
        Assert.Equal("ERR 83886179 Operation cancelled <Pinentry>", lines[^1]);
    }

    [Fact]
    public async Task ARepeatedPassphraseIsAnnounced()
    {
        var lines = await Converse(new FakeUi { Pin = new PinResult(DialogOutcome.Ok, "x", Repeated: true) },
            "SETREPEAT Repeat:", "GETPIN");
        Assert.Equal(["S PIN_REPEATED", "D x", "OK"], lines[^3..]);
    }

    [Fact]
    public async Task TheAgentsDefaultsFillInWhatWasNotSet()
    {
        var ui = new FakeUi();
        await Converse(ui, "OPTION default-ok=_OK", "OPTION default-cancel=_Cancel", "OPTION default-prompt=PIN:",
            "SETCANCEL Abbrechen", "GETPIN");
        Assert.Equal("_OK", ui.Asked[0].Ok);
        Assert.Equal("Abbrechen", ui.Asked[0].Cancel);
        Assert.Equal("PIN:", ui.Asked[0].Prompt);
    }

    [Fact]
    public async Task AnErrorIsShownForOneAttempt()
    {
        var ui = new FakeUi();
        await Converse(ui, "SETERROR Bad Passphrase (try 2 of 3)", "GETPIN", "GETPIN");
        Assert.Equal("Bad Passphrase (try 2 of 3)", ui.Asked[0].Error);
        Assert.Null(ui.Asked[1].Error);
    }

    [Theory]
    [InlineData(0, "OK")]
    [InlineData(1, "ERR 83886194 Not confirmed <Pinentry>")]
    [InlineData(2, "ERR 83886179 Operation cancelled <Pinentry>")]
    public async Task ConfirmAnswers(int outcome, string reply)
    {
        // Ok, NotOk, Canceled, by value: the enum is internal to the app.
        var lines = await Converse(new FakeUi { Confirm = (DialogOutcome)outcome }, "CONFIRM");
        Assert.Equal(reply, lines[^1]);
    }

    [Fact]
    public async Task MessageAndOneButtonConfirmHaveOneButton()
    {
        var ui = new FakeUi();
        await Converse(ui, "MESSAGE", "CONFIRM --one-button", "CONFIRM");
        Assert.Equal([true, true, false], ui.OneButton);
    }

    [Fact]
    public async Task GetInfoAnswersWhatGpgAgentAsks()
    {
        var lines = await Converse(new FakeUi(), "GETINFO flavor", "GETINFO pid", "GETINFO nonsense");
        Assert.Equal("D wlrix", lines[1]);
        Assert.Equal($"D {Environment.ProcessId}", lines[3]);
        Assert.StartsWith("ERR 83886360", lines[^1]);
    }

    [Fact]
    public async Task UnknownCommandsAreRefusedAndOptionalOnesAccepted()
    {
        var lines = await Converse(new FakeUi(), "FROBNICATE", "SETKEYINFO n/ABC", "SETQUALITYBAR", "OPTION ttyname=/dev/pts/1");
        Assert.Equal(["OK Pleased to meet you", "ERR 83886355 Unknown IPC command <Pinentry>", "OK", "OK", "OK"], lines);
    }

    [Fact]
    public async Task ResetForgetsTheText()
    {
        var ui = new FakeUi();
        await Converse(ui, "SETDESC first", "RESET", "GETPIN");
        Assert.Null(ui.Asked[0].Description);
    }

    [Theory]
    [InlineData("caf%C3%A9", "café")]
    [InlineData("100%25", "100%")]
    [InlineData("no escapes", "no escapes")]
    [InlineData("trailing %", "trailing %")]
    public void ArgumentsAreUnescaped(string escaped, string text) =>
        Assert.Equal(text, PinentryServer.Unescape(escaped));

    [Theory]
    [InlineData("_OK", "OK")]
    [InlineData("キャンセル(_C)", "キャンセル(C)")]
    [InlineData("snake__case", "snake_case")]
    public void MnemonicsAreDropped(string label, string shown) =>
        Assert.Equal(shown, PinentryViewModel.Label(label));

    [Fact]
    public async Task MismatchedRepeatsAskAgain()
    {
        var model = new PinentryViewModel(new DialogText { Repeat = "Repeat:" }, DialogKind.Pin)
        {
            Pin = "one",
            RepeatPin = "two",
        };
        model.Ok();
        Assert.False(model.Completion.IsCompleted);
        Assert.True(model.HasError);
        Assert.Empty(model.Pin);

        model.Pin = "same";
        model.RepeatPin = "same";
        model.Ok();
        var result = await model.Completion;
        Assert.Equal(new PinResult(DialogOutcome.Ok, "same", Repeated: true), result);
    }

    [Fact]
    public async Task ATimeoutAnswersOnItsOwn()
    {
        var model = new PinentryViewModel(new DialogText { Timeout = 1 }, DialogKind.Pin);
        var result = await model.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DialogOutcome.TimedOut, result.Outcome);
    }
}
