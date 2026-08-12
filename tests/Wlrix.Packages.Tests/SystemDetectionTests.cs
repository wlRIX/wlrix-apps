using Xunit;

namespace Wlrix.Packages.Tests;

public class SystemDetectionTests
{
    [Fact]
    public void Parse_ReadsTheIdAndStripsTheShellQuotes()
    {
        var distribution = SystemDetection.Parse([
            """NAME="Arch Linux" """,
            """PRETTY_NAME="Arch Linux" """,
            "ID=arch",
        ]);

        Assert.Equal("arch", distribution.Id);
        Assert.Equal("Arch Linux", distribution.Name);
    }

    [Fact]
    public void Matches_RecognizesADerivativeThroughIdLike()
    {
        // This is what makes CachyOS, EndeavourOS and Manjaro work without being listed
        // anywhere: they declare ID_LIKE=arch and inherit pacman from it.
        var distribution = SystemDetection.Parse([
            """PRETTY_NAME="CachyOS" """,
            "ID=cachyos",
            "ID_LIKE=arch",
        ]);

        Assert.Equal("cachyos", distribution.Id);
        Assert.True(distribution.Matches("arch"));
        Assert.True(distribution.Matches("cachyos"));
        Assert.False(distribution.Matches("debian"));
    }

    [Fact]
    public void Matches_ReadsEveryEntryOfAMultiValuedIdLike()
    {
        // Linux Mint says ID_LIKE="ubuntu debian".
        var distribution = SystemDetection.Parse(["ID=linuxmint", """ID_LIKE="ubuntu debian" """]);

        Assert.True(distribution.Matches("debian"));
        Assert.True(distribution.Matches("ubuntu"));
    }

    [Fact]
    public void Parse_FallsBackToNameThenToTheIdWhenThereIsNoPrettyName()
    {
        Assert.Equal("Debian GNU/Linux",
            SystemDetection.Parse(["ID=debian", """NAME="Debian GNU/Linux" """]).Name);
        Assert.Equal("debian", SystemDetection.Parse(["ID=debian"]).Name);
    }

    [Fact]
    public void Parse_SkipsCommentsAndBlankLines()
    {
        var distribution = SystemDetection.Parse([
            "# a comment",
            string.Empty,
            "ID=opensuse-tumbleweed",
            "ID_LIKE=\"opensuse suse\"",
        ]);

        Assert.Equal("opensuse-tumbleweed", distribution.Id);
        Assert.True(distribution.Matches("suse"));
    }

    [Fact]
    public void Parse_AnswersUnknownForAFileWithNoIdAtAll() =>
        Assert.Equal("unknown", SystemDetection.Parse(["BUILD_ID=rolling"]).Id);
}
