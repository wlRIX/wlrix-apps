using System.Collections;
using System.Collections.Specialized;

namespace Wlrix.Console.ViewModels;

/// <summary>
/// The most recent lines of a log, at most <see cref="Capacity"/> of them, as a list a virtualized
/// view can bind to. Appending past the capacity drops the oldest lines from the front.
/// </summary>
/// <remarks>
/// A ring buffer rather than an <c>ObservableCollection</c>: dropping from the front of a list
/// moves every line after it, once per dropped line, and a live log drops one for each it adds.
/// Changes are announced a batch at a time — one range removal from the front, one range
/// addition at the end — which the virtualizing panel handles without touching any line that is
/// not on screen. Not thread-safe; the view model only touches it on the UI thread.
/// </remarks>
public sealed class LogLineBuffer : IList, IReadOnlyList<string>, INotifyCollectionChanged
{
    private readonly string[] _items;
    private int _head;
    private int _count;

    public LogLineBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new string[capacity];
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>The most lines kept.</summary>
    public int Capacity => _items.Length;

    public int Count => _count;

    public string this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _items[(_head + index) % _items.Length];
        }
    }

    /// <summary>
    /// Appends <paramref name="lines"/>, first dropping as many of the oldest lines as it takes to
    /// stay within <see cref="Capacity"/>. Of a batch larger than the capacity only its last
    /// lines are kept.
    /// </summary>
    public void AddRange(IReadOnlyList<string> lines)
    {
        var skip = Math.Max(0, lines.Count - Capacity);
        var adding = lines.Count - skip;
        if (adding == 0)
            return;

        var dropping = Math.Max(0, _count + adding - Capacity);
        if (dropping > 0)
        {
            var dropped = new string[dropping];
            for (var i = 0; i < dropping; i++)
            {
                var slot = (_head + i) % _items.Length;
                dropped[i] = _items[slot];
                _items[slot] = null!;
            }

            _head = (_head + dropping) % _items.Length;
            _count -= dropping;
            CollectionChanged?.Invoke(this,
                new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, dropped, 0));
        }

        var start = _count;
        var added = new string[adding];
        for (var i = 0; i < adding; i++)
        {
            added[i] = lines[skip + i];
            _items[(_head + _count + i) % _items.Length] = added[i];
        }

        _count += adding;
        CollectionChanged?.Invoke(this,
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, added, start));
    }

    /// <summary>Drops every line.</summary>
    public void Clear()
    {
        if (_count == 0)
            return;

        Array.Clear(_items);
        _head = 0;
        _count = 0;
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public IEnumerator<string> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // The non-generic IList is what lets Avalonia's ItemsSourceView index the lines in place;
    // given only IReadOnlyList it would copy them. It is read-only to everyone else.
    bool IList.IsFixedSize => false;
    bool IList.IsReadOnly => true;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    int IList.IndexOf(object? value)
    {
        for (var i = 0; i < _count; i++)
            if (Equals(this[i], value))
                return i;
        return -1;
    }

    bool IList.Contains(object? value) => ((IList)this).IndexOf(value) >= 0;

    void ICollection.CopyTo(Array array, int index)
    {
        for (var i = 0; i < _count; i++)
            array.SetValue(this[i], index + i);
    }

    int IList.Add(object? value) => throw new NotSupportedException();
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void IList.RemoveAt(int index) => throw new NotSupportedException();
    void IList.Clear() => Clear();
}
