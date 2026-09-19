using Wlrix.Files.Core.Metadata;
using Wlrix.Files.Core.Mime;
using Wlrix.Files.Core.Platform;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// Deciding whether a double-click should offer to run a file, and whether running it means
/// marking it executable first.
/// </summary>
public class ExecutablePolicyTests
{
    private static SharedMimeDatabase Mime() =>
        SharedMimeDatabase.Load([Path.Combine(AppContext.BaseDirectory, "Fixtures", "mime")]);

    /// <remarks>
    /// No mode, deliberately: enumeration does not report one, so this is the shape the policy
    /// actually meets. The mode comes from a stat the caller makes afterwards.
    /// </remarks>
    private static FileEntry Entry(string path, FileKind kind = FileKind.File)
    {
        var location = Location.Parse(path);
        return new FileEntry
        {
            Location = location,
            Name = location.Name,
            Kind = kind,
            Size = 1024
        };
    }

    // Written in binary, grouped in threes, because C# has no octal literal and these are three
    // digits read one group at a time. Same reasoning as UnixPermissionsTests.
    private const int Rw_R__R__ = 0b110_100_100;
    private const int Rwxr_xr_x = 0b111_101_101;
    private const int Rw_Rwxr__ = 0b110_111_100;

    private static bool IsProgram(FileEntry entry, string mimeType, bool hasHandler = false) =>
        ExecutablePolicy.IsProgram(Mime(), entry, mimeType, hasHandler);

    /// <summary>The whole decision, the way a caller makes it: ask, then stat, then stage.</summary>
    private static ExecutableAction Decide(FileEntry entry, string mimeType, int? mode,
        bool hasHandler = false) =>
        IsProgram(entry, mimeType, hasHandler)
            ? ExecutablePolicy.Stage(mode)
            : ExecutableAction.None;

    [Fact]
    public void AnAppImageThatIsAlreadyExecutableIsRunAfterOneConfirmation()
    {
        var entry = Entry("/home/vic/IRIS-gui.AppImage");

        Assert.Equal(ExecutableAction.Run, Decide(entry, "application/vnd.appimage", Rwxr_xr_x));
    }

    [Fact]
    public void AnAppImageWithNoExecuteBitHasToBeGrantedOneFirst()
    {
        var entry = Entry("/home/vic/IRIS-gui.AppImage");

        Assert.Equal(ExecutableAction.GrantThenRun, Decide(entry, "application/vnd.appimage", Rw_R__R__));
    }

    /// <summary>
    /// Any of the three bits counts, which is what wlrix-desktop's is_executable asks of a
    /// .desktop file. A file the group may run is not one to ask about marking executable.
    /// </summary>
    [Fact]
    public void AnExecuteBitBelongingToSomebodyElseStillCountsAsExecutable()
    {
        var entry = Entry("/home/vic/tool");

        Assert.Equal(ExecutableAction.Run, Decide(entry, "application/x-executable", Rw_Rwxr__));
    }

    /// <summary>
    /// An unknown mode must never become a grant. Granting means writing a mode, and the only
    /// mode available to write would be zero plus the execute bit — 0o100, which takes the
    /// owner's read and write with it. Found by running the thing: the listing reports no mode
    /// at all, so this was every file until the caller learned to stat first.
    /// </summary>
    [Fact]
    public void AnUnknownModeIsRunRatherThanGranted()
    {
        var entry = Entry("/home/vic/tool");

        Assert.True(IsProgram(entry, "application/x-executable"));
        Assert.Equal(ExecutableAction.Run, Decide(entry, "application/x-executable", mode: null));
    }

    [Fact]
    public void AnOrdinaryDocumentIsNotOfferedAtAll()
    {
        var entry = Entry("/home/vic/notes.txt");

        Assert.Equal(ExecutableAction.None, Decide(entry, "text/plain", Rwxr_xr_x));
    }

    /// <summary>
    /// The choice that differs from Dolphin, pinned so it cannot drift by accident: a type
    /// something else already opens keeps opening that way. Every scripting language inherits
    /// application/x-executable, and a shell script running on a double-click where it used to
    /// open in an editor would be a surprise, not a feature.
    /// </summary>
    [Fact]
    public void ARegisteredHandlerWinsOverRunningTheFile()
    {
        var entry = Entry("/home/vic/build.sh");

        Assert.Equal(ExecutableAction.Run, Decide(entry, "text/x-shellscript", Rwxr_xr_x));
        Assert.Equal(ExecutableAction.None,
            Decide(entry, "text/x-shellscript", Rwxr_xr_x, hasHandler: true));
    }

    [Fact]
    public void ADirectoryIsNavigationRatherThanAProgram()
    {
        var entry = Entry("/home/vic/bin", FileKind.Directory);

        Assert.Equal(ExecutableAction.None, Decide(entry, "application/x-executable", Rwxr_xr_x));
    }

    /// <summary>
    /// A symlink's recorded mode is the link's own, which is 0777 whatever the target says, so
    /// one would always look executable and the second confirmation would never appear.
    /// </summary>
    [Fact]
    public void ASymlinkIsLeftAlone()
    {
        var entry = Entry("/home/vic/tool", FileKind.Symlink);

        Assert.Equal(ExecutableAction.None, Decide(entry, "application/x-executable", Rwxr_xr_x));
    }

    [Fact]
    public void AProgramOnAShareIsNotOfferedBecauseItCannotBeRun()
    {
        var entry = Entry("smb://nas/share/tool.AppImage");

        Assert.Equal(ExecutableAction.None, Decide(entry, "application/vnd.appimage", Rwxr_xr_x));
    }

    /// <summary>
    /// The owner's bit and nothing else: the question the user answered was whether they trust
    /// the program, which says nothing about everybody else on the machine.
    /// </summary>
    [Fact]
    public void GrantingSetsOnlyTheOwnersExecuteBit()
    {
        var granted = new UnixPermissions(ExecutablePolicy.WithOwnerExecute(Rw_R__R__));

        Assert.True(granted.Has(PermissionClass.Owner, PermissionBits.Execute));
        Assert.False(granted.Has(PermissionClass.Group, PermissionBits.Execute));
        Assert.False(granted.Has(PermissionClass.Other, PermissionBits.Execute));
        Assert.True(granted.Has(PermissionClass.Owner, PermissionBits.Read | PermissionBits.Write));
        Assert.True(granted.Has(PermissionClass.Group, PermissionBits.Read));
    }
}
