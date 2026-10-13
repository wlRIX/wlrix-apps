// SPDX-License-Identifier: GPL-3.0-or-later

using Wlrix.Writer.Models;
using Xunit;

namespace Wlrix.Writer.Tests;

public class TextSearchTests
{
    private const string Text = "one two One three one";

    private static SearchMatch? Find(int start, int end, SearchQuery query) => TextSearch.Find(Text, start, end, query);

    [Fact]
    public void Forward_finds_the_next_match_after_the_caret()
    {
        Assert.Equal(new SearchMatch(8, 3, false), Find(1, 1, new SearchQuery("one")));
    }

    [Fact]
    public void Searching_again_steps_past_the_selected_match()
    {
        var first = Find(0, 0, new SearchQuery("one"))!.Value;
        var second = Find(first.Offset, first.Offset + first.Length, new SearchQuery("one"));

        Assert.Equal(0, first.Offset);
        Assert.Equal(8, second?.Offset);
    }

    [Fact]
    public void Case_sensitive_skips_other_cases()
    {
        Assert.Equal(18, Find(1, 1, new SearchQuery("one", CaseSensitive: true))?.Offset);
    }

    [Fact]
    public void The_end_of_the_document_wraps_to_the_start()
    {
        Assert.Equal(new SearchMatch(0, 3, true), Find(19, 19, new SearchQuery("one")));
    }

    [Fact]
    public void Backward_finds_the_match_before_the_selection_and_wraps_to_the_end()
    {
        Assert.Equal(new SearchMatch(8, 3, false), Find(18, 21, new SearchQuery("one", Backward: true)));
        Assert.Equal(new SearchMatch(18, 3, true), Find(0, 3, new SearchQuery("one", Backward: true)));
    }

    [Fact]
    public void The_only_match_is_found_again_rather_than_reported_missing()
    {
        Assert.Equal(new SearchMatch(4, 3, true), Find(4, 7, new SearchQuery("two")));
    }

    [Fact]
    public void Missing_text_is_not_found()
    {
        Assert.Null(Find(0, 0, new SearchQuery("four")));
    }

    [Fact]
    public void Plain_text_is_not_a_pattern()
    {
        Assert.Equal(4, TextSearch.Find("abc a.c", 0, 0, new SearchQuery("a.c"))?.Offset);
        Assert.Null(TextSearch.Find("abc", 0, 0, new SearchQuery("a.c")));
    }

    [Fact]
    public void Regular_expressions_match_lines_with_anchors()
    {
        var match = TextSearch.Find("int a;\nint b;\n", 1, 1, new SearchQuery("^int (\\w)", RegularExpression: true));

        Assert.Equal(new SearchMatch(7, 5, false), match);
    }

    [Fact]
    public void Whole_word_ignores_matches_inside_words()
    {
        const string text = "count counter recount count_x count";

        Assert.Equal(30, TextSearch.Find(text, 1, 1, new SearchQuery("count", WholeWord: true))?.Offset);
    }

    [Fact]
    public void Whole_word_works_for_patterns_ending_in_punctuation()
    {
        Assert.Equal(4, TextSearch.Find("ab: a: x", 1, 1, new SearchQuery("a:", WholeWord: true))?.Offset);
    }

    [Fact]
    public void An_empty_match_at_the_caret_does_not_stick()
    {
        var match = TextSearch.Find("ab\ncd\n", 0, 0, new SearchQuery("^", RegularExpression: true));

        Assert.Equal(3, match?.Offset);
    }

    [Fact]
    public void A_bad_pattern_says_so()
    {
        Assert.Throws<InvalidPatternException>(() =>
            TextSearch.Find("text", 0, 0, new SearchQuery("(unclosed", RegularExpression: true)));
    }
}

public class SearchHistoryTests
{
    [Fact]
    public void Up_walks_back_from_the_newest_and_stops_at_the_oldest()
    {
        var history = new SearchHistory(["new", "mid", "old"]);

        Assert.Equal("new", history.Older(""));
        Assert.Equal("mid", history.Older("new"));
        Assert.Equal("old", history.Older("mid"));
        Assert.Null(history.Older("old"));
    }

    [Fact]
    public void Down_walks_forward_and_returns_what_was_being_typed()
    {
        var history = new SearchHistory(["new", "old"]);
        history.Older("draft");
        history.Older("new");

        Assert.Equal("new", history.Newer());
        Assert.Equal("draft", history.Newer());
        Assert.Null(history.Newer());
    }

    [Fact]
    public void A_search_moves_its_string_to_the_top_and_resets_recall()
    {
        var history = new SearchHistory(["a", "b"]);
        history.Older("");

        history.Add("b");

        Assert.Equal(["b", "a"], history.Entries);
        Assert.Equal("b", history.Older(""));
    }

    [Fact]
    public void The_first_up_skips_the_string_already_in_the_field()
    {
        var history = new SearchHistory(["last", "before"]);

        Assert.Equal("before", history.Older("last"));
        Assert.Equal("last", history.Newer());
    }

    [Fact]
    public void Only_the_capacity_is_kept()
    {
        var history = new SearchHistory();
        for (var i = 0; i < SearchHistory.Capacity + 3; i++)
            history.Add($"s{i}");

        Assert.Equal(SearchHistory.Capacity, history.Entries.Count);
        Assert.Equal($"s{SearchHistory.Capacity + 2}", history.Entries[0]);
    }
}
