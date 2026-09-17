using Wlrix.Common;

namespace Wlrix.Files.Core.Platform;

/// <summary>
/// Reading and writing <c>mimeapps.list</c>, which is what decides the default handler.
/// </summary>
/// <remarks>
/// Installing a desktop entry and running <c>update-desktop-database</c> makes an application
/// <i>eligible</i> to open a MIME type. It does not make it the default — that lives in
/// <c>$XDG_CONFIG_HOME/mimeapps.list</c> under <c>[Default Applications]</c>, and nothing but
/// an explicit write puts it there.
///
/// <para>
/// The format is a desktop-entry-style INI, and the file is the user's: it very likely already
/// says which application opens PDFs and which opens images. So this is a read-modify-write
/// that preserves every line it does not understand, including comments and blank lines and
/// groups it has never heard of. Rewriting the file from a parsed model would quietly discard
/// whatever a future spec revision adds.
/// </para>
///
/// <para>
/// Also used by "Open With → always", which is the same edit against a different MIME type.
/// </para>
/// </remarks>
public sealed class MimeAppsList
{
    /// <summary>The group that decides what opens a type.</summary>
    public const string DefaultApplications = "[Default Applications]";

    /// <summary>The group that adds handlers to the Open With list without making one default.</summary>
    public const string AddedAssociations = "[Added Associations]";

    /// <summary>The group that takes a handler away again.</summary>
    /// <remarks>
    /// Needed because a desktop entry's own <c>MimeType=</c> cannot be edited by a user — it
    /// belongs to the package. This is how somebody says "not that one" about an association
    /// an application claimed for itself.
    /// </remarks>
    public const string RemovedAssociations = "[Removed Associations]";

    private readonly List<string> _lines;

    private MimeAppsList(List<string> lines) => _lines = lines;

    /// <summary>Where the user's own associations live.</summary>
    public static string UserPath =>
        Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
                ? config
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "mimeapps.list");

    /// <summary>Reads a list, or an empty one if the file is not there.</summary>
    public static MimeAppsList Read(string path) =>
        new(File.Exists(path) ? [.. File.ReadAllLines(path)] : []);

    /// <summary>The current lines, for a caller that wants to see what would be written.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>What is currently registered to open <paramref name="mimeType"/>, or null.</summary>
    /// <remarks>
    /// The first entry only. The value is a semicolon-separated list in preference order, and
    /// the first one that exists is the one that runs.
    /// </remarks>
    public string? DefaultFor(string mimeType) =>
        EntriesFor(DefaultApplications, mimeType).FirstOrDefault();

    /// <summary>Every entry named for a type in one group, in the order written.</summary>
    /// <remarks>
    /// The value is a semicolon-separated preference list. For
    /// <see cref="DefaultApplications"/> the first one that exists is what runs; for the other
    /// two groups the whole list matters.
    /// </remarks>
    public IReadOnlyList<string> EntriesFor(string group, string mimeType)
    {
        var index = Find(group, mimeType);
        if (index < 0)
            return [];
        return [.. _lines[index][(_lines[index].IndexOf('=') + 1)..]
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>Makes <paramref name="entry"/> the default for <paramref name="mimeType"/>.</summary>
    /// <remarks>
    /// The previous default is not discarded: it is pushed down the preference list, so
    /// removing this entry later falls back to whatever was there before rather than to
    /// nothing at all.
    /// </remarks>
    public void SetDefault(string mimeType, string entry)
    {
        var existing = Find(DefaultApplications, mimeType);
        var others = existing < 0
            ? []
            : _lines[existing][(_lines[existing].IndexOf('=') + 1)..]
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(name => !string.Equals(name, entry, StringComparison.Ordinal))
                .ToArray();

        var line = $"{mimeType}={string.Join(';', new[] { entry }.Concat(others))};";
        if (existing >= 0)
            _lines[existing] = line;
        else
            Insert(DefaultApplications, line);
    }

    /// <summary>Removes <paramref name="entry"/> from the defaults for a type.</summary>
    /// <returns>False if it was not there.</returns>
    public bool ClearDefault(string mimeType, string entry)
    {
        var index = Find(DefaultApplications, mimeType);
        if (index < 0)
            return false;

        var remaining = _lines[index][(_lines[index].IndexOf('=') + 1)..]
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => !string.Equals(name, entry, StringComparison.Ordinal))
            .ToArray();

        // The whole line goes when nothing is left, rather than an empty assignment: a
        // `inode/directory=` with no value is a line other readers have to guess about.
        if (remaining.Length == 0)
            _lines.RemoveAt(index);
        else
            _lines[index] = $"{mimeType}={string.Join(';', remaining)};";
        return true;
    }

    /// <summary>Writes the file, atomically, creating the directory if needed.</summary>
    public void Save(string path)
    {
        var temporary = path + ".wlrix-new";
        ApplicationPaths.EnsureDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(temporary, _lines);
        // The user's associations for every application are in here, so a half-written file
        // is a broken desktop rather than one missing setting.
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The index of a key's line within a group, or −1.</summary>
    private int Find(string group, string key)
    {
        var inGroup = false;
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i].Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inGroup = string.Equals(line, group, StringComparison.Ordinal);
                continue;
            }
            if (!inGroup)
                continue;

            var equals = line.IndexOf('=');
            if (equals > 0 && string.Equals(line[..equals].Trim(), key, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    /// <summary>Adds a line to a group, creating the group at the end if it is absent.</summary>
    private void Insert(string group, string line)
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            if (!string.Equals(_lines[i].Trim(), group, StringComparison.Ordinal))
                continue;

            // At the end of the group, which is the next group header or the end of the file.
            var at = i + 1;
            while (at < _lines.Count && !_lines[at].TrimStart().StartsWith('['))
                at++;
            // Past any blank lines the group was padded with, so the new key stays inside it.
            while (at > i + 1 && _lines[at - 1].Trim().Length == 0)
                at--;
            _lines.Insert(at, line);
            return;
        }

        if (_lines.Count > 0 && _lines[^1].Trim().Length > 0)
            _lines.Add(string.Empty);
        _lines.Add(group);
        _lines.Add(line);
    }
}
