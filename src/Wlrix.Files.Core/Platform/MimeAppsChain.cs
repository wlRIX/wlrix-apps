namespace Wlrix.Files.Core.Platform;

/// <summary>
/// Every <c>mimeapps.list</c> that has a say, in the order the association specification
/// gives them.
/// </summary>
/// <remarks>
/// The user's file is only the first of several. A distribution or an administrator sets
/// defaults in <c>/etc/xdg/mimeapps.list</c>, packages ship them under
/// <c>/usr/share/applications</c>, and each of those places may also carry a
/// <c>&lt;desktop&gt;-mimeapps.list</c> that only applies under that desktop. Reading the user
/// file alone means a default the system set is invisible here and visible to
/// <c>xdg-open</c>, so the two disagree about what a double-click does.
///
/// <para>
/// The three groups combine differently, and that is the reason this is a class rather than a
/// loop at each caller:
/// </para>
/// <list type="bullet">
/// <item>Defaults are tried file by file, and the first one that is installed wins.</item>
/// <item>Added associations accumulate down the chain, minus whatever a file above removed.</item>
/// <item>A removal hides the association in every file below it and in the entries' own
/// <c>MimeType=</c>, but never in a file above it.</item>
/// </list>
///
/// <para>
/// Read-only. Writes go to the user's file alone, through <see cref="MimeAppsList"/>: the
/// others belong to root or to a package.
/// </para>
/// </remarks>
public sealed class MimeAppsChain
{
    private readonly IReadOnlyList<MimeAppsList> _lists;

    private MimeAppsChain(IReadOnlyList<string> paths, IReadOnlyList<MimeAppsList> lists)
    {
        Paths = paths;
        _lists = lists;
    }

    /// <summary>The files read, most important first.</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>Reads every file in the chain. A missing one reads as empty.</summary>
    public static MimeAppsChain Read(IEnumerable<string> paths)
    {
        var list = paths as IReadOnlyList<string> ?? [.. paths];
        return new MimeAppsChain(list, [.. list.Select(MimeAppsList.Read)]);
    }

    /// <summary>The chain for this session, from the environment.</summary>
    public static IReadOnlyList<string> DefaultPaths() =>
        PathsFor(
            ConfigHome(),
            Split(Environment.GetEnvironmentVariable("XDG_CONFIG_DIRS"), "/etc/xdg"),
            DataHome(),
            Split(Environment.GetEnvironmentVariable("XDG_DATA_DIRS"), "/usr/local/share:/usr/share"),
            Split(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"), string.Empty));

    /// <summary>The chain for the given directories.</summary>
    /// <remarks>
    /// In each directory the desktop-specific files come before the plain one, and the
    /// desktops in the order <c>XDG_CURRENT_DESKTOP</c> lists them. The names are lowercased,
    /// as the spec says, so this session's <c>wlRIX</c> reads <c>wlrix-mimeapps.list</c>.
    /// <c>$XDG_DATA_HOME/applications</c> is deprecated by the spec and still read, after the
    /// configuration directories, because older tools wrote there.
    /// </remarks>
    public static IReadOnlyList<string> PathsFor(
        string configHome,
        IReadOnlyList<string> configDirs,
        string dataHome,
        IReadOnlyList<string> dataDirs,
        IReadOnlyList<string> desktops)
    {
        var names = desktops
            .Select(static desktop => $"{desktop.ToLowerInvariant()}-mimeapps.list")
            .Append("mimeapps.list")
            .ToList();

        var directories = new List<string> { configHome };
        directories.AddRange(configDirs);
        directories.Add(Path.Combine(dataHome, "applications"));
        directories.AddRange(dataDirs.Select(static dir => Path.Combine(dir, "applications")));

        return [.. directories.SelectMany(dir => names.Select(name => Path.Combine(dir, name))).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The candidates for a type's default, in the order to try them.</summary>
    /// <remarks>
    /// Every file's list, one after another. The caller takes the first that is installed;
    /// a list in a file above naming only uninstalled applications falls through to the next
    /// file rather than to no default at all.
    /// </remarks>
    public IReadOnlyList<string> Defaults(string mimeType) =>
        [.. _lists.SelectMany(list => list.EntriesFor(MimeAppsList.DefaultApplications, mimeType)).Distinct(StringComparer.Ordinal)];

    /// <summary>The added associations for a type, after every removal above them.</summary>
    public IReadOnlyList<string> Added(string mimeType)
    {
        var removed = new HashSet<string>(StringComparer.Ordinal);
        var added = new List<string>();
        foreach (var list in _lists)
        {
            foreach (var id in list.EntriesFor(MimeAppsList.AddedAssociations, mimeType))
            {
                if (!removed.Contains(id) && !added.Contains(id, StringComparer.Ordinal))
                    added.Add(id);
            }
            removed.UnionWith(list.EntriesFor(MimeAppsList.RemovedAssociations, mimeType));
        }
        return added;
    }

    /// <summary>Everything any file removed for a type, which the entries' own claims lose to.</summary>
    public IReadOnlySet<string> Removed(string mimeType) =>
        _lists.SelectMany(list => list.EntriesFor(MimeAppsList.RemovedAssociations, mimeType)).ToHashSet(StringComparer.Ordinal);

    private static string ConfigHome() =>
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
            ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

    private static string DataHome() =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data
            ? data
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    private static string[] Split(string? value, string fallback) =>
        (string.IsNullOrEmpty(value) ? fallback : value).Split(':', StringSplitOptions.RemoveEmptyEntries);
}
