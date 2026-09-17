namespace Wlrix.Files.Core.Tests;

/// <summary>A scratch directory that deletes itself.</summary>
/// <remarks>
/// Modelled on the Archiver's, including the swallowed exceptions on cleanup: a test
/// that already made its assertion should not then fail because a file was briefly
/// locked while it was being removed.
/// </remarks>
internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wlrix-files-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public Location Location => Core.Location.FromLocalPath(Path);

    public string File(string relative, string content = "")
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public string Directory_(string relative)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
