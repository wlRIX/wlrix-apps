using Microsoft.Extensions.Logging.Abstractions;
using Wlrix.Packages.UserSoftware;
using Xunit;

namespace Wlrix.Packages.Tests;

/// <summary>
/// These touch the real <c>~/Applications</c>, which is the directory the source is defined in
/// terms of. Every file they create carries a name no real AppImage would, and each test cleans
/// up after itself.
/// </summary>
public class AppImageSourceTests : IDisposable
{
    private const string TestName = "wlrix-test-fixture-do-not-keep";

    private readonly AppImageSource _source = new(NullLogger<AppImageSource>.Instance);
    private readonly string _staging = Path.Combine(Path.GetTempPath(), $"{TestName}.AppImage");

    public AppImageSourceTests() => File.WriteAllText(_staging, "#!/bin/sh\nexit 0\n");

    public void Dispose()
    {
        File.Delete(_staging);

        var installed = Path.Combine(AppImageSource.Directory, $"{TestName}.AppImage");
        if (File.Exists(installed))
            _source.Remove(installed);
    }

    [Fact]
    public void Install_CopiesTheFileWithoutTakingItFromWhereItWas()
    {
        var package = _source.Install(_staging);

        Assert.True(File.Exists(package.Id), "the AppImage was not installed");
        Assert.True(File.Exists(_staging), "the original was moved rather than copied");
        Assert.Equal(AppImageSource.Directory, Path.GetDirectoryName(package.Id));
        Assert.Equal(TestName, package.Name);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Install_MarksItExecutable()
    {
        // The single most common reason an AppImage "does not work".
        var package = _source.Install(_staging);

        Assert.True(File.GetUnixFileMode(package.Id).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void Install_WritesADesktopEntryTheToolchestWillFind()
    {
        var package = _source.Install(_staging);
        var entry = DesktopEntryPath();

        Assert.True(File.Exists(entry), "no desktop entry was written");

        var content = File.ReadAllText(entry);
        Assert.Contains("[Desktop Entry]", content);
        Assert.Contains($"Name={TestName}", content);
        Assert.Contains($"Exec=\"{package.Id}\"", content);
        Assert.Contains("Type=Application", content);
    }

    [Fact]
    public void List_ReportsWhatWasInstalled()
    {
        _source.Install(_staging);

        Assert.Contains(_source.List(), package => package.Name == TestName);
    }

    [Fact]
    public void Remove_TakesTheLauncherWithIt()
    {
        var package = _source.Install(_staging);
        _source.Remove(package.Id);

        Assert.False(File.Exists(package.Id));
        Assert.False(File.Exists(DesktopEntryPath()), "the desktop entry outlived the AppImage");
        Assert.DoesNotContain(_source.List(), p => p.Name == TestName);
    }

    [Fact]
    public void Remove_RefusesAPathOutsideTheManagedDirectory()
    {
        // `id` is a path, and a bug that let it be an arbitrary one would make this a file
        // deleter rather than an uninstaller.
        var outside = Path.Combine(Path.GetTempPath(), "not-managed.AppImage");
        File.WriteAllText(outside, "x");

        try
        {
            Assert.Throws<InvalidOperationException>(() => _source.Remove(outside));
            Assert.True(File.Exists(outside), "a file outside the managed directory was deleted");
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void Install_ReportsAFileThatIsNotThere() =>
        Assert.Throws<FileNotFoundException>(() =>
            _source.Install(Path.Combine(Path.GetTempPath(), "no-such-file.AppImage")));

    private static string DesktopEntryPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "applications", $"wlrix-appimage-{TestName}.desktop");
}
