// SPDX-License-Identifier: GPL-3.0-or-later

using Wlrix.Console.Services;
using Wlrix.Console.ViewModels;
using Xunit;

namespace Wlrix.Console.Tests;

public sealed class LogViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wlrix-console-tests-").FullName;
    private readonly string _path;
    private readonly Queue<Action> _posted = new();

    public LogViewModelTests() => _path = Path.Combine(_dir, "test.log");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ReadsBetweenUiTurnsArriveAsOneBatch()
    {
        var (log, tailer) = Create(maxLines: 100);
        var changes = 0;
        log.Lines.CollectionChanged += (_, _) => changes++;

        File.WriteAllText(_path, "one\n");
        tailer.ReadNew();
        File.AppendAllText(_path, "two\nthree\n");
        tailer.ReadNew();

        Assert.Single(_posted);
        Assert.Empty(log.Lines);
        RunPosted();

        Assert.Equal(["one", "two", "three"], log.Lines);
        Assert.Equal(1, changes);
        Assert.True(log.HasLines);
    }

    [Fact]
    public void ABacklogLargerThanTheViewKeepsOnlyWhatItShows()
    {
        var (log, tailer) = Create(maxLines: 1000);

        File.WriteAllText(_path, string.Concat(Enumerable.Range(0, 100_000).Select(i => $"line {i}\n")));
        tailer.ReadNew();
        RunPosted();

        Assert.Equal(1000, log.Lines.Count);
        Assert.Equal("line 99000", log.Lines[0]);
        Assert.Equal("line 99999", log.Lines[^1]);
    }

    [Fact]
    public void ARestartedLogReplacesWhatWasShown()
    {
        var (log, tailer) = Create(maxLines: 100);
        File.WriteAllText(_path, "old one\nold two\n");
        tailer.ReadNew();
        RunPosted();

        File.WriteAllText(_path, "new\n");
        tailer.ReadNew();
        RunPosted();

        Assert.Equal(["new"], log.Lines);
    }

    private (LogViewModel Log, LogTailer Tailer) Create(int maxLines)
    {
        var tailer = new LogTailer(_path);
        return (new LogViewModel("Test", tailer, maxLines, _posted.Enqueue), tailer);
    }

    private void RunPosted()
    {
        while (_posted.TryDequeue(out var action))
            action();
    }
}
