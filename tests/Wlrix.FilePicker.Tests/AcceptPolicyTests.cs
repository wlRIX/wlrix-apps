using Wlrix.Files.Core;
using Wlrix.Files.Core.Portal;
using Wlrix.FilePicker.Services;
using Xunit;

namespace Wlrix.FilePicker.Tests;

/// <summary>
/// What the accept button does, which is the whole of what this application decides.
/// </summary>
/// <remarks>
/// Every branch here is reached by a gesture and nothing on this machine can synthesize one,
/// so this is where the behavior is settled. It matters more than the file manager's
/// equivalent: the wrong branch does not draw the wrong thing, it hands an application a file
/// the user did not choose.
/// </remarks>
public class AcceptPolicyTests
{
    private static readonly Location Folder = Location.FromLocalPath("/home/vic");

    private static FileEntry Entry(string name, bool directory = false) => new()
    {
        Location = Folder.Child(name),
        Name = name,
        Kind = directory ? FileKind.Directory : FileKind.File,
    };

    /// <summary>A filesystem where the named paths exist, as files unless listed as folders.</summary>
    private static Func<Location, FileKind?> Disk(string[] files, string[]? folders = null) =>
        location =>
        {
            if (folders is not null && folders.Contains(location.Path, StringComparer.Ordinal))
                return FileKind.Directory;
            return files.Contains(location.Path, StringComparer.Ordinal) ? FileKind.File : null;
        };

    private static readonly Func<Location, FileKind?> Empty = Disk([]);

    [Fact]
    public void OpeningAnsweredWithTheSelectedFiles()
    {
        var decision = AcceptPolicy.Decide(
            new FileChooserRequest { Multiple = true },
            Folder,
            [Entry("a.txt"), Entry("b.txt")],
            "",
            Empty);

        Assert.Equal(AcceptAction.Accept, decision.Action);
        Assert.Equal([Folder.Child("a.txt"), Folder.Child("b.txt")], decision.Chosen);
    }

    /// <summary>
    /// What makes the button follow the listing rather than needing a double-click for
    /// folders and a press for files.
    /// </summary>
    [Fact]
    public void OneFolderSelectedOpensItRatherThanAnsweringWithIt()
    {
        var decision = AcceptPolicy.Decide(
            new FileChooserRequest(), Folder, [Entry("Documents", directory: true)], "", Empty);

        Assert.Equal(AcceptAction.Navigate, decision.Action);
        Assert.Equal(Folder.Child("Documents"), decision.Target);
    }

    [Fact]
    public void NothingSelectedIsNothingToAnswerWith()
    {
        Assert.Equal(
            AcceptAction.None,
            AcceptPolicy.Decide(new FileChooserRequest(), Folder, [], "", Empty).Action);
    }

    /// <summary>
    /// An application that asked for one file and is handed three will use the first, and the
    /// user will believe all three went.
    /// </summary>
    [Fact]
    public void ASingleFileRequestNeverAnswersWithMore()
    {
        var decision = AcceptPolicy.Decide(
            new FileChooserRequest { Multiple = false },
            Folder,
            [Entry("a.txt"), Entry("b.txt")],
            "",
            Empty);

        Assert.Equal(Folder.Child("a.txt"), Assert.Single(decision.Chosen!));
    }

    [Fact]
    public void ChoosingAFolderAnswersWithTheOneSelected()
    {
        var decision = AcceptPolicy.Decide(
            new FileChooserRequest { Directory = true },
            Folder,
            [Entry("Pictures", directory: true)],
            "",
            Empty);

        Assert.Equal(AcceptAction.Accept, decision.Action);
        Assert.Equal(Folder.Child("Pictures"), Assert.Single(decision.Chosen!));
    }

    /// <summary>What somebody who navigated into a folder and pressed the button meant.</summary>
    [Fact]
    public void ChoosingAFolderWithNothingSelectedAnswersWithTheOneOnScreen()
    {
        var decision = AcceptPolicy.Decide(
            new FileChooserRequest { Directory = true }, Folder, [], "", Empty);

        Assert.Equal(Folder, Assert.Single(decision.Chosen!));
    }

    /// <summary>A file selected in directory mode is not an answer of any kind.</summary>
    [Fact]
    public void ChoosingAFolderIgnoresASelectedFile()
    {
        var decision = AcceptPolicy.Decide(
            new FileChooserRequest { Directory = true }, Folder, [Entry("a.txt")], "", Empty);

        Assert.Equal(Folder, Assert.Single(decision.Chosen!));
    }

