using Microsoft.Extensions.Logging;
using ZLogger;

namespace Wlrix.Packages.UserSoftware;

/// <summary>One piece of software the user owns, outside the system package manager.</summary>
/// <param name="Id">What identifies it to its source — for an AppImage, its path.</param>
/// <param name="Name">What to show.</param>
/// <param name="SizeKilobytes">How much disk it takes.</param>
/// <param name="Installed">When it arrived.</param>
public sealed record UserPackage(string Id, string Name, long SizeKilobytes, DateTime Installed);

/// <summary>
/// Software the user installs for themselves: no root, no package manager, nothing outside
/// their home directory.
///
/// Shaped for a Flatpak implementation to sit beside this one. The two have the same four
/// operations and nothing else in common, which is why this is an interface rather than a class
/// with a mode switch.
/// </summary>
public interface IUserSoftwareSource
{
    /// <summary>What this source is called, for the Type column.</summary>
    string Id { get; }

    /// <summary>What is installed.</summary>
    IReadOnlyList<UserPackage> List();

    /// <summary>Takes a copy of <paramref name="path"/> and makes it launchable.</summary>
    UserPackage Install(string path);

    /// <summary>Removes it, and whatever was created to launch it.</summary>
    void Remove(string id);
}

/// <summary>
/// AppImages in <c>~/Applications</c>.
///
/// Installing one is three things: copy it in, mark it executable, and write a
/// <c>.desktop</c> entry so it turns up in the Toolchest. That last one is the reason this
/// exists at all — an AppImage sitting in a directory is a file, and an AppImage with a desktop
/// entry is an application.
/// </summary>
public sealed class AppImageSource(ILogger<AppImageSource> logger) : IUserSoftwareSource
{
    /// <summary>Where AppImages live. The convention every AppImage launcher already looks in.</summary>
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications");

    /// <summary>Where the generated launchers go, for the Toolchest's scanner to find.</summary>
    private static string DesktopDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "applications");

    /// <summary>
    /// The prefix on every generated entry's file name. It is what makes removal safe: only a
    /// file this wrote is ever deleted, so a hand-written entry for the same AppImage survives.
    /// </summary>
    private const string DesktopPrefix = "wlrix-appimage-";

    public string Id => "appimage";

    public IReadOnlyList<UserPackage> List()
    {
        if (!System.IO.Directory.Exists(Directory))
            return [];

        try
        {
            return System.IO.Directory
                .EnumerateFiles(Directory, "*.AppImage", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Select(file => new UserPackage(file.FullName,
                    Path.GetFileNameWithoutExtension(file.Name),
                    file.Length / 1024,
                    file.LastWriteTime))
                .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.ZLogWarning(ex, $"Could not read {Directory}.");
            return [];
        }
    }

    public UserPackage Install(string path)
    {
        var source = new FileInfo(path);
        if (!source.Exists)
            throw new FileNotFoundException($"There is no file at {path}.", path);

        System.IO.Directory.CreateDirectory(Directory);
        var target = Path.Combine(Directory, source.Name);

        // Copying rather than moving: the AppImage the user pointed at may be in their Downloads
        // folder, or on a memory stick, and taking it away from where they put it is not what
        // "install" means to anyone. The exception is a file that is already here.
        if (!string.Equals(source.FullName, target, StringComparison.Ordinal))
            source.CopyTo(target, overwrite: true);

        MakeExecutable(target);
        WriteDesktopEntry(target);

        var installed = new FileInfo(target);
        logger.ZLogInformation($"Installed the AppImage {installed.Name}.");
        return new UserPackage(installed.FullName, Path.GetFileNameWithoutExtension(installed.Name),
            installed.Length / 1024, installed.LastWriteTime);
    }

    public void Remove(string id)
    {
        // Only inside the managed directory. `id` is a path, and a bug that let it be an
        // arbitrary one would make this a file deleter rather than an uninstaller.
        var full = Path.GetFullPath(id);
        if (Path.GetDirectoryName(full) != Directory)
            throw new InvalidOperationException($"{id} is not a managed AppImage.");

        File.Delete(full);

        var entry = DesktopEntryPath(full);
        if (File.Exists(entry))
            File.Delete(entry);

        logger.ZLogInformation($"Removed the AppImage {Path.GetFileName(full)}.");
    }

    /// <summary>
    /// Writes the launcher the Toolchest picks up.
    ///
    /// Nothing is read out of the AppImage itself — no name, no icon, no categories. Getting any
    /// of that means running the file with <c>--appimage-extract</c>, which is executing an
    /// untrusted binary to find out what it is, and the user has not asked to run it yet. The
    /// entry gets the file name and a generic icon, both of which they can edit.
    /// </summary>
    private void WriteDesktopEntry(string appImage)
    {
        var name = Path.GetFileNameWithoutExtension(appImage);

        // Exec values are unquoted by the Desktop Entry Spec's own rules, and a path can hold a
        // space. Quoting and doubling any backslash is what the spec asks for.
        var exec = "\"" + appImage.Replace(@"\", @"\\").Replace("\"", "\\\"") + "\"";

        var entry = $"""
            [Desktop Entry]
            Type=Application
            Name={name}
            Exec={exec}
            Icon=application-x-executable
            Terminal=false
            Categories=Utility;
            X-Wlrix-AppImage={appImage}
            """;

        try
        {
            System.IO.Directory.CreateDirectory(DesktopDirectory);
            File.WriteAllText(DesktopEntryPath(appImage), entry + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The AppImage is installed and runnable either way; it just will not appear in the
            // Toolchest. Worth saying, not worth undoing the install over.
            logger.ZLogWarning(ex, $"Could not write a desktop entry for {name}.");
        }
    }

    private static string DesktopEntryPath(string appImage) => Path.Combine(DesktopDirectory,
        DesktopPrefix + Path.GetFileNameWithoutExtension(appImage) + ".desktop");

    /// <summary>
    /// Adds the executable bit, wherever read is already allowed. An AppImage that is not
    /// executable is the single most common reason one "does not work".
    /// </summary>
    private static void MakeExecutable(string path)
    {
        // A guard for the type system rather than for anyone's benefit: the Unix file mode APIs
        // are annotated as unavailable on Windows, and wlRIX is a Linux desktop that will never
        // be there. Naming the condition satisfies the analyzer without suppressing it.
        if (OperatingSystem.IsWindows())
            return;

        var mode = File.GetUnixFileMode(path);

        if (mode.HasFlag(UnixFileMode.UserRead))
            mode |= UnixFileMode.UserExecute;
        if (mode.HasFlag(UnixFileMode.GroupRead))
            mode |= UnixFileMode.GroupExecute;
        if (mode.HasFlag(UnixFileMode.OtherRead))
            mode |= UnixFileMode.OtherExecute;

        File.SetUnixFileMode(path, mode);
    }
}
