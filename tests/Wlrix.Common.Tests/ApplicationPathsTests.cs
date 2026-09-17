using Wlrix.Common;
using Xunit;

namespace Wlrix.Common.Tests;

/// <summary>Where every wlRIX application keeps its state.</summary>
public class ApplicationPathsTests
{
    [Fact]
    public void TheDataDirectoryIsAlwaysAbsolute()
    {
        // The one property that matters. A relative answer is not a wrong directory, it is
        // *every* directory: each application writes its state relative to wherever it
        // happened to be launched from, so the same program has different settings depending
        // on the shell's working directory. It is also invisible until somebody notices a
        // stray wlrix/ folder in a source tree.
        Assert.True(Path.IsPathRooted(ApplicationPaths.AppData), ApplicationPaths.AppData);
        Assert.EndsWith("wlrix", ApplicationPaths.AppData, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureDirectoryMakesTheWholeChainAndAnswersWithIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "wlrix-paths-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var deep = Path.Combine(root, "one", "two", "three");
            Assert.Equal(deep, ApplicationPaths.EnsureDirectory(deep));
            Assert.True(Directory.Exists(deep));
            // Idempotent: called on every save, and an existing directory is the usual case.
            Assert.Equal(deep, ApplicationPaths.EnsureDirectory(deep));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
