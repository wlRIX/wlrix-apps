// SPDX-License-Identifier: GPL-3.0-or-later

namespace Wlrix.Writer.Models;

/// <summary>The files behind File ▸ Open Previous, most recent first.</summary>
/// <remarks>
/// More are remembered than are shown. The menu lists the ten most recent that still exist, and
/// keeping a few spare means deleting one file does not shorten the menu to nine.
/// </remarks>
public sealed class RecentFiles
{
    /// <summary>How many the menu shows.</summary>
    public const int Shown = 10;

    /// <summary>How many are remembered.</summary>
    public const int Capacity = 20;

    private readonly List<string> _paths;

    public RecentFiles(IEnumerable<string>? paths = null)
    {
        _paths = [];
        foreach (var path in paths ?? [])
        {
            if (!string.IsNullOrWhiteSpace(path) && !_paths.Contains(path, StringComparer.Ordinal))
                _paths.Add(path);
            if (_paths.Count == Capacity)
                break;
        }
    }

    /// <summary>Everything remembered, most recent first. What gets saved.</summary>
    public IReadOnlyList<string> All => _paths;

    /// <summary>Puts <paramref name="path"/> at the top, moving it there if it was already listed.</summary>
    public void Add(string path)
    {
        _paths.RemoveAll(existing => string.Equals(existing, path, StringComparison.Ordinal));
        _paths.Insert(0, path);
        if (_paths.Count > Capacity)
            _paths.RemoveRange(Capacity, _paths.Count - Capacity);
    }

    /// <summary>What the menu shows: the most recent <see cref="Shown"/> that pass <paramref name="exists"/>.</summary>
    public IReadOnlyList<string> Existing(Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        return [.. _paths.Where(exists).Take(Shown)];
    }
}
