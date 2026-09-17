using Wlrix.Common.Desktop;
using Xunit;

namespace Wlrix.Common.Tests;

public class ExecParserTests
{
    [Fact]
    public void WithNoFieldsEveryCodeIsStripped()
    {
        // The Toolchest's behavior, unchanged: a menu launches an application with
        // no document.
        var parsed = ExecParser.Parse("myapp %f --flag")!.Value;
        Assert.Equal("myapp", parsed.File);
        Assert.Equal(["--flag"], parsed.Args);
    }

    [Fact]
    public void SingularFTakesOnlyTheFirstPath()
    {
        // The spec is explicit: an entry with %f that is handed several files must be
        // launched once per file. ExpectsMultiple is how a caller finds that out.
        var parsed = ExecParser.Parse("viewer %f", ExecFields.ForPaths(["/a.txt", "/b.txt"]))!.Value;
        Assert.Equal("viewer", parsed.File);
        Assert.Equal(["/a.txt"], parsed.Args);
        Assert.False(ExecParser.ExpectsMultiple("viewer %f"));
    }

    [Fact]
    public void PluralFExpandsToOneArgumentPerFile()
    {
        var parsed = ExecParser.Parse("viewer %F", ExecFields.ForPaths(["/a.txt", "/b.txt", "/c.txt"]))!.Value;
        Assert.Equal(["/a.txt", "/b.txt", "/c.txt"], parsed.Args);
        Assert.True(ExecParser.ExpectsMultiple("viewer %F"));
    }

    [Fact]
    public void PathsBecomeFileUrisForTheUriCodes()
    {
        // An application declaring %u handles URIs; handing it a bare path would work
        // only by accident.
        var parsed = ExecParser.Parse("browser %u", ExecFields.ForPath("/home/vic/a b.html"))!.Value;
        Assert.Equal(["file:///home/vic/a%20b.html"], parsed.Args);
    }

    [Fact]
    public void ExplicitUrisAreUsedAsGivenRatherThanDerivedFromPaths()
    {
        var parsed = ExecParser.Parse("browser %U",
            new ExecFields(Paths: ["/local"], Uris: ["smb://host/share/x"]))!.Value;
        Assert.Equal(["smb://host/share/x"], parsed.Args);
    }

    [Fact]
    public void ACodeWithNothingToSubstituteProducesNoArgumentAtAll()
    {
        // Not an empty string: "viewer ''" is a request to open a file named nothing.
        var parsed = ExecParser.Parse("viewer %F", ExecFields.None)!.Value;
        Assert.Equal("viewer", parsed.File);
        Assert.Empty(parsed.Args);
    }

    [Fact]
    public void TheIconCodeExpandsToTwoArgumentsOrToNothing()
    {
        var withIcon = ExecParser.Parse("app %i", new ExecFields(Icon: "my-icon"))!.Value;
        Assert.Equal(["--icon", "my-icon"], withIcon.Args);

        var without = ExecParser.Parse("app %i", ExecFields.None)!.Value;
        Assert.Empty(without.Args);
    }

    [Fact]
    public void TheNameAndEntryPathCodesAreSubstituted()
    {
        var parsed = ExecParser.Parse("app %c %k",
            new ExecFields(DisplayName: "My App", EntryPath: "/usr/share/applications/a.desktop"))!.Value;
        Assert.Equal(["My App", "/usr/share/applications/a.desktop"], parsed.Args);
    }

    [Fact]
    public void AnEscapedPercentIsNotAFieldCode()
    {
        var parsed = ExecParser.Parse("app 100%%", ExecFields.ForPath("/x"))!.Value;
        Assert.Equal(["100%"], parsed.Args);
        // ...and "%%F" must not read as the list code.
        Assert.False(ExecParser.ExpectsMultiple("app %%F"));
    }

    [Fact]
    public void QuotingAndBackslashesFollowTheSpec()
    {
        var parsed = ExecParser.Parse("""/usr/bin/app "an argument" "with \"quotes\"" plain""")!.Value;
        Assert.Equal("/usr/bin/app", parsed.File);
        Assert.Equal(["an argument", "with \"quotes\"", "plain"], parsed.Args);
    }

    [Fact]
    public void APathWithSpacesSurvivesAsOneArgument()
    {
        // The reason substitution happens after tokenizing rather than by string
        // replacement: a textual swap would split this into two arguments.
        var parsed = ExecParser.Parse("viewer %f", ExecFields.ForPath("/home/vic/My Documents/a b.txt"))!.Value;
        Assert.Equal(["/home/vic/My Documents/a b.txt"], parsed.Args);
    }

    [Fact]
    public void ACodeJoinedToATokenKeepsTheArgumentIntact()
    {
        var parsed = ExecParser.Parse("app --file=%f", ExecFields.ForPath("/x.txt"))!.Value;
        Assert.Equal(["--file=/x.txt"], parsed.Args);
    }

    [Fact]
    public void DeprecatedCodesAreDropped()
    {
        // %d %D %n %N %v %m are deprecated; the spec says to ignore them.
        var parsed = ExecParser.Parse("app %d %n %v --real", ExecFields.ForPath("/x"))!.Value;
        Assert.Equal(["--real"], parsed.Args);
    }

    [Theory]
    [InlineData("viewer %f", true)]
    [InlineData("viewer %U", true)]
    [InlineData("viewer --no-files", false)]
    [InlineData("viewer %i %c", false)]
    public void AcceptsFilesRecognizesOnlyTheFileCodes(string exec, bool expected) =>
        Assert.Equal(expected, ExecParser.AcceptsFiles(exec));

    [Fact]
    public void AnEmptyExecParsesToNothingRatherThanThrowing()
    {
        Assert.Null(ExecParser.Parse(""));
        Assert.Null(ExecParser.Parse("   "));
        Assert.Null(ExecParser.Parse("%f", ExecFields.None));
    }
}
