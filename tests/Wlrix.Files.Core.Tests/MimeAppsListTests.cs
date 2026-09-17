using Wlrix.Files.Core.Platform;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// The file that decides what opens a directory. It is also the file that decides what opens
/// everything else the user has ever chosen a handler for, so most of these are about what
/// survives a write rather than what it changes.
/// </summary>
public class MimeAppsListTests
{
    private const string Files = "com.wlrix.files.desktop";
    private const string Directory = "inode/directory";

    private static MimeAppsList From(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), "wlrix-mimeapps", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        try
        {
            return MimeAppsList.Read(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMissingFileReadsAsAnEmptyList()
    {
        var list = MimeAppsList.Read(Path.Combine(Path.GetTempPath(), "wlrix-no-such-mimeapps.list"));
        Assert.Empty(list.Lines);
        Assert.Null(list.DefaultFor(Directory));
    }

    [Fact]
    public void SettingADefaultInAnEmptyFileCreatesTheGroup()
    {
        var list = From("");
        list.SetDefault(Directory, Files);

        Assert.Contains(MimeAppsList.DefaultApplications, list.Lines);
        Assert.Equal(Files, list.DefaultFor(Directory));
    }

    [Fact]
    public void EverythingElseInTheFileSurvivesTheWrite()
    {
        // The user's associations for every application they have ever chosen a handler for
        // live in here. Rewriting the file from a parsed model would quietly drop whatever
        // this code does not understand.
        var list = From("""
            # my associations
            [Added Associations]
            application/pdf=org.pwmt.zathura.desktop;

            [Default Applications]
            application/pdf=org.pwmt.zathura.desktop;
            image/png=imv.desktop;

            [X-Something-Future]
            key=value
            """);

        list.SetDefault(Directory, Files);
        var written = string.Join('\n', list.Lines);

        Assert.Contains("# my associations", written, StringComparison.Ordinal);
        Assert.Contains("[Added Associations]", written, StringComparison.Ordinal);
        Assert.Contains("image/png=imv.desktop;", written, StringComparison.Ordinal);
        Assert.Contains("[X-Something-Future]", written, StringComparison.Ordinal);
        Assert.Contains("key=value", written, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNewDefaultGoesInTheDefaultApplicationsGroupAndNotAnother()
    {
        // Both groups can hold the same key. Writing into Added Associations would add the
        // application to the Open With list and change nothing about what actually opens.
        var list = From("""
            [Added Associations]
            inode/directory=org.gnome.Nautilus.desktop;

            [Default Applications]
            image/png=imv.desktop;
            """);
        list.SetDefault(Directory, Files);

        var lines = list.Lines.ToList();
        var added = lines.IndexOf(MimeAppsList.AddedAssociations);
        var defaults = lines.IndexOf(MimeAppsList.DefaultApplications);
        var ours = lines.FindIndex(line => line.StartsWith($"{Directory}={Files}", StringComparison.Ordinal));

        Assert.True(ours > defaults, "the new default belongs to [Default Applications]");
        Assert.True(added < defaults);
        // And the Added Associations entry for the same key is left exactly as it was.
        Assert.Contains($"{Directory}=org.gnome.Nautilus.desktop;", lines);
    }

    [Fact]
    public void ThePreviousDefaultIsPushedDownRatherThanDiscarded()
    {
        // So that clearing ours later falls back to whatever the user had, rather than to
        // nothing at all.
        var list = From($"""
            [Default Applications]
            {Directory}=org.gnome.Nautilus.desktop;
            """);
        list.SetDefault(Directory, Files);

        Assert.Equal($"{Directory}={Files};org.gnome.Nautilus.desktop;", list.Lines[1]);
        Assert.Equal(Files, list.DefaultFor(Directory));
    }

    [Fact]
    public void SettingTheSameDefaultTwiceDoesNotListItTwice()
    {
        var list = From("");
        list.SetDefault(Directory, Files);
        list.SetDefault(Directory, Files);

        Assert.Equal($"{Directory}={Files};", list.Lines[^1]);
    }

    [Fact]
    public void ClearingPutsTheOldHandlerBack()
    {
        var list = From($"""
            [Default Applications]
            {Directory}=org.gnome.Nautilus.desktop;
            """);
        list.SetDefault(Directory, Files);

        Assert.True(list.ClearDefault(Directory, Files));
        Assert.Equal("org.gnome.Nautilus.desktop", list.DefaultFor(Directory));
    }

    [Fact]
    public void ClearingTheOnlyHandlerRemovesTheLineRatherThanEmptyingIt()
    {
        // An `inode/directory=` with no value is a line every other reader has to guess about.
        var list = From("");
        list.SetDefault(Directory, Files);
        Assert.True(list.ClearDefault(Directory, Files));

        Assert.DoesNotContain(list.Lines, line => line.StartsWith(Directory, StringComparison.Ordinal));
        Assert.Null(list.DefaultFor(Directory));
    }

    [Fact]
    public void ClearingSomethingThatIsNotThereSaysSo()
    {
        Assert.False(From("").ClearDefault(Directory, Files));
    }

    [Fact]
    public void OnlyTheFirstHandlerInThePreferenceListIsTheDefault()
    {
        var list = From($"""
            [Default Applications]
            {Directory}=first.desktop;second.desktop;third.desktop;
            """);
        Assert.Equal("first.desktop", list.DefaultFor(Directory));
    }

    [Fact]
    public void AKeyInAnotherGroupIsNotMistakenForTheDefault()
    {
        var list = From($"""
            [Added Associations]
            {Directory}=org.gnome.Nautilus.desktop;
            """);
        Assert.Null(list.DefaultFor(Directory));
    }

    [Fact]
    public void TheGroupIsAddedAtTheEndWhenTheFileHasOtherGroupsButNotThatOne()
    {
        var list = From("""
            [Added Associations]
            image/png=imv.desktop;
            """);
        list.SetDefault(Directory, Files);

        Assert.Contains(MimeAppsList.DefaultApplications, list.Lines);
        Assert.Equal($"{Directory}={Files};", list.Lines[^1]);
    }

    [Fact]
    public void ANewKeyLandsInsideItsGroupAndNotAfterTheBlankLineFollowingIt()
    {
        // Appending after the trailing blank line would put the key in the *next* group, or
        // in no group at all if it were the last one.
        var list = From("""
            [Default Applications]
            image/png=imv.desktop;

            [Added Associations]
            application/pdf=zathura.desktop;
            """);
        list.SetDefault(Directory, Files);

        var lines = list.Lines.ToList();
        var ours = lines.FindIndex(line => line.StartsWith(Directory, StringComparison.Ordinal));
        var added = lines.IndexOf(MimeAppsList.AddedAssociations);

        Assert.True(ours < added, "the key must stay inside [Default Applications]");
    }

    [Fact]
    public void SavingRoundTripsThroughTheFileAndLeavesNoPartBehind()
    {
        using var dir = new TemporaryDirectory();
        var path = Path.Combine(dir.Path, "config", "mimeapps.list");

        var list = MimeAppsList.Read(path);
        list.SetDefault(Directory, Files);
        list.Save(path);

        Assert.Equal(Files, MimeAppsList.Read(path).DefaultFor(Directory));
        Assert.Empty(System.IO.Directory.GetFiles(Path.GetDirectoryName(path)!, "*.wlrix-new"));
    }
}
