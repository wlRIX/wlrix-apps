using System.Text.Json;
using Wlrix.Files.Core.Portal;
using Xunit;

namespace Wlrix.Files.Core.Tests;

public class FileFilterTests
{
    private static FileFilter Filter(params FilterRule[] rules) =>
        new() { Name = "Test", Rules = rules };

    /// <summary>Anything asked about a type when the test does not expect to be asked.</summary>
    private static string NotAsked() =>
        throw new InvalidOperationException("the type should not have been resolved");

    [Fact]
    public void AFilterWithNoRulesIsHowAnApplicationSpellsAllFiles()
    {
        Assert.True(Filter().Matches("anything.at.all", NotAsked));
    }

    [Fact]
    public void AGlobRuleMatchesByExtension()
    {
        var images = Filter(new FilterRule(IsMimeType: false, "*.png"));
        Assert.True(images.Matches("holiday.png", NotAsked));
        Assert.False(images.Matches("holiday.jpg", NotAsked));
        // Anchored, as a glob is everywhere else: this is not a substring search.
        Assert.False(images.Matches("holiday.png.bak", NotAsked));
    }

    /// <summary>
    /// A camera writes IMG_0001.JPG, and an image editor asks for *.jpg. Hiding the picture
    /// would be the dialog looking broken with the file plainly visible elsewhere.
    /// </summary>
    [Fact]
    public void GlobsIgnoreCase()
    {
        Assert.True(Filter(new FilterRule(false, "*.jpg")).Matches("IMG_0001.JPG", NotAsked));
    }

    [Fact]
    public void AnyOneRuleIsEnough()
    {
        var images = Filter(
            new FilterRule(false, "*.png"),
            new FilterRule(false, "*.jpg"),
            new FilterRule(IsMimeType: true, "image/svg+xml"));

        Assert.True(images.Matches("a.jpg", NotAsked));
        Assert.True(images.Matches("drawing.svg", () => "image/svg+xml"));
        Assert.False(images.Matches("notes.txt", () => "text/plain"));
    }

    /// <summary>
    /// Resolving a type costs a lookup at least and a read of the file's head at worst, and a
    /// filter answered by its extension rules must never pay it.
    /// </summary>
    [Fact]
    public void TheTypeIsOnlyResolvedWhenAMimeRuleIsReached()
    {
        var asked = 0;
        var filter = Filter(
            new FilterRule(false, "*.png"),
            new FilterRule(IsMimeType: true, "image/jpeg"));

        Assert.True(filter.Matches("a.png", () => { asked++; return "image/png"; }));
        Assert.Equal(0, asked);

        Assert.True(filter.Matches("a.jpeg", () => { asked++; return "image/jpeg"; }));
        Assert.Equal(1, asked);
    }

    /// <summary>Resolved once however many MIME rules follow.</summary>
    [Fact]
    public void TheTypeIsResolvedAtMostOnce()
    {
        var asked = 0;
        var filter = Filter(
            new FilterRule(true, "image/png"),
            new FilterRule(true, "image/gif"),
            new FilterRule(true, "image/jpeg"));

        Assert.False(filter.Matches("x", () => { asked++; return "text/plain"; }));
        Assert.Equal(1, asked);
    }

    /// <summary>
    /// Not in the interface's vocabulary, but applications send it and GTK honors it.
    /// </summary>
    [Fact]
    public void AMediaWildcardMatchesEveryTypeInIt()
    {
        var text = Filter(new FilterRule(true, "text/*"));
        Assert.True(text.Matches("a", () => "text/x-csrc"));
        Assert.False(text.Matches("a", () => "image/png"));
        // And does not reach past the separator into a type that merely starts the same way.
        Assert.False(Filter(new FilterRule(true, "image/*")).Matches("a", () => "imagex/png"));
    }
}

public class FileChooserRequestTests
{
    private static readonly Location Home = Location.FromLocalPath("/home/vic");

    [Fact]
    public void CurrentFolderIsWhereTheDialogOpens()
    {
        var request = new FileChooserRequest { CurrentFolder = "/mnt/Dev" };
        Assert.Equal(Location.FromLocalPath("/mnt/Dev"), request.StartingFolder(Home, _ => true));
    }

    /// <summary>Saving over a file means starting in that file's own folder.</summary>
    [Fact]
    public void CurrentFileDecidesWhenNoFolderWasGiven()
    {
        var request = new FileChooserRequest { CurrentFile = "/mnt/Dev/notes.txt" };
        Assert.Equal(Location.FromLocalPath("/mnt/Dev"), request.StartingFolder(Home, _ => true));
    }

    /// <summary>
    /// A folder that has since been removed would open an empty listing with no explanation.
    /// </summary>
    [Fact]
    public void AFolderThatIsNotThereFallsBackToHome()
    {
        var request = new FileChooserRequest { CurrentFolder = "/mnt/gone" };
        Assert.Equal(Home, request.StartingFolder(Home, _ => false));
    }

    [Fact]
    public void TheSaveFieldPrefersWhatTheApplicationSuggested()
    {
        Assert.Equal("draft.odt", new FileChooserRequest { CurrentName = "draft.odt" }.StartingName());
        // Save As on an open document offers that document's name rather than an empty box.
        Assert.Equal("report.odt", new FileChooserRequest { CurrentFile = "/x/report.odt" }.StartingName());
        Assert.Equal("", new FileChooserRequest().StartingName());
    }
}

