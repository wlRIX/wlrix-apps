using Wlrix.Files.Core.Operations;
using Xunit;

namespace Wlrix.Files.Core.Tests;

public class ConflictNamingTests
{
    [Theory]
    [InlineData("notes.txt", "notes (copy).txt")]
    [InlineData("archive.tar.gz", "archive.tar (copy).gz")]
    [InlineData("README", "README (copy)")]
    // A leading dot is part of the name, not an extension: .bashrc has none.
    [InlineData(".bashrc", ".bashrc (copy)")]
    public void TheSuffixGoesBeforeTheExtension(string name, string expected) =>
        Assert.Equal(expected, ConflictNaming.NextName(name));

    [Fact]
    public void RepeatedCopiesCountRatherThanNest()
    {
        // Without this, three collisions produce "notes (copy) (copy) (copy).txt".
        Assert.Equal("notes (copy 2).txt", ConflictNaming.NextName("notes (copy).txt"));
        Assert.Equal("notes (copy 3).txt", ConflictNaming.NextName("notes (copy 2).txt"));
        Assert.Equal("notes (copy 11).txt", ConflictNaming.NextName("notes (copy 10).txt"));
    }

    [Fact]
    public void ANameThatMerelyLooksLikeACopyIsNotMisread()
    {
        // The user's own parentheses are theirs; only the exact pattern this method
        // produces is treated as one of its own.
        Assert.Equal("report (final) (copy).txt", ConflictNaming.NextName("report (final).txt"));
        Assert.Equal("notes (copy of a) (copy).txt", ConflictNaming.NextName("notes (copy of a).txt"));
        Assert.Equal("x (copy 0) (copy).txt", ConflictNaming.NextName("x (copy 0).txt"));
    }

    [Fact]
    public void NextFreeNameSkipsWhateverIsAlreadyThere()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal) { "a (copy).txt", "a (copy 2).txt" };
        Assert.Equal("a (copy 3).txt", ConflictNaming.NextFreeName("a.txt", taken.Contains));
    }
}
