namespace Wlrix.Files.Core.Platform;

/// <summary>One mounted filesystem.</summary>
/// <param name="DeviceId">The kernel's <c>major:minor</c>. Two paths sharing this are on one filesystem.</param>
/// <param name="MountPoint">Where it is mounted.</param>
/// <param name="FilesystemType">The type as the kernel names it: <c>ext4</c>, <c>btrfs</c>, <c>tmpfs</c>.</param>
/// <param name="Source">The backing device or source, e.g. <c>/dev/nvme0n1p2</c>.</param>
/// <param name="IsReadOnly">Whether the mount itself is read-only.</param>
public sealed record Mount(
    string DeviceId,
    string MountPoint,
    string FilesystemType,
    string Source,
    bool IsReadOnly)
{
    /// <summary>
    /// Whether this holds real user files, as opposed to a kernel or runtime
    /// filesystem.
    /// </summary>
    /// <remarks>
    /// An allowlist, not a denylist of pseudo-filesystems: <c>/proc/self/mountinfo</c>
    /// on a modern system lists dozens of cgroup, bpf, tracefs and overlay mounts, and
    /// new ones keep appearing. Naming what we want cannot be surprised by that.
    /// Mirrors the allowlist in <c>Wlrix.SoftwareManager</c>'s <c>DiskSpaceProbe</c>.
    /// </remarks>
    public bool IsRealFilesystem => RealFilesystemTypes.Contains(FilesystemType);

    /// <summary>Filesystem types that hold user files.</summary>
    /// <remarks>
    /// The devices rail's question: is this worth listing as a place with free space on it.
    /// Network filesystems belong here — a mounted share is somewhere the user keeps things.
    /// It is <b>not</b> the question "is this cheap to read"; see <see cref="IsLocalStorage"/>.
    /// </remarks>
    public static readonly IReadOnlySet<string> RealFilesystemTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "ext2", "ext3", "ext4", "btrfs", "xfs", "zfs", "f2fs", "jfs", "reiserfs", "bcachefs",
        "vfat", "exfat", "ntfs", "ntfs3", "fuseblk", "iso9660", "udf",
        "nfs", "nfs4", "cifs", "smb3", "overlay"
    };

    /// <summary>Filesystem types whose contents live on another machine.</summary>
    public static readonly IReadOnlySet<string> NetworkFilesystemTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "nfs", "nfs4", "cifs", "smb3", "smbfs", "afs", "ceph", "glusterfs", "9p",
        "fuse.sshfs", "fuse.davfs", "fuse.gvfsd-fuse", "davfs"
    };

    /// <summary>Local filesystems that are not backed by a device.</summary>
    /// <remarks>
    /// Reading one is a memory copy — the cheapest thing on the machine — so excluding them
    /// from <see cref="IsLocalStorage"/> would mean no previews in <c>/tmp</c>, which is
    /// where a download or an unpacked archive lands.
    /// </remarks>
    private static readonly IReadOnlySet<string> LocalVolatileTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "tmpfs", "ramfs"
    };

    /// <summary>
    /// Whether this filesystem's <i>contents</i> can be read without crossing a network.
    /// </summary>
    /// <remarks>
    /// A different question from <see cref="IsRealFilesystem"/>, and confusing the two is a
    /// real mistake rather than a theoretical one: that set is the devices rail's, so using it
    /// to gate thumbnailing both refused <c>tmpfs</c>, which is the fastest storage on the
    /// machine, and permitted <c>nfs</c> and <c>cifs</c> — which is precisely the case the
    /// gate exists to prevent, since it means pulling every file in a directory across the
    /// wire to make pictures nobody asked for.
    /// </remarks>
    public bool IsLocalStorage =>
        !NetworkFilesystemTypes.Contains(FilesystemType)
        && (RealFilesystemTypes.Contains(FilesystemType) || LocalVolatileTypes.Contains(FilesystemType));
}

/// <summary>
/// The mounted filesystems, read from <c>/proc/self/mountinfo</c>.
/// </summary>
/// <remarks>
/// <c>mountinfo</c> rather than <c>/proc/mounts</c>, for one decisive reason: it
/// carries the <c>major:minor</c> device id in field 3. That is what answers "are these
/// two paths on the same filesystem", which the operations engine must know *before*
/// attempting a rename — catching <c>EXDEV</c> afterwards means a failure some backends
/// report indistinguishably from a real error. It also gets bind mounts and btrfs
/// subvolumes right, which <c>/proc/mounts</c> does not.
///
/// <para>
/// A snapshot. Mounts change while the app runs, so a long-lived instance goes stale;
/// re-read when it matters rather than caching one forever.
/// </para>
/// </remarks>
public sealed class MountTable
{
    private const string MountInfoPath = "/proc/self/mountinfo";

