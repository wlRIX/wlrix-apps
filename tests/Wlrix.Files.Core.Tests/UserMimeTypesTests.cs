using Wlrix.Files.Core.Mime;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>The user's own type packages, and what the database makes of them once compiled.</summary>
public class UserMimeTypesTests
{
    [Fact]
    public void ChangedPatternsReplaceTheSystemsRatherThanAddingToThem()
    {
        var xml = UserMimeTypes.Render(new UserMimeType("text/plain", ["*.txt", "*.foo"], null));

        Assert.Contains("<glob-deleteall />", xml, StringComparison.Ordinal);
        Assert.Contains("<glob pattern=\"*.foo\" />", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("<comment", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void ADescriptionAloneLeavesThePatternsAlone()
    {
        var xml = UserMimeTypes.Render(new UserMimeType("text/plain", null, "Notes"));

        Assert.Contains("<comment>Notes</comment>", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("glob", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void APackageRoundTripsAndDeletingItReverts()
    {
        using var dir = new TemporaryDirectory();
        var types = new UserMimeTypes(dir.Path);
        var type = new UserMimeType("application/x-wlrix-test", ["*.wlx"], "Test file");

        types.Write(type);
        var read = types.Read(type.Type);

        Assert.NotNull(read);
        Assert.Equal(["*.wlx"], read.Patterns);
        Assert.Equal("Test file", read.Description);
        Assert.Single(types.All());
        Assert.True(types.Delete(type.Type));
        Assert.False(types.Has(type.Type));
    }

    [Fact]
    public void ACompiledDeleteAllDropsTheLowerDirectorysPatterns()
    {
        // What update-mime-database writes for <glob-deleteall/>, read the way the database
        // reads it: the user directory first.
        using var dir = new TemporaryDirectory();
        dir.File("user/globs2", "50:text/plain:__NOGLOBS__\n50:text/plain:*.foo\n");
        dir.File("user/types", "text/plain\n");
        dir.File("system/globs2", "50:text/plain:*.txt\n50:text/markdown:*.md\n");
        dir.File("system/types", "text/markdown\ntext/plain\n");

        var db = SharedMimeDatabase.Load([Path.Combine(dir.Path, "user"), Path.Combine(dir.Path, "system")]);

        Assert.Equal(["*.foo"], db.PatternsFor("text/plain"));
        Assert.Equal("text/plain", db.ResolveByName("a.foo"));
        Assert.Null(db.ResolveByName("a.txt"));
        Assert.Equal(["text/markdown", "text/plain"], db.AllTypes());
    }

    [Fact]
    public void AliasesAreFoundFromTheTypeTheyStandFor()
    {
        using var dir = new TemporaryDirectory();
        dir.File("mime/aliases", "text/x-markdown text/markdown\napplication/x-gzip application/gzip\n");

        var db = SharedMimeDatabase.Load([Path.Combine(dir.Path, "mime")]);

        Assert.Equal(["text/x-markdown"], db.AliasesFor("text/markdown"));
    }
}
