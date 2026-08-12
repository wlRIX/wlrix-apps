namespace Wlrix.Packages.Tests;

/// <summary>Reads the recorded package-manager output the parser tests run against.</summary>
internal static class Fixture
{
    /// <summary>The contents of <c>Fixtures/<paramref name="name"/></c>.</summary>
    internal static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
