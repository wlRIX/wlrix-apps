namespace Wlrix.Packages;

/// <summary>What <c>/etc/os-release</c> says this system is.</summary>
/// <param name="Id">The <c>ID</c> field — <c>arch</c>, <c>debian</c>, <c>opensuse-tumbleweed</c>.</param>
/// <param name="Name">The <c>PRETTY_NAME</c>, for the status line.</param>
/// <param name="Like">
/// The <c>ID_LIKE</c> field, split. This is what makes a derivative work without being listed:
/// CachyOS says <c>ID=cachyos ID_LIKE=arch</c>, Linux Mint says <c>ID_LIKE=ubuntu debian</c>.
/// </param>
public sealed record DistributionInfo(string Id, string Name, IReadOnlyList<string> Like)
{
    /// <summary>What is shown when there is no <c>/etc/os-release</c> to read.</summary>
    public static DistributionInfo Unknown { get; } = new("unknown", "this system", []);

    /// <summary>Whether this system is, or is derived from, <paramref name="id"/>.</summary>
    public bool Matches(string id) =>
        string.Equals(Id, id, StringComparison.OrdinalIgnoreCase)
        || Like.Contains(id, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Reads <c>/etc/os-release</c>.</summary>
public static class SystemDetection
{
    private const string OsReleasePath = "/etc/os-release";

    // The fallback the spec names for systems that only ship the one under /usr.
    private const string UsrOsReleasePath = "/usr/lib/os-release";

    /// <summary>
    /// What this system is, or <see cref="DistributionInfo.Unknown"/> if the file is missing or
    /// unreadable. Never throws: a package manager is found by probing the PATH anyway, so a
    /// missing os-release costs a nicer status line and nothing else.
    /// </summary>
    public static DistributionInfo Detect()
    {
        foreach (var path in new[] { OsReleasePath, UsrOsReleasePath })
        {
            try
            {
                if (File.Exists(path))
                    return Parse(File.ReadAllLines(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Try the other path, then give up.
            }
        }

        return DistributionInfo.Unknown;
    }

    /// <summary>Parses os-release content. Pure, so the tests can feed it a fixture.</summary>
    internal static DistributionInfo Parse(IEnumerable<string> lines)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;

            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
                continue;

            fields[trimmed[..separator]] = Unquote(trimmed[(separator + 1)..]);
        }

        var id = fields.GetValueOrDefault("ID", "unknown");
        return new DistributionInfo(
            id,
            fields.GetValueOrDefault("PRETTY_NAME") ?? fields.GetValueOrDefault("NAME") ?? id,
            fields.GetValueOrDefault("ID_LIKE", string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    // os-release values are shell-quoted. Only the outer quotes matter here: no value this
    // reads has an escape in it, and inventing a shell unescaper for three fields would be more
    // code than the fields are worth.
    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == trimmed[^1] && trimmed[0] is '"' or '\'')
            return trimmed[1..^1];
        return trimmed;
    }
}
