// SPDX-License-Identifier: GPL-3.0-or-later

using Wlrix.ClipboardHistory.Models;
using Wlrix.ClipboardHistory.Services;
using Wlrix.ClipboardHistory.ViewModels;
using Xunit;

namespace Wlrix.ClipboardHistory.Tests;

public class HistoryViewModelTests
{
    /// <summary>Records what the popup asked the daemon for, and answers with a fixed history.</summary>
    private sealed class FakeHistory(params ClipboardEntry[] entries) : IClipboardHistory
    {
        public List<string> Requests { get; } = [];

        public ulong? Active { get; set; }

        public Task<IReadOnlyList<ClipboardEntry>> GetHistoryAsync() =>
            Task.FromResult<IReadOnlyList<ClipboardEntry>>(entries);

        public Task<ulong?> GetActiveAsync() => Task.FromResult(Active);

        public Task ActivateAsync(ulong id) => Record($"activate {id}");

        public Task SetTextAsync(ulong id, string text) => Record($"set-text {id} {text}");

        public Task RemoveAsync(ulong id) => Record($"remove {id}");

        public Task SetStarredAsync(ulong id, bool starred) => Record($"star {id} {starred}");

        public Task ClearAsync() => Record("clear");

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public void Dispose()
        {
        }

        private Task Record(string request)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private static ClipboardEntry Text(ulong id, string text, bool starred = false) =>
        new(id, EntryKind.Text, text, string.Empty, starred, DateTimeOffset.UnixEpoch);

    private static ClipboardEntry Image(ulong id) =>
        new(id, EntryKind.Image, string.Empty, "/nonexistent.png", false, DateTimeOffset.UnixEpoch);

    private static async Task<(HistoryViewModel Model, FakeHistory History)> Open(params ClipboardEntry[] entries)
    {
        var history = new FakeHistory(entries) { Active = entries.FirstOrDefault()?.Id };
        var model = new HistoryViewModel();
        await model.ConnectAsync(() => Task.FromResult<IClipboardHistory>(history));
        return (model, history);
    }

    [Fact]
    public async Task Both_tabs_show_newest_first_and_starred_is_a_subset()
    {
        var (model, _) = await Open(Text(3, "three"), Text(2, "two", starred: true), Text(1, "one"));
        Assert.Equal([3UL, 2, 1], model.All.Select(entry => entry.Id));
        Assert.Equal([2UL], model.Starred.Select(entry => entry.Id));
        Assert.Null(model.AllMessage);
        Assert.Null(model.StarredMessage);
    }

    [Fact]
    public async Task The_top_row_is_selected_and_marked_active()
    {
        var (model, _) = await Open(Text(3, "three"), Text(1, "one"));
        Assert.Equal(3UL, model.SelectedAll?.Id);
        Assert.True(model.All[0].IsActive);
        Assert.False(model.All[1].IsActive);
    }

    [Fact]
    public async Task Searching_narrows_both_tabs_and_skips_images()
    {
        var (model, _) = await Open(Text(4, "apple pie", starred: true), Image(3), Text(2, "banana"), Text(1, "Apple"));
        model.Search = "apple";
        Assert.Equal([4UL, 1], model.All.Select(entry => entry.Id));
        Assert.Equal([4UL], model.Starred.Select(entry => entry.Id));

        model.Search = "cherry";
        Assert.Empty(model.All);
        Assert.Equal(Localization.Strings.NoMatches, model.AllMessage);

        model.Search = string.Empty;
        Assert.Equal(4, model.All.Count);
    }

    [Fact]
    public async Task An_empty_history_says_so_on_each_tab()
    {
        var (model, _) = await Open();
        Assert.Equal(Localization.Strings.Empty, model.AllMessage);
        Assert.Equal(Localization.Strings.EmptyStarred, model.StarredMessage);
        Assert.False(model.CanClear);
    }

    [Fact]
    public async Task No_daemon_is_said_plainly()
    {
        var model = new HistoryViewModel();
        await model.ConnectAsync(() => throw new InvalidOperationException("no session bus to connect to"));
        Assert.Equal(Localization.Strings.NotRunning, model.AllMessage);
        Assert.Equal(Localization.Strings.NotRunning, model.StarredMessage);
    }

    [Fact]
    public async Task Choosing_an_entry_asks_for_it_and_closes()
    {
        var (model, history) = await Open(Text(2, "two"), Text(1, "one"));
        var closed = false;
        model.CloseRequested += () => closed = true;

        await model.ActivateAsync(model.All[1]);

        Assert.Equal(["activate 1"], history.Requests);
        Assert.True(closed);
    }

    [Fact]
    public async Task Rows_survive_a_refresh_and_take_on_new_text()
    {
        var (model, _) = await Open(Text(2, "two"), Text(1, "one"));
        var row = model.All[1];
        model.BeginEdit(row);

        model.Apply([Text(5, "five"), Text(2, "two"), Text(1, "uno")], active: 5);

        Assert.Same(row, model.All[2]);
        Assert.Equal("uno", row.Line);
        Assert.True(row.IsEditing, "an edit in progress is not lost to someone else's copy");
        Assert.True(model.All[0].IsActive);
    }

    [Fact]
    public async Task Only_one_row_is_edited_at_a_time()
    {
        var (model, _) = await Open(Text(2, "two"), Text(1, "one"));
        model.BeginEdit(model.All[0]);
        model.BeginEdit(model.All[1]);
        Assert.False(model.All[0].IsEditing);
        Assert.True(model.All[1].IsEditing);
        Assert.Equal("one", model.All[1].EditText);

        Assert.True(model.CancelEdits());
        Assert.False(model.CancelEdits());
    }

    [Fact]
    public async Task Saving_sends_the_new_text_and_nothing_when_unchanged()
    {
        var (model, history) = await Open(Text(1, "one"));
        var row = model.All[0];

        model.BeginEdit(row);
        await model.SaveEditAsync(row);
        Assert.Empty(history.Requests);

        model.BeginEdit(row);
        row.EditText = "uno";
        await model.SaveEditAsync(row);
        Assert.Equal(["set-text 1 uno"], history.Requests);
        Assert.False(row.IsEditing);
    }

    [Fact]
    public async Task Editing_down_to_nothing_deletes()
    {
        var (model, history) = await Open(Text(1, "one"));
        var row = model.All[0];
        model.BeginEdit(row);
        row.EditText = string.Empty;
        await model.SaveEditAsync(row);
        Assert.Equal(["remove 1"], history.Requests);
    }

    [Fact]
    public async Task Clear_is_only_offered_when_it_would_delete_something()
    {
        var (model, _) = await Open(Text(1, "one", starred: true));
        Assert.False(model.CanClear, "Clear keeps stars, so with only stars it would do nothing");
        model.Apply([Text(2, "two"), Text(1, "one", starred: true)], active: 2);
        Assert.True(model.CanClear);
    }
}