    private readonly List<Mount> _mounts;

    private MountTable(List<Mount> mounts)
    {
        // Longest mount point first, so the first prefix match is the most specific
        // one -- /home/vic/data must win over / for a path inside it.
        mounts.Sort(static (a, b) => b.MountPoint.Length.CompareTo(a.MountPoint.Length));
        _mounts = mounts;
    }

    /// <summary>Every mount, most specific mount point first.</summary>
    public IReadOnlyList<Mount> Mounts => _mounts;

    /// <summary>Just the ones holding user files.</summary>
    public IEnumerable<Mount> RealFilesystems => _mounts.Where(static m => m.IsRealFilesystem);

    /// <summary>Reads the current mount table.</summary>
    public static MountTable Read() => Parse(File.ReadAllLines(MountInfoPath));

    /// <summary>Parses mountinfo content. The seam the tests use.</summary>
    public static MountTable Parse(IEnumerable<string> lines)
    {
        var mounts = new List<Mount>();
        foreach (var line in lines)
        {
            if (TryParseLine(line, out var mount))
                mounts.Add(mount);
        }
        return new MountTable(mounts);
    }

    /// <summary>The mount a path is on, or null if none matches.</summary>
    public Mount? Find(string path)
    {
        var normalized = Location.NormalizePath(path);
        foreach (var mount in _mounts)
        {
            if (IsUnder(normalized, mount.MountPoint))
                return mount;
        }
        return null;
    }

    /// <summary>The mount a location is on, or null if it is not local.</summary>
    public Mount? Find(Location location) =>
        location.TryGetLocalPath(out var path) ? Find(path) : null;

    /// <summary>
    /// Whether two locations are on the same filesystem, and so can be moved between
    /// with a rename.
    /// </summary>
    /// <remarks>
    /// Both must be local and both must resolve to a mount. An unknown answer is
    /// reported as <c>false</c> — treating it as "same" would mean attempting a rename
    /// that fails halfway through a move, where treating it as "different" merely
    /// costs a copy.
    /// </remarks>
    public bool IsSameFilesystem(Location a, Location b)
    {
        var ma = Find(a);
        var mb = Find(b);
        return ma is not null && mb is not null
            && string.Equals(ma.DeviceId, mb.DeviceId, StringComparison.Ordinal);
    }

    private static bool IsUnder(string path, string mountPoint)
    {
        if (mountPoint == "/")
            return true;
        if (!path.StartsWith(mountPoint, StringComparison.Ordinal))
            return false;
        return path.Length == mountPoint.Length || path[mountPoint.Length] == '/';
    }

    /// <summary>
    /// Parses one mountinfo line.
    /// </summary>
    /// <remarks>
    /// The format is documented in <c>proc(5)</c>:
    /// <code>
    /// 36 35 98:0 /mnt1 /mnt2 rw,noatime - ext3 /dev/root rw,errors=continue
    /// (1)(2)(3)  (4)   (5)   (6)       (7) (8)  (9)      (10)
    /// </code>
    /// Field 7 is a variable number of optional fields terminated by a single
    /// <c>-</c>, which is why this splits on that separator instead of indexing
    /// fixed positions. Paths are octal-escaped.
    /// </remarks>
    private static bool TryParseLine(string line, out Mount mount)
    {
        mount = null!;
        var separator = line.IndexOf(" - ", StringComparison.Ordinal);
        if (separator < 0)
            return false;

        var head = line[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tail = line[(separator + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (head.Length < 6 || tail.Length < 2)
            return false;

        var mountOptions = head[5];
        mount = new Mount(
            DeviceId: head[2],
            MountPoint: Unescape(head[4]),
            FilesystemType: tail[0],
            Source: Unescape(tail[1]),
            IsReadOnly: mountOptions == "ro" || mountOptions.StartsWith("ro,", StringComparison.Ordinal));
        return true;
    }

    /// <summary>
    /// Undoes the octal escaping the kernel applies to space, tab, newline and
    /// backslash in mountinfo paths.
    /// </summary>
    private static string Unescape(string value)
    {
        if (!value.Contains('\\'))
            return value;

        var sb = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 3 < value.Length
                && TryOctal(value[i + 1], out var a) && TryOctal(value[i + 2], out var b) && TryOctal(value[i + 3], out var c))
            {
                sb.Append((char)((a << 6) | (b << 3) | c));
                i += 3;
            }
            else
            {
                sb.Append(value[i]);
            }
        }
        return sb.ToString();

        static bool TryOctal(char ch, out int value)
        {
            value = ch - '0';
            return value is >= 0 and <= 7;
        }
    }
}
