// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Media;
using Wlrix.Writer.Services;
using Xunit;

namespace Wlrix.Writer.Tests;

public class HighlightingTests
{
    private static readonly WriterTheme Theme = WriterTheme.Shared;

    [Theory]
    [InlineData("/src/main.c", "c")]
    [InlineData("/src/thing.cpp", "cpp")]
    [InlineData("/src/tool.py", "python")]
    [InlineData("/src/App.cs", "csharp")]
    [InlineData("/home/user/README.md", "markdown")]
    [InlineData("/etc/profile.sh", "shellscript")]
    [InlineData("/project/Makefile", "makefile")]
    [InlineData("/src/MAIN.C", "c")]
    public void Languages_are_guessed_from_the_file_name(string path, string expected)
    {
        Assert.Equal(expected, Theme.Languages.ForPath(path));
    }

    [Theory]
    [InlineData("/home/user/notes.txt")]
    [InlineData("/home/user/no-extension")]
    [InlineData("/home/user/archive.unknownext")]
    public void Anything_else_is_plain(string path)
    {
        Assert.Null(Theme.Languages.ForPath(path));
    }

    [Fact]
    public void The_menu_lists_only_languages_with_a_grammar_in_name_order()
    {
        var all = Theme.Languages.All;

        Assert.NotEmpty(all);
        Assert.All(all, mode => Assert.False(string.IsNullOrEmpty(mode.Scope)));
        Assert.Equal(all.OrderBy(mode => mode.Name, StringComparer.OrdinalIgnoreCase), all);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Both_themes_build_around_the_scheme_colors(bool dark)
    {
        var theme = Theme.Build(dark, Color.Parse("#000000"), Color.Parse("#8e8e8e"));

        Assert.NotNull(theme.GetTokenColors());
        Assert.NotEmpty(theme.GetTokenColors());
    }
}
