// SPDX-License-Identifier: GPL-3.0-or-later

using Wlrix.ClipboardHistory.Models;
using Xunit;

namespace Wlrix.ClipboardHistory.Tests;

public class EntryTextTests
{
    [Fact]
    public void A_single_line_shows_whole_with_nothing_more()
    {
        Assert.Equal(("text 2", false), EntryText.Preview("text 2"));
    }

    [Fact]
    public void A_trailing_line_break_counts_as_more()
    {
        // What KDE's ↵ is for: pasting this into a terminal runs it.
        Assert.Equal(("text 1", true), EntryText.Preview("text 1\n"));
    }

    [Fact]
    public void Only_the_first_line_is_shown()
    {
        Assert.Equal(("first", true), EntryText.Preview("first\r\nsecond"));
    }

    [Fact]
    public void Leading_blank_lines_and_spaces_are_skipped()
    {
        Assert.Equal(("indented", false), EntryText.Preview("\n\n   indented  "));
    }

    [Fact]
    public void A_very_long_line_is_cut_and_marked()
    {
        var (line, more) = EntryText.Preview(new string('x', 5000));
        Assert.Equal(EntryText.MaxPreviewLength, line.Length);
        Assert.True(more);
    }

    [Fact]
    public void Tabs_do_not_open_gaps_in_the_row()
    {
        Assert.Equal(("a b", false), EntryText.Preview("a\tb"));
    }

    [Theory]
    [InlineData("Hello World", "world", true)]
    [InlineData("Hello World", "  hello ", true)]
    [InlineData("Hello World", "planet", false)]
    [InlineData("anything", "", true)]
    [InlineData("anything", null, true)]
    [InlineData("second line\nhas it", "has it", true)]
    public void Search_ignores_case_and_looks_at_every_line(string text, string? search, bool expected)
    {
        Assert.Equal(expected, EntryText.Matches(text, search));
    }
}
