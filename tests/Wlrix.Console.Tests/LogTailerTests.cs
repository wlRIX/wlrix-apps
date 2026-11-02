// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using Wlrix.Console.Services;
using Xunit;

namespace Wlrix.Console.Tests;

public sealed class LogTailerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wlrix-console-tests-").FullName;
    private readonly string _path;

    public LogTailerTests() => _path = Path.Combine(_dir, "test.log");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void AWeekOfLogReadsOnlyTheBacklogAtTheEnd()
    {
        // About 150 MB, the size of a session log after a few days.
        const int count = 1_500_000;
        using (var writer = new StreamWriter(_path, false, new UTF8Encoding(false)))
            for (var i = 0; i < count; i++)
                writer.Write($"[{i:D8}] wlrix-session: some ordinary line of log output, padded out to about a hundred bytes\n");
        Assert.True(new FileInfo(_path).Length > 140_000_000);

        const int backlog = 1024 * 1024;
        var (tailer, batches) = Tail(backlog);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        tailer.ReadNew();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

        var lines = Assert.Single(batches);
        // Whole lines only, ending with the last one written.
        Assert.All(lines, line => Assert.Matches(@"^\[\d{8}\] wlrix-session: .*hundred bytes$", line));
        Assert.StartsWith($"[{count - 1:D8}]", lines[^1]);
        Assert.InRange(lines.Count, backlog / 120, backlog / 90);
        // The bytes, the strings made of them and the list: a few times the backlog, nothing like
        // the file.
        Assert.InRange(allocated, 0, 6L * backlog);
    }

    [Fact]
    public void ALineStartingExactlyAtTheBacklogIsKept()
    {
        File.WriteAllText(_path, "first\nsecond\nthird\n");
        var (tailer, batches) = Tail("second\nthird\n".Length);

        tailer.ReadNew();

        Assert.Equal(["second", "third"], Assert.Single(batches));
    }

    [Fact]
    public void ALinePartlyInsideTheBacklogIsDropped()
    {
        File.WriteAllText(_path, "first\nsecond\nthird\n");
        var (tailer, batches) = Tail("cond\nthird\n".Length);

        tailer.ReadNew();

        Assert.Equal(["third"], Assert.Single(batches));
    }

    [Fact]
    public void AppendedLinesArriveOnTheirOwn()
    {
        File.WriteAllText(_path, "one\ntwo\n");
        var (tailer, batches) = Tail(1024);
        tailer.ReadNew();

        File.AppendAllText(_path, "three\nfour\n");
        tailer.ReadNew();
        tailer.ReadNew();

        Assert.Equal(2, batches.Count);
        Assert.Equal(["three", "four"], batches[1]);
    }

    [Fact]
    public void ALineStillBeingWrittenWaitsForItsEnd()
    {
        File.WriteAllText(_path, "done\nhalf");
        var (tailer, batches) = Tail(1024);
        tailer.ReadNew();
        Assert.Equal(["done"], Assert.Single(batches));

        File.AppendAllText(_path, " a line\n");
        tailer.ReadNew();

        Assert.Equal(["half a line"], batches[1]);
    }

    [Fact]
    public void ABurstLargerThanTheBacklogSkipsToItsEnd()
    {
        File.WriteAllText(_path, "before\n");
        var (tailer, batches) = Tail(64);
        tailer.ReadNew();

        File.AppendAllText(_path, string.Concat(Enumerable.Range(0, 1000).Select(i => $"burst {i:D4}\n")));
        tailer.ReadNew();

        var burst = batches[1];
        Assert.Equal("burst 0999", burst[^1]);
        Assert.InRange(burst.Count, 1, 64 / "burst 0000\n".Length);
        Assert.All(burst, line => Assert.Matches(@"^burst \d{4}$", line));
    }

    [Fact]
    public void AnEndlessLineLongerThanTheBacklogIsSkipped()
    {
        File.WriteAllText(_path, new string('x', 500));
        var (tailer, batches) = Tail(64);
        tailer.ReadNew();
        Assert.Empty(batches);

        File.AppendAllText(_path, "x\nafter\n");
        tailer.ReadNew();

        // The tail of the long line ended inside the backlog without its start, so it is dropped.
        Assert.Equal(["after"], Assert.Single(batches));
    }

    [Fact]
    public void ATruncatedLogStartsOver()
    {
        File.WriteAllText(_path, "old run line one\nold run line two\n");
        var (tailer, batches) = Tail(1024);
        var restarts = 0;
        tailer.Restarted += () => restarts++;
        tailer.ReadNew();

        File.WriteAllText(_path, "new run\n");
        tailer.ReadNew();

        Assert.Equal(1, restarts);
        Assert.Equal(["new run"], batches[1]);
    }

    [Fact]
    public void LinesAreDecodedAsUtf8WithoutTheirEndings()
    {
        File.WriteAllText(_path, "crlf\r\n\nウェイランド\n", new UTF8Encoding(false));
        var (tailer, batches) = Tail(1024);

        tailer.ReadNew();

        Assert.Equal(["crlf", "", "ウェイランド"], Assert.Single(batches));
    }

    [Fact]
    public void AMissingFileIsNotAnError()
    {
        var (tailer, batches) = Tail(1024);
        tailer.ReadNew();
        Assert.Empty(batches);
    }

    private (LogTailer Tailer, List<IReadOnlyList<string>> Batches) Tail(int backlog)
    {
        var tailer = new LogTailer(_path, backlog);
        var batches = new List<IReadOnlyList<string>>();
        tailer.LinesAppended += batches.Add;
        return (tailer, batches);
    }
}
