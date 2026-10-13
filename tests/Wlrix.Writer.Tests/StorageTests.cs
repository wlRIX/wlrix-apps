// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.Versioning;
using System.Text;
using Wlrix.Writer.Models;
using Wlrix.Writer.Services;
using Xunit;

namespace Wlrix.Writer.Tests;

/// <summary>A directory of its own under the system temp directory, removed afterwards.</summary>
public sealed class Scratch : IDisposable
{
    public Scratch()
    {
        Root = Path.Combine(Path.GetTempPath(), "wlrix-writer-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(string name) => Path.Combine(Root, name);

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

// The editor only runs on Linux, and these check Unix permission bits.
[SupportedOSPlatform("linux")]
public sealed class DocumentIOTests : IDisposable
{
    private readonly Scratch _scratch = new();

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public void Saving_replaces_the_file_and_keeps_its_permissions()
    {
        var path = _scratch.File("script.sh");
        System.IO.File.WriteAllText(path, "old\n");
        System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        DocumentIO.Save(path, "new\n", TextFormat.Default);

        Assert.Equal("new\n", System.IO.File.ReadAllText(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            System.IO.File.GetUnixFileMode(path));
        // Nothing left behind from the write-and-rename.
        Assert.Single(Directory.GetFileSystemEntries(_scratch.Root));
    }

    [Fact]
    public void Saving_through_a_symbolic_link_writes_the_target_and_keeps_the_link()
    {
        var target = _scratch.File("real.txt");
        var link = _scratch.File("link.txt");
        System.IO.File.WriteAllText(target, "old\n");
        System.IO.File.CreateSymbolicLink(link, target);

        DocumentIO.Save(link, "new\n", TextFormat.Default);

        Assert.Equal(target, new FileInfo(link).LinkTarget);
        Assert.Equal("new\n", System.IO.File.ReadAllText(target));
    }

    [Fact]
    public void The_stamp_a_save_returns_is_the_file_as_it_now_is()
    {
        var path = _scratch.File("notes.txt");

        var (stamp, _) = DocumentIO.Save(path, "some text\n", TextFormat.Default);

        Assert.Equal(DocumentIO.Probe(path), stamp);
        Assert.Equal(10, stamp.Length);
    }

    [Fact]
    public void Load_reads_text_format_and_stamp()
    {
        var path = _scratch.File("dos.txt");
        System.IO.File.WriteAllBytes(path, Encoding.ASCII.GetBytes("a\r\nb\r\n"));

        var file = DocumentIO.Load(path);

        Assert.Equal("a\nb\n", file.Text);
        Assert.Equal(LineEnding.CrLf, file.Format.LineEnding);
        Assert.Equal(DocumentIO.Probe(path), file.Stamp);
    }

    [Fact]
    public void Probe_of_a_missing_file_is_null()
    {
        Assert.Null(DocumentIO.Probe(_scratch.File("nothing")));
    }
}

public class RecentFilesTests
{
    [Fact]
    public void Newest_is_first_and_reopening_moves_a_file_to_the_top()
    {
        var recent = new RecentFiles();
        recent.Add("/a");
        recent.Add("/b");
        recent.Add("/a");

        Assert.Equal(["/a", "/b"], recent.All);
    }

    [Fact]
    public void Only_the_capacity_is_remembered()
    {
        var recent = new RecentFiles();
        for (var i = 0; i < RecentFiles.Capacity + 5; i++)
            recent.Add($"/file{i}");

        Assert.Equal(RecentFiles.Capacity, recent.All.Count);
        Assert.Equal($"/file{RecentFiles.Capacity + 4}", recent.All[0]);
    }

    [Fact]
    public void The_menu_shows_ten_that_still_exist()
    {
        var recent = new RecentFiles(Enumerable.Range(0, 15).Select(i => $"/file{i}"));

        // Every other one deleted: the menu still fills to ten from the spares.
        var shown = recent.Existing(path => int.Parse(path[5..]) % 3 != 0);

        Assert.Equal(RecentFiles.Shown, shown.Count);
        Assert.DoesNotContain("/file0", shown);
        Assert.Equal("/file1", shown[0]);
    }

    [Fact]
    public void A_loaded_list_is_cleaned_of_blanks_and_duplicates()
    {
        var recent = new RecentFiles(["/a", "", "/a", "  ", "/b"]);

        Assert.Equal(["/a", "/b"], recent.All);
    }
}

public sealed class WriterStateTests : IDisposable
{
    private readonly Scratch _scratch = new();

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public void A_missing_file_reads_as_the_defaults()
    {
        var state = new WriterState(_scratch.File("state.json"));

        Assert.Empty(state.Document.Recent);
        Assert.Equal(TabSettings.Default, state.Document.DefaultTabs);
        Assert.False(state.Document.ShowLineNumbers);
    }

    [Fact]
    public void A_corrupt_file_reads_as_the_defaults_and_is_reported()
    {
        var path = _scratch.File("state.json");
        System.IO.File.WriteAllText(path, "{ not json");
        var warnings = new List<string>();

        var state = new WriterState(path, warnings.Add);

        Assert.Empty(state.Document.Recent);
        Assert.Single(warnings);
    }

    [Fact]
    public void Updates_survive_a_restart()
    {
        var path = _scratch.File("sub/state.json");
        new WriterState(path).Update(state => state with
        {
            Recent = ["/x"],
            DefaultTabs = new TabSettings(4, true),
            ShowLineNumbers = true,
        });

        var reread = new WriterState(path).Document;

        Assert.Equal(["/x"], reread.Recent);
        Assert.Equal(new TabSettings(4, true), reread.DefaultTabs);
        Assert.True(reread.ShowLineNumbers);
    }

    [Fact]
    public void A_tab_width_out_of_range_is_pulled_back_in()
    {
        var path = _scratch.File("state.json");
        System.IO.File.WriteAllText(path, """{ "version": 1, "defaultTabs": { "width": 99, "useSpaces": false } }""");

        Assert.Equal(TabSettings.MaxWidth, new WriterState(path).Document.DefaultTabs.Width);
    }

    [Fact]
    public void A_file_from_a_newer_version_is_not_misread()
    {
        var path = _scratch.File("state.json");
        System.IO.File.WriteAllText(path, """{ "version": 99, "recent": ["/x"] }""");

        Assert.Empty(new WriterState(path).Document.Recent);
    }
}

public class NamingTests
{
    [Fact]
    public void Untitled_numbers_count_up_and_reuse_gaps()
    {
        Assert.Equal(1, UntitledNames.Next([]));
        Assert.Equal(2, UntitledNames.Next([1]));
        Assert.Equal(2, UntitledNames.Next([1, 3]));
    }

    [Fact]
    public void The_first_untitled_document_has_no_number()
    {
        Assert.Equal("Untitled", UntitledNames.Format("Untitled", 1));
        Assert.Equal("Untitled 2", UntitledNames.Format("Untitled", 2));
    }
}
