using Wlrix.NetworkPrompt.Protocol;
using Xunit;

namespace Wlrix.NetworkPrompt.Tests;

/// <summary>The exchange with <c>wlrix-network</c>, and what each kind of network takes as a key.</summary>
public class ProtocolTests
{
    [Fact]
    public void APasswordRequestNamesItsNetwork()
    {
        var request = PromptRequest.Parse("""{"mode":"password","ssid":"HomeNet","security":"sae","retry":true}""");
        Assert.Equal(PromptMode.Password, request.Mode);
        Assert.Equal("HomeNet", request.Ssid);
        Assert.Equal(Security.Sae, request.Security);
        Assert.True(request.Retry);
    }

    [Fact]
    public void JoinOtherNetworkNeedsNothingElse()
    {
        var request = PromptRequest.Parse("""{"mode":"other"}""");
        Assert.Equal(PromptMode.Other, request.Mode);
        Assert.Equal(string.Empty, request.Ssid);
        Assert.Equal(Security.WpaPsk, request.Security);
        Assert.False(request.Retry);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"mode":"shout"}""")]
    [InlineData("""{"mode":"password"}""")]
    [InlineData("""{"mode":"password","ssid":"x","security":"rot13"}""")]
    public void ANonsenseRequestIsRefused(string line) =>
        Assert.Throws<FormatException>(() => PromptRequest.Parse(line));

    [Fact]
    public void TheAnswerIsOneLineWhateverThePasswordHolds()
    {
        var json = new PromptAnswer("Café", Security.WpaPsk, "a\"b\nc").ToJson();
        Assert.DoesNotContain('\n', json);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("Café", document.RootElement.GetProperty("ssid").GetString());
        Assert.Equal("wpa-psk", document.RootElement.GetProperty("security").GetString());
        Assert.Equal("a\"b\nc", document.RootElement.GetProperty("password").GetString());
    }

    [Theory]
    [InlineData("password", nameof(KeyProblem.None))]
    [InlineData("short", nameof(KeyProblem.TooShort))]
    [InlineData("", nameof(KeyProblem.Empty))]
    [InlineData("pässword", nameof(KeyProblem.NotAscii))]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", nameof(KeyProblem.None))]
    public void WpaTakesEightToSixtyThreeCharactersOrTheRawKey(string key, string expected) =>
        Assert.Equal(Enum.Parse<KeyProblem>(expected), Keys.Check(Security.WpaPsk, key));

    [Fact]
    public void SixtyFourCharactersThatAreNotHexAreTooLong() =>
        Assert.Equal(KeyProblem.TooLong, Keys.Check(Security.WpaPsk, new string('z', 64)));

    [Fact]
    public void Wpa3TakesAnyPassword()
    {
        Assert.Equal(KeyProblem.None, Keys.Check(Security.Sae, "x"));
        Assert.Equal(KeyProblem.None, Keys.Check(Security.Sae, "パスワード"));
        Assert.Equal(KeyProblem.Empty, Keys.Check(Security.Sae, ""));
    }

    [Theory]
    [InlineData("abcde", nameof(KeyProblem.None))]
    [InlineData("0123456789", nameof(KeyProblem.None))]
    [InlineData("abcd", nameof(KeyProblem.WepLength))]
    [InlineData("zzzzzzzzzz", nameof(KeyProblem.WepLength))]
    public void WepTakesItsTwoKeySizes(string key, string expected) =>
        Assert.Equal(Enum.Parse<KeyProblem>(expected), Keys.Check(Security.Wep, key));

    [Fact]
    public void AnOpenNetworkNeedsNoKey() => Assert.Equal(KeyProblem.None, Keys.Check(Security.None, ""));

    [Fact]
    public void ANameIsAtMostThirtyTwoBytes()
    {
        Assert.True(Keys.NameFits(new string('a', 32)));
        Assert.False(Keys.NameFits(new string('a', 33)));
        // Eleven three-byte characters is 33 bytes, though only eleven long.
        Assert.False(Keys.NameFits(new string('無', 11)));
        Assert.False(Keys.NameFits(string.Empty));
    }
}