public class FileChooserWireTests
{
    /// <summary>
    /// The backend writes this JSON and the picker reads it. They are built separately, so a
    /// name that drifts is a dialog that offers nothing rather than a build error.
    /// </summary>
    [Fact]
    public void TheManifestReadsBackAsTheBackendWritesIt()
    {
        const string json = """
            {
              "mode": "save_files",
              "app_id": "org.example.Mail",
              "title": "Save Attachments",
              "accept_label": "_Save",
              "multiple": false,
              "directory": false,
              "current_name": "",
              "current_folder": "/home/vic/Downloads",
              "current_file": "",
              "files": ["invoice.pdf", "photo.jpg"],
              "filters": [
                {"name": "Images", "rules": [{"mime": false, "pattern": "*.png"},
                                             {"mime": true, "pattern": "image/jpeg"}]}
              ],
              "current_filter": 0,
              "choices": [
                {"id": "encoding", "label": "Encoding",
                 "options": [{"id": "utf8", "label": "UTF-8"}], "default": "utf8"},
                {"id": "readonly", "label": "Open read-only", "options": [], "default": "false"}
              ]
            }
            """;

        var request = JsonSerializer.Deserialize(json, FileChooserJson.Default.FileChooserRequest);

        Assert.NotNull(request);
        Assert.Equal(FileChooserMode.SaveFiles, request.Mode);
        Assert.Equal("org.example.Mail", request.AppId);
        Assert.Equal(["invoice.pdf", "photo.jpg"], request.Files);
        Assert.Equal("Images", Assert.Single(request.Filters).Name);
        Assert.Equal(2, request.Filters[0].Rules.Count);
        Assert.False(request.Filters[0].Rules[0].IsMimeType);
        Assert.True(request.Filters[0].Rules[1].IsMimeType);
        Assert.Equal(0, request.CurrentFilter);

        // A choice with no options is a checkbox, which is the interface's own rule and not
        // something a dialog can guess from the label.
        Assert.False(request.Choices[0].IsBoolean);
        Assert.True(request.Choices[1].IsBoolean);
    }

    [Fact]
    public void TheAnswerIsWrittenInTheShapeTheBackendParses()
    {
        var result = new FileChooserResult
        {
            Uris = ["file:///home/vic/a.txt"],
            Choices = [new ChoiceAnswer("readonly", "true")],
            CurrentFilter = 2,
            Writable = true,
        };

        var json = JsonSerializer.Serialize(result, FileChooserJson.Default.FileChooserResult);

        Assert.Contains("\"uris\":[\"file:///home/vic/a.txt\"]", json);
        Assert.Contains("\"choices\":[{\"id\":\"readonly\",\"value\":\"true\"}]", json);
        Assert.Contains("\"current_filter\":2", json);
        Assert.Contains("\"writable\":true", json);
    }

    /// <summary>
    /// A newer frontend passing a key from a later revision must not break a file dialog, the
    /// same leniency the screenshot options are read with.
    /// </summary>
    [Fact]
    public void AnUnknownKeyIsIgnored()
    {
        var request = JsonSerializer.Deserialize(
            """{"mode": "open", "something_new": 1}""", FileChooserJson.Default.FileChooserRequest);

        Assert.NotNull(request);
        Assert.Equal(FileChooserMode.Open, request.Mode);
    }
}

public class SaveFilesPlanTests
{
    private static readonly Location Folder = Location.FromLocalPath("/home/vic/Downloads");

    [Fact]
    public void NamesThatAreFreeAreUsedAsTheyCame()
    {
        var chosen = SaveFilesPlan.Resolve(Folder, ["a.txt", "b.txt"], _ => false);

        Assert.Equal(
            [Folder.Child("a.txt"), Folder.Child("b.txt")],
            chosen);
    }

    [Fact]
    public void ANameAlreadyOnDiskIsRenamedRatherThanOverwritten()
    {
        var chosen = SaveFilesPlan.Resolve(Folder, ["notes.txt"], name => name == "notes.txt");

        Assert.Equal(Folder.Child("notes (copy).txt"), Assert.Single(chosen));
    }

    /// <summary>
    /// Two attachments with one name is a real case, and checking only the disk would answer
    /// the same URI twice and lose a file.
    /// </summary>
    [Fact]
    public void TwoOfTheSameNameInOneRequestDoNotCollide()
    {
        var chosen = SaveFilesPlan.Resolve(Folder, ["invoice.pdf", "invoice.pdf"], _ => false);

        Assert.Equal(Folder.Child("invoice.pdf"), chosen[0]);
        Assert.Equal(Folder.Child("invoice (copy).pdf"), chosen[1]);
    }

    /// <summary>The application matches answers to requests by position.</summary>
    [Fact]
    public void ThereIsOneAnswerPerRequestedNameInOrder()
    {
        var chosen = SaveFilesPlan.Resolve(Folder, ["a", "b", "c"], name => name == "b");

        Assert.Equal(3, chosen.Count);
        Assert.Equal("a", chosen[0].Name);
        Assert.Equal("b (copy)", chosen[1].Name);
        Assert.Equal("c", chosen[2].Name);
    }

    /// <summary>
    /// A name carrying a separator is an application writing outside the folder the user
    /// picked. Location.Child refuses one outright, so without this the call would fail
    /// instead of saving where it was told to.
    /// </summary>
    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("/etc/shadow", "shadow")]
    [InlineData("sub/dir/file.txt", "file.txt")]
    [InlineData("", "file")]
    [InlineData("..", "file")]
    [InlineData(".", "file")]
    public void ANameCanOnlyEverLandInTheChosenFolder(string requested, string expected)
    {
        var chosen = Assert.Single(SaveFilesPlan.Resolve(Folder, [requested], _ => false));

        Assert.Equal(expected, chosen.Name);
        Assert.Equal(Folder, chosen.Parent);
    }
}
