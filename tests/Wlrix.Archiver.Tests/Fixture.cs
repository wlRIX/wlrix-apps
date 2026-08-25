namespace Wlrix.Archiver.Tests;

/// <summary>Reaching the archives under <c>Fixtures/</c>.</summary>
internal static class Fixture
{
    /// <summary>The full path to a fixture archive.</summary>
    /// <remarks>
    /// <see cref="AppContext.BaseDirectory"/> rather than the source tree: the csproj copies
    /// <c>Fixtures/</c> next to the test assembly, so this works the same from a bare
    /// <c>dotnet test</c> and from a runner with a different working directory.
    /// </remarks>
    public static string Path(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