    [Fact]
    public void SavingAnswersWithTheTypedNameInTheFolderOnScreen()
    {
        var decision = AcceptPolicy.Decide(
            new FileChooserRequest { Mode = FileChooserMode.Save }, Folder, [], "draft.odt", Empty);

        Assert.Equal(AcceptAction.Accept, decision.Action);
        Assert.Equal(Folder.Child("draft.odt"), Assert.Single(decision.Chosen!));
    }

    [Fact]
    public void SavingOverAFileAsksFirst()
    {
        var decision = AcceptPolicy.Decide(
            new FileChooserRequest { Mode = FileChooserMode.Save },
            Folder,
            [],
            "notes.txt",
            Disk(["/home/vic/notes.txt"]));

        Assert.Equal(AcceptAction.ConfirmOverwrite, decision.Action);
        // The question names what the confirmed answer will be, so a confirmed overwrite
        // needs no second decision and cannot reach a different file.
        Assert.Equal(Folder.Child("notes.txt"), Assert.Single(decision.Chosen!));
    }

    /// <summary>
    /// How a path is typed a step at a time, and the only reading that cannot destroy
    /// something.
    /// </summary>
    [Fact]
    public void TypingTheNameOfAFolderOpensIt()
    {
        var decision = AcceptPolicy.Decide(
            new FileChooserRequest { Mode = FileChooserMode.Save },
            Folder,
            [],
            "Documents",
            Disk([], folders: ["/home/vic/Documents"]));

        Assert.Equal(AcceptAction.Navigate, decision.Action);
        Assert.Equal(Folder.Child("Documents"), decision.Target);
    }

    [Fact]
    public void AnEmptyNameWithAFolderSelectedOpensThatFolder()
    {
        var decision = AcceptPolicy.Decide(
            new FileChooserRequest { Mode = FileChooserMode.Save },
            Folder,
            [Entry("Documents", directory: true)],
            "  ",
            Empty);

        Assert.Equal(AcceptAction.Navigate, decision.Action);
    }

    [Fact]
    public void AnEmptyNameWithNothingSelectedDoesNothing()
    {
        Assert.Equal(
            AcceptAction.None,
            AcceptPolicy.Decide(
                new FileChooserRequest { Mode = FileChooserMode.Save }, Folder, [], "", Empty).Action);
    }

    /// <summary>Somebody who typed an absolute path meant it, as every other chooser accepts.</summary>
    [Fact]
    public void ATypedAbsolutePathIsHonored()
    {
        var decision = AcceptPolicy.Decide(
            new FileChooserRequest { Mode = FileChooserMode.Save }, Folder, [], "/mnt/Dev/x.txt", Empty);

        Assert.Equal(Location.FromLocalPath("/mnt/Dev/x.txt"), Assert.Single(decision.Chosen!));
    }

    /// <summary>
    /// The folder on screen is what the user agreed to. Location.Child refuses a separator
    /// outright, so the alternative is not a saved file but a failed call.
    /// </summary>
    [Theory]
    [InlineData("../secret.txt", "secret.txt")]
    [InlineData("sub/dir/notes.txt", "notes.txt")]
    public void ARelativeNameKeepsOnlyItsLastComponent(string typed, string expected)
    {
        var decision = AcceptPolicy.Decide(
            new FileChooserRequest { Mode = FileChooserMode.Save }, Folder, [], typed, Empty);

        var chosen = Assert.Single(decision.Chosen!);
        Assert.Equal(expected, chosen.Name);
        Assert.Equal(Folder, chosen.Parent);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("sub/")]
    public void ANameThatResolvesToNothingIsNotAnAnswer(string typed)
    {
        Assert.Equal(
            AcceptAction.None,
            AcceptPolicy.Decide(
                new FileChooserRequest { Mode = FileChooserMode.Save }, Folder, [], typed, Empty).Action);
    }

    /// <summary>
    /// The folder is the answer and the names came with the question — one per request, in the
    /// order asked, because the application matches them up by position.
    /// </summary>
    [Fact]
    public void SavingSeveralFilesAnswersOnePerNameInTheChosenFolder()
    {
        var decision = AcceptPolicy.Decide(
            new FileChooserRequest
            {
                Mode = FileChooserMode.SaveFiles,
                Files = ["invoice.pdf", "photo.jpg"],
            },
            Folder,
            [],
            "",
            Disk(["/home/vic/invoice.pdf"]));

        Assert.Equal(AcceptAction.Accept, decision.Action);
        Assert.Equal(
            [Folder.Child("invoice (copy).pdf"), Folder.Child("photo.jpg")],
            decision.Chosen);
    }
}
