using Wlrix.Files.Core.State;
using Wlrix.Files.Services;
using Xunit;

namespace Wlrix.Files.Tests;

/// <summary>
/// <c>files.toml</c>, which <c>wlrix-settings-daemon</c> writes and this reads.
/// </summary>
/// <remarks>
/// The strictness is the subject. The daemon validates a candidate file by running
/// <c>wlrix-files --check-config</c> on it before renaming it into place, so what this accepts
/// is exactly what a settings panel is allowed to write — and what it rejects is what the gate
/// is for.
/// </remarks>
public class FilesConfigTests
{
    private static FilesConfig Parse(string text)
    {
        Assert.True(FilesConfig.Parse(text, out var config, out var problem), problem);
        return config;
    }

    private static string Reject(string text)
    {
        Assert.False(FilesConfig.Parse(text, out _, out var problem));
        Assert.NotNull(problem);
        return problem!;
    }

    [Fact]
    public void AnEmptyFileIsTheDefaults()
    {
        var config = Parse("");
        Assert.Equal(NavigationMode.Modern, config.NavigationMode);
        Assert.Equal("Adwaita", config.IconTheme);
    }

    [Fact]
    public void TheTwoSettingsAreRead()
    {
        var config = Parse("""
            [navigation]
            mode = "classic"

            [appearance]
            icon_theme = "Papirus"
            """);

        Assert.Equal(NavigationMode.Classic, config.NavigationMode);
        Assert.Equal("Papirus", config.IconTheme);
    }

    [Fact]
    public void OneSectionWithoutTheOtherLeavesTheRestAtItsDefault()
    {
        // The daemon's Reset deletes a key rather than writing the default, so a file with one
        // section in it is the normal shape rather than an odd one.
        Assert.Equal("Adwaita", Parse("[navigation]\nmode = \"classic\"").IconTheme);
        Assert.Equal(NavigationMode.Modern, Parse("[appearance]\nicon_theme = \"Papirus\"").NavigationMode);
    }

    [Fact]
    public void TheModeIsReadWhateverCaseItIsWrittenIn()
    {
        Assert.Equal(NavigationMode.Classic, Parse("[navigation]\nmode = \"Classic\"").NavigationMode);
        Assert.Equal(NavigationMode.Classic, Parse("[navigation]\nmode = \"CLASSIC\"").NavigationMode);
    }

    [Fact]
    public void AnEmptyIconThemeIsAValueRatherThanAnOmission()
    {
        // "no named theme" is a real answer: hicolor and pixmaps only, which is what the icon
        // theme code does with an empty string.
        Assert.Equal(string.Empty, Parse("[appearance]\nicon_theme = \"\"").IconTheme);
    }

    // --- what the validation gate is for -------------------------------------

    [Fact]
    public void AKeyThisVersionDoesNotKnowIsRefused()
    {
        // Matching the deny_unknown_fields every Rust component here uses. Accepting it would
        // mean a settings panel could write a key that silently does nothing, which is exactly
        // what the gate exists to catch.
        Assert.Contains("unknown key", Reject("nonsense = 1"));
        Assert.Contains("navigation.speed", Reject("[navigation]\nspeed = 2"));
        Assert.Contains("appearance.palette", Reject("[appearance]\npalette = \"gotham\""));
    }

    [Fact]
    public void AModeThatIsNotOneOfTheTwoIsRefused()
    {
        var problem = Reject("[navigation]\nmode = \"sideways\"");
        Assert.Contains("navigation.mode", problem);
        Assert.Contains("sideways", problem);
    }

    [Fact]
    public void AValueOfTheWrongTypeIsRefusedAndSaysWhatItWas()
    {
        Assert.Contains("expected a string", Reject("[appearance]\nicon_theme = 3"));
        Assert.Contains("expected \"modern\"", Reject("[navigation]\nmode = true"));
    }

    [Fact]
    public void TextThatIsNotTomlIsRefusedWithTheLineItFailedOn()
    {
        // Tomlyn's first line carries the line and column, which is most of what makes a
        // config error actionable.
        var problem = Reject("this is not = = toml");
        Assert.Contains("(1,", problem);
    }

    // --- the two entry points differ on purpose ------------------------------

    [Fact]
    public void LoadingABrokenFileFallsBackToDefaultsWhereCheckingItDoesNot()
    {
        // A file manager that would not open because a config file had a typo in it is worse
        // than one that opens with the defaults. The daemon asks the other question — "would
        // you accept this?" — and the answer there has to be no.
        using var temp = new TemporaryFile("""
            [navigation]
            mode = "sideways"
            """);

        Assert.Equal(NavigationMode.Modern, FilesConfig.Load(temp.Path).NavigationMode);
        Assert.NotNull(FilesConfig.Check(temp.Path));
    }

    [Fact]
    public void CheckingAGoodFileAnswersNothing()
    {
        using var temp = new TemporaryFile("[navigation]\nmode = \"classic\"\n");
        Assert.Null(FilesConfig.Check(temp.Path));
    }

    [Fact]
    public void CheckingAFileThatIsNotThereIsAProblemRatherThanSilence()
    {
        // The daemon hands a path it has just written. If it is not readable, saying so beats
        // approving a file nobody looked at.
        Assert.NotNull(FilesConfig.Check(Path.Combine(Path.GetTempPath(), "wlrix-no-such-file.toml")));
    }

    [Fact]
    public void AMissingFileIsSimplyTheDefaults()
    {
        Assert.Equal(
            NavigationMode.Modern,
            FilesConfig.Load(Path.Combine(Path.GetTempPath(), "wlrix-no-such-file.toml")).NavigationMode);
    }

    private sealed class TemporaryFile : IDisposable
    {
        public TemporaryFile(string contents)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"wlrix-files-config-{Guid.NewGuid():N}.toml");
            File.WriteAllText(Path, contents);
        }

        public string Path { get; }

        public void Dispose() => File.Delete(Path);
    }
}
