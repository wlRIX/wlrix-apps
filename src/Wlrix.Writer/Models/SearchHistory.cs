// SPDX-License-Identifier: GPL-3.0-or-later

namespace Wlrix.Writer.Models;

/// <summary>
/// The strings searched for, behind the Find dialog's "use up arrow key to recall previous".
/// </summary>
/// <remarks>
/// Most recent first, each string once. Up steps back through it from the newest, Down steps
/// forward again, and going forward past the newest returns to whatever was typed before
/// recalling began. That is shell-history behavior, which is what the arrow keys promise.
/// </remarks>
public sealed class SearchHistory
{
    public const int Capacity = 20;

    private readonly List<string> _entries;

    /// <summary>Where recall is: -1 for the text being typed, otherwise an index into the entries.</summary>
    private int _position = -1;

    /// <summary>What was in the field when recall began, so Down can give it back.</summary>
    private string _typed = "";

    public SearchHistory(IEnumerable<string>? entries = null)
    {
        _entries = [];
        foreach (var entry in entries ?? [])
        {
            if (entry.Length > 0 && !_entries.Contains(entry, StringComparer.Ordinal))
                _entries.Add(entry);
            if (_entries.Count == Capacity)
                break;
        }
    }

    /// <summary>Everything remembered, most recent first. What gets saved.</summary>
    public IReadOnlyList<string> Entries => _entries;

    /// <summary>Records a search and resets recall to the field.</summary>
    public void Add(string text)
    {
        ResetRecall();
        if (text.Length == 0)
            return;
        _entries.RemoveAll(entry => string.Equals(entry, text, StringComparison.Ordinal));
        _entries.Insert(0, text);
        if (_entries.Count > Capacity)
            _entries.RemoveRange(Capacity, _entries.Count - Capacity);
    }

    /// <summary>Up: the next older string, or null at the oldest.</summary>
    /// <param name="current">What the field holds now, kept if this is the first step back.</param>
    public string? Older(string current)
    {
        var next = _position + 1;
        if (_position < 0)
        {
            // The field usually still holds the last search, which is also the newest entry;
            // recalling it would make the first press of Up appear to do nothing.
            _typed = current;
            while (next < _entries.Count && string.Equals(_entries[next], current, StringComparison.Ordinal))
                next++;
        }

        if (next >= _entries.Count)
            return null;
        _position = next;
        return _entries[next];
    }

    /// <summary>Down: the next newer string, the text typed before recall began, or null if not recalling.</summary>
    public string? Newer()
    {
        if (_position < 0)
            return null;
        return --_position < 0 ? _typed : _entries[_position];
    }

    /// <summary>Forgets where recall was, as typing in the field does.</summary>
    public void ResetRecall()
    {
        _position = -1;
        _typed = "";
    }
}
