using Wlrix.Files.Core.Metadata;
using Wlrix.Files.Core.Mime;

namespace Wlrix.Files.Core.Platform;

/// <summary>What opening a file that might be a program ought to do.</summary>
public enum ExecutableAction
{
    /// <summary>Not a program, or not one runnable from here. Open it the ordinary way.</summary>
    None,

    /// <summary>Already marked executable: confirm once, then run it.</summary>
    Run,

    /// <summary>
    /// A program with no execute bit anywhere: confirm once, confirm again, set the owner's
    /// execute bit, then run it.
    /// </summary>
    GrantThenRun
}

/// <summary>
/// Decides whether a file is a program the file manager should offer to run, and whether
/// running it means marking it executable first.
/// </summary>
/// <remarks>
/// Four conditions, each of which rules something out on its own.
///
/// <para>
/// <b>It must be a regular file.</b> A directory is navigation and a device is not a program.
/// Symlinks are excluded too, and deliberately: the mode recorded for one is the link's own,
/// which is 0777 on Linux whatever the target says, so a link would always look executable and
/// the second confirmation would never appear for the file that actually needs it.
/// </para>
///
/// <para>
/// <b>It must be local.</b> There is no executing a file on an SMB share without fetching it
/// first, and quietly downloading and running something is not a thing to do on a double-click.
/// </para>
///
/// <para>
/// <b>Nothing may already claim the type.</b> This is the significant choice, and it differs
/// from Dolphin, which prefers execution over a registered handler. Types that inherit
/// <c>application/x-executable</c> include every scripting language — shell, Perl, Lua, awk,
/// sed — and a file manager that ran a shell script on a double-click where it used to open it
/// in an editor would be a nasty surprise dressed as a feature. So this is a last resort before
/// reporting that nothing can open the file, which is exactly the case that prompted it: an
/// AppImage, which nothing claims and which today gives an error. The cost is that offering to
/// run a script needs Dolphin's three-way Execute / Open / Cancel prompt, which this is not.
/// </para>
///
/// <para>
/// <b>Its type must inherit <c>application/x-executable</c>.</b> The name is not enough and the
/// execute bit is not either: a text file somebody marked executable is still a text file, and
/// the caller has already sniffed the content, so an ELF binary with no extension is recognized
/// on its contents rather than missed.
/// </para>
///
/// <para>
/// "Already executable" means any of the three execute bits, which is what
/// <c>wlrix-desktop</c>'s <c>is_executable</c> asks of a <c>.desktop</c> file and what
/// Dolphin asks of anything. Granting sets the owner's bit only: the question being answered
/// is whether <em>this</em> user trusts the program, and that is no reason to hand it to
/// everybody else on the machine.
/// </para>
/// </remarks>
public static class ExecutablePolicy
{
    /// <summary>The type every runnable type derives from, per shared-mime-info.</summary>
    public const string ProgramType = "application/x-executable";

    /// <summary>Whether <paramref name="entry"/> is a program worth offering to run.</summary>
    /// <remarks>
    /// Deliberately says nothing about the execute bit, because the listing does not know it:
    /// enumeration reports size, time and kind but not the mode — <see cref="FileEntry.UnixMode"/>
    /// is null for every row — and a mode treated as zero would be chmodded to 0o100, taking the
    /// owner's read and write with it. The caller stats the file once it knows the answer here is
    /// yes, and passes the real mode to <see cref="Stage"/>.
    /// </remarks>
    /// <param name="mime">The type database, for the inheritance question.</param>
    /// <param name="entry">The file.</param>
    /// <param name="mimeType">The type already resolved for it, sniffed rather than named.</param>
    /// <param name="hasHandler">
    /// Whether an application is registered for <paramref name="mimeType"/>. When one is, it
    /// wins; see the remarks on the class.
    /// </param>
    public static bool IsProgram(
        SharedMimeDatabase mime, FileEntry entry, string mimeType, bool hasHandler)
    {
        ArgumentNullException.ThrowIfNull(mime);
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Kind != FileKind.File)
            return false;
        if (hasHandler)
            return false;
        if (!entry.Location.TryGetLocalPath(out _))
            return false;

        return !string.IsNullOrEmpty(mimeType) && mime.IsSubclassOf(mimeType, ProgramType);
    }

    /// <summary>What running a program amounts to, given the mode a stat actually returned.</summary>
    /// <remarks>
    /// An unknown mode means run and let the attempt speak for itself. It cannot mean "grant":
    /// there is no safe bit to add to a mode nobody knows, and inventing one destroys the file's
    /// permissions. In practice this does not arise — <see cref="IsProgram"/> has already
    /// established the file is local, and a local stat always reports a mode.
    /// </remarks>
    public static ExecutableAction Stage(int? mode) =>
        mode is not { } known || IsExecutable(known)
            ? ExecutableAction.Run
            : ExecutableAction.GrantThenRun;

    /// <summary>Whether any of the three execute bits is set.</summary>
    public static bool IsExecutable(int mode)
    {
        var permissions = new UnixPermissions(mode);
        return permissions.Has(PermissionClass.Owner, PermissionBits.Execute)
            || permissions.Has(PermissionClass.Group, PermissionBits.Execute)
            || permissions.Has(PermissionClass.Other, PermissionBits.Execute);
    }

    /// <summary>The mode to write to grant the owner's execute bit, leaving the rest alone.</summary>
    public static int WithOwnerExecute(int mode) =>
        new UnixPermissions(mode).With(PermissionClass.Owner, PermissionBits.Execute, true).Mode;
}
