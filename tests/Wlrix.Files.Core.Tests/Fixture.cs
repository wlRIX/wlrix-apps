namespace Wlrix.Files.Core.Tests;

/// <summary>Locates a committed fixture file.</summary>
internal static class Fixture
{
    public static string Path(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static string[] Lines(string name) => File.ReadAllLines(Path(name));
}
