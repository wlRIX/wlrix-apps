namespace Wlrix.Archiver.Services.Archives;

/// <summary>Gives extracted files the permissions their archive recorded.</summary>
/// <remarks>
/// <para>
/// What <c>tar</c> does for a user who is not root: the read, write and execute bits as stored,
/// less whatever the umask takes away. So an executable comes out executable, a private file
/// stays private, and a umask of 077 still keeps everything to its owner.
/// </para>
/// <para>
/// The setuid, setgid and sticky bits are never restored. An archive is somebody else's
/// opinion of what should run as whom, and an extractor that honored it would make a setuid
/// program out of anything that asked -- which is exactly why <c>tar</c> leaves them off unless
/// told otherwise by root.
/// </para>
/// </remarks>
internal static class UnixPermissions
{
    private const int PermissionBits = 0x1ff; // 0o777

    private static readonly Lazy<int> Umask = new(ReadUmask);

    /// <summary>Sets <paramref name="path"/>'s mode from a recorded one; nothing when there is none.</summary>
    public static void Apply(string path, int? recorded)
    {
        if (recorded is not { } mode || OperatingSystem.IsWindows())
            return;

        var effective = mode & PermissionBits & ~Umask.Value;
        try
        {
            File.SetUnixFileMode(path, (UnixFileMode)effective);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file is out with its contents intact; a mode that would not take is a lesser
            // failure than abandoning the rest of the archive over it.
        }
    }

    /// <summary>The process's umask, from <c>/proc</c>; 022, the usual one, if that cannot be read.</summary>
    /// <remarks>
    /// .NET has no call for it, and the classic way -- set a new one to learn the old -- changes
    /// it for every thread in the process for the moment in between.
    /// </remarks>
    private static int ReadUmask()
    {
        const int Usual = 0x12; // 0o022
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("Umask:", StringComparison.Ordinal))
                    return System.Convert.ToInt32(line["Umask:".Length..].Trim(), 8) & PermissionBits;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
        }

        return Usual;
    }
}
