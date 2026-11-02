// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections;
using System.Collections.Specialized;
using Wlrix.Console.ViewModels;
using Xunit;

namespace Wlrix.Console.Tests;

public class LogLineBufferTests
{
    [Fact]
    public void KeepsTheNewestLinesUpToItsCapacity()
    {
        var buffer = new LogLineBuffer(1000);

        // A week of a busy log, in the batches the tailer hands over.
        for (var batch = 0; batch < 3000; batch++)
            buffer.AddRange(Enumerable.Range(batch * 500, 500).Select(i => $"line {i}").ToArray());

        Assert.Equal(1000, buffer.Count);
        Assert.Equal("line 1499000", buffer[0]);
        Assert.Equal("line 1499999", buffer[^1]);
        Assert.Equal(Enumerable.Range(1_499_000, 1000).Select(i => $"line {i}"), buffer);
    }

    [Fact]
    public void ABatchLargerThanTheCapacityKeepsItsEnd()
    {
        var buffer = new LogLineBuffer(3);
        buffer.AddRange(["a"]);
        var events = Record(buffer);

        buffer.AddRange(["1", "2", "3", "4", "5"]);

        Assert.Equal(["3", "4", "5"], buffer);
        Assert.Collection(events,
            e => AssertChange(e, NotifyCollectionChangedAction.Remove, 0, ["a"]),
            e => AssertChange(e, NotifyCollectionChangedAction.Add, 0, ["3", "4", "5"]));
    }

    [Fact]
    public void AnnouncesEachBatchAsOneRemovalAndOneAddition()
    {
        var buffer = new LogLineBuffer(4);
        buffer.AddRange(["a", "b", "c"]);
        var events = Record(buffer);

        buffer.AddRange(["d", "e", "f"]);

        Assert.Equal(["c", "d", "e", "f"], buffer);
        Assert.Collection(events,
            e => AssertChange(e, NotifyCollectionChangedAction.Remove, 0, ["a", "b"]),
            e => AssertChange(e, NotifyCollectionChangedAction.Add, 1, ["d", "e", "f"]));
    }

    [Fact]
    public void BelowCapacityOnlyAdds()
    {
        var buffer = new LogLineBuffer(10);
        var events = Record(buffer);

        buffer.AddRange(["a", "b"]);
        buffer.AddRange([]);

        AssertChange(Assert.Single(events), NotifyCollectionChangedAction.Add, 0, ["a", "b"]);
    }

    [Fact]
    public void ClearResets()
    {
        var buffer = new LogLineBuffer(2);
        buffer.AddRange(["a", "b", "c"]);
        var events = Record(buffer);

        buffer.Clear();
        buffer.AddRange(["d"]);

        Assert.Equal(["d"], buffer);
        Assert.Equal(NotifyCollectionChangedAction.Reset, events[0].Action);
    }

    [Fact]
    public void IndexesInPlaceAsAnIList()
    {
        // What lets the list control read lines without copying them all.
        IList buffer = new LogLineBuffer(3);
        ((LogLineBuffer)buffer).AddRange(["a", "b", "c", "d"]);

        Assert.Equal(3, buffer.Count);
        Assert.Equal("b", buffer[0]);
        Assert.Equal(2, buffer.IndexOf("d"));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[3]);
        Assert.Throws<NotSupportedException>(() => buffer.Add("e"));
    }

    private static List<NotifyCollectionChangedEventArgs> Record(LogLineBuffer buffer)
    {
        var events = new List<NotifyCollectionChangedEventArgs>();
        buffer.CollectionChanged += (_, e) => events.Add(e);
        return events;
    }

    private static void AssertChange(NotifyCollectionChangedEventArgs e, NotifyCollectionChangedAction action,
        int index, string[] items)
    {
        Assert.Equal(action, e.Action);
        if (action == NotifyCollectionChangedAction.Remove)
        {
            Assert.Equal(index, e.OldStartingIndex);
            Assert.Equal(items, e.OldItems!.Cast<string>());
        }
        else
        {
            Assert.Equal(index, e.NewStartingIndex);
            Assert.Equal(items, e.NewItems!.Cast<string>());
        }
    }
}
