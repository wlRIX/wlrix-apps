using Wlrix.Files.Core.Dnd;
using Wlrix.Files.Core.Operations;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// What a drop means. Pure, and therefore the only part of drag and drop this machine can
/// check at all — nothing here can synthesize the gesture, so the arithmetic and the rules
/// are tested here and the plumbing is verified by hand.
/// </summary>
public class DropPolicyTests
{
    private static readonly Location Home = Location.Parse("/home/vic");
    private static readonly Location Downloads = Location.Parse("/home/vic/Downloads");
    private static readonly Location Notes = Location.Parse("/home/vic/notes.txt");

    [Theory]
    [InlineData(DropModifiers.None, true, DropAction.Move)]
    [InlineData(DropModifiers.None, false, DropAction.Copy)]
    [InlineData(DropModifiers.Control, true, DropAction.Copy)]
    [InlineData(DropModifiers.Control, false, DropAction.Copy)]
    [InlineData(DropModifiers.Shift, true, DropAction.Move)]
    [InlineData(DropModifiers.Shift, false, DropAction.Move)]
    [InlineData(DropModifiers.Control | DropModifiers.Shift, true, DropAction.Link)]
    [InlineData(DropModifiers.Control | DropModifiers.Shift, false, DropAction.Link)]
    public void TheModifiersChooseTheAction(DropModifiers modifiers, bool sameFilesystem, DropAction expected) =>
        Assert.Equal(expected, DropPolicy.Intent(modifiers, sameFilesystem));

    [Fact]
    public void OnlyAnUnmodifiedDropCaresWhichFilesystemTheTargetIsOn()
    {
        // The default is the one that changes: an explicit Shift means move even onto another
        // disk, where it costs a full copy. That is what the user asked for.
        Assert.Equal(DropAction.Move, DropPolicy.Intent(DropModifiers.Shift, sameFilesystem: false));
        Assert.Equal(DropAction.Copy, DropPolicy.Intent(DropModifiers.Control, sameFilesystem: true));
    }

    [Fact]
    public void AltAloneIsNotAModifierWeAnswerTo()
    {
        // Some file managers pop a menu on Alt. wlRIX does not, and the important part is
        // that it falls back to the plain behavior rather than to nothing at all.
        Assert.Equal(DropAction.Move, DropPolicy.Intent(DropModifiers.Alt, sameFilesystem: true));
    }

    [Fact]
    public void ADirectoryCannotBeDroppedIntoItself()
    {
        var plan = DropPolicy.Decide([Downloads], Downloads, DropModifiers.None, sameFilesystem: true);
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void ADirectoryCannotBeDroppedIntoItsOwnDescendant()
    {
        // The one that would actually destroy something: moving a tree inside a piece of
        // itself. Every case of it has to be refused before the engine sees it.
        var inside = Location.Parse("/home/vic/Downloads/archive/old");
        var plan = DropPolicy.Decide([Downloads], inside, DropModifiers.None, sameFilesystem: true);
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void MovingSomethingIntoTheDirectoryItIsAlreadyInDoesNothing()
    {
        var plan = DropPolicy.Decide([Notes], Home, DropModifiers.None, sameFilesystem: true);
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void ButCopyingSomethingIntoItsOwnDirectoryIsADuplicate()
    {
        // Deliberately not filtered: this is how a duplicate is made with the mouse, and the
        // conflict resolver gives the second one its name.
        var plan = DropPolicy.Decide([Notes], Home, DropModifiers.Control, sameFilesystem: true);
        Assert.Equal(DropAction.Copy, plan.Action);
        Assert.Equal([Notes], plan.Sources);
    }

    [Fact]
    public void TheRefusedSourcesAreDroppedAndTheRestStillGo()
    {
        // A selection made by rubber band can easily contain the target folder itself. The
        // whole drop failing because of one member would be the wrong answer.
        var other = Location.Parse("/home/vic/report.pdf");
        var plan = DropPolicy.Decide([Downloads, other], Downloads, DropModifiers.None, sameFilesystem: true);

        Assert.Equal(DropAction.Move, plan.Action);
        Assert.Equal([other], plan.Sources);
    }

    [Fact]
    public void AnEmptyDragIsRefusedRatherThanQueuedAsANoOp()
    {
        Assert.True(DropPolicy.Decide([], Home, DropModifiers.None, sameFilesystem: true).IsEmpty);
        Assert.True(DropPlan.Nothing.IsEmpty);
    }

    [Fact]
    public void EachActionBecomesItsOwnOperation()
    {
        var sources = new[] { Notes };
        Assert.Equal(OperationKind.Copy,
            DropPolicy.ToOperation(new DropPlan(DropAction.Copy, sources), Downloads)!.Kind);
        Assert.Equal(OperationKind.Move,
            DropPolicy.ToOperation(new DropPlan(DropAction.Move, sources), Downloads)!.Kind);
        Assert.Equal(OperationKind.Link,
            DropPolicy.ToOperation(new DropPlan(DropAction.Link, sources), Downloads)!.Kind);
        Assert.Null(DropPolicy.ToOperation(DropPlan.Nothing, Downloads));
    }

    [Fact]
    public void TheOperationCarriesTheFilteredSourcesRatherThanTheOriginalSelection()
    {
        // The filtering is only worth anything if what reaches the engine is the filtered
        // list. Building the operation from the raw selection would undo all of it.
        var plan = DropPolicy.Decide([Downloads, Notes], Downloads, DropModifiers.None, sameFilesystem: true);
        var operation = DropPolicy.ToOperation(plan, Downloads)!;

        Assert.Equal([Notes], operation.Sources);
        Assert.Equal(Downloads, operation.Target);
    }

    [Fact]
    public void ADropFromAnotherMountIsNotConfusedWithOneFromThisOne()
    {
        // Contains() is mount-aware, so a remote path that happens to spell the same string
        // as a local directory must not be mistaken for being inside it.
        var remote = Location.Parse("smb://host/share/Downloads");
        var plan = DropPolicy.Decide([remote], Downloads, DropModifiers.None, sameFilesystem: false);

        Assert.Equal(DropAction.Copy, plan.Action);
        Assert.Equal([remote], plan.Sources);
    }
}
