using Wlrix.Icons;
using Xunit;

namespace Wlrix.Files.Core.Tests;

public class LegacySvgTests
{
    // The root of every IRIX export: the carpets, the mouse, the Writer.
    private const string IrixRoot = """<svg width="50pt" height="50pt" viewBox="0 0 50 50" xml:space="preserve"><g/></svg>""";

    [Fact]
    public void AnIrixExportIsInTheCarpetFrame()
    {
        Assert.True(LegacySvg.IsCarpetFrame(IrixRoot));
        Assert.True(LegacySvg.IsCarpetFrame(
            "<?xml version=\"1.0\"?>\n<!DOCTYPE svg PUBLIC \"x\" \"y\">\n<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox='0,0,50,50' height='50pt' width='50pt'>"));
    }

    [Theory]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 48 48"></svg>""")]
    [InlineData("""<svg width="50pt" height="50pt" viewBox="0 0 100 100"></svg>""")]
    [InlineData("""<svg width="50px" height="50px" viewBox="0 0 50 50"></svg>""")]
    [InlineData("""<svg viewBox="0 0 50 50"></svg>""")]
    [InlineData("not svg at all")]
    public void AnythingElseIsNot(string svg) => Assert.False(LegacySvg.IsCarpetFrame(svg));

    [Fact]
    public void TheNamespaceIsAddedOnlyWhenMissing()
    {
        var added = LegacySvg.WithNamespace(IrixRoot);
        Assert.StartsWith("""<svg xmlns="http://www.w3.org/2000/svg" width="50pt" """, added);
        Assert.Equal(added, LegacySvg.WithNamespace(added));
    }
}
