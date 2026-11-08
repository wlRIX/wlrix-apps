using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>Cases taken from what <c>fnmatch(3)</c> answers, which is what GTK's chooser uses.</summary>
public class GlobTests
{
    [Theory]
    [InlineData("*.png", "photo.png", true)]
    [InlineData("*.png", "photo.png.bak", false)]
    [InlineData("*", "", true)]
    [InlineData("*", "anything", true)]
    [InlineData("", "", true)]
    [InlineData("", "a", false)]
    [InlineData("a*b*c", "aXbYbZc", true)]
    [InlineData("a*b*c", "aXbYbZ", false)]
    [InlineData("?.txt", "a.txt", true)]
    [InlineData("?.txt", ".txt", false)]
    [InlineData("README*", "README.md", true)]
    [InlineData("*~", "draft.txt~", true)]
    public void StarsAndQuestionMarks(string pattern, string name, bool expected) =>
        Assert.Equal(expected, Glob.IsMatch(pattern, name));

    [Theory]
    [InlineData("*.[pP][nN][gG]", "a.png", true)]
    [InlineData("*.[pP][nN][gG]", "a.PnG", true)]
    [InlineData("*.[pP][nN][gG]", "a.pnx", false)]
    [InlineData("*.[1-9]", "ls.1", true)]
    [InlineData("*.[1-9]", "ls.0", false)]
    [InlineData("*.so.[0-9]*", "libc.so.6", true)]
    [InlineData("*.so.[0-9]*", "libc.so.x", false)]
    [InlineData("*.anim[1-9j]", "a.animj", true)]
    [InlineData("[!a]*", "b", true)]
    [InlineData("[!a]*", "a", false)]
    [InlineData("[^a]*", "a", false)]
    [InlineData("[]]", "]", true)]
    [InlineData("[!]]", "]", false)]
    [InlineData("[a-]", "-", true)]
    public void BracketExpressions(string pattern, string name, bool expected) =>
        Assert.Equal(expected, Glob.IsMatch(pattern, name));

    [Theory]
    [InlineData("[abc", "[abc", true)]
    [InlineData("[abc", "a", false)]
    [InlineData(@"\*.txt", "*.txt", true)]
    [InlineData(@"\*.txt", "a.txt", false)]
    [InlineData(@"[\]]", "]", true)]
    public void UnclosedBracketsAndEscapesAreLiteral(string pattern, string name, bool expected) =>
        Assert.Equal(expected, Glob.IsMatch(pattern, name));

    [Theory]
    [InlineData("*.jpg", "IMG_0001.JPG")]
    [InlineData("*.[a-z]", "x.Q")]
    [InlineData("*.[A-Z]", "x.q")]
    public void CaseCanBeIgnored(string pattern, string name)
    {
        Assert.False(Glob.IsMatch(pattern, name));
        Assert.True(Glob.IsMatch(pattern, name, ignoreCase: true));
    }
}
