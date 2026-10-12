// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Wlrix.ImageView.Models;
using Xunit;

namespace Wlrix.ImageView.Tests;

public class TransformHistoryTests
{
    private static readonly ViewTransform Start = ViewTransform.Identity;

    [Fact]
    public void Undo_and_redo_walk_the_committed_steps()
    {
        var history = new TransformHistory(Start);
        var panned = Start.WithPan(new Vector(10, 5));
        var turned = panned.WithRotation(30);

        history.Commit(panned);
        history.Commit(turned);

        Assert.True(history.Undo());
        Assert.Equal(panned, history.Current);
        Assert.True(history.Undo());
        Assert.Equal(Start, history.Current);
        Assert.False(history.Undo());

        Assert.True(history.Redo());
        Assert.Equal(panned, history.Current);
        Assert.True(history.CanRedo);
    }

    [Fact]
    public void A_new_step_clears_redo()
    {
        var history = new TransformHistory(Start);
        history.Commit(Start.WithZoom(2));
        history.Undo();
        Assert.True(history.CanRedo);

        history.Commit(Start.WithRotation(90));

        Assert.False(history.CanRedo);
        Assert.Equal(90, history.Current.Rotation);
    }

    [Fact]
    public void A_step_that_changes_nothing_is_not_recorded()
    {
        var history = new TransformHistory(Start);
        Assert.False(history.Commit(Start));
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Reset_forgets_everything()
    {
        var history = new TransformHistory(Start);
        history.Commit(Start.WithZoom(2));
        history.Commit(Start.WithZoom(3));
        history.Undo();

        history.Reset(Start.WithZoom(0.5));

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Equal(0.5, history.Current.Zoom);
    }
}

public class ViewTransformTests
{
    [Theory]
    [InlineData(-90, 270)]
    [InlineData(450, 90)]
    [InlineData(360, 0)]
    [InlineData(359.99999999, 0)]
    public void Rotation_is_normalized(double degrees, double expected) =>
        Assert.Equal(expected, ViewTransform.Identity.WithRotation(degrees).Rotation, 6);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 1)]
    [InlineData(180, 2)]
    [InlineData(270, 3)]
    [InlineData(89.9999999999, 1)]
    public void Quarter_turns_are_recognized(double degrees, int turns) =>
        Assert.Equal(turns, ViewTransform.Identity.WithRotation(degrees).QuarterTurns);

    [Fact]
    public void Other_angles_are_not_quarter_turns()
    {
        Assert.Null(ViewTransform.Identity.WithRotation(45).QuarterTurns);
        Assert.True(ViewTransform.Identity.WithRotation(45).IsRotated);
        Assert.False(ViewTransform.Identity.WithRotation(360).IsRotated);
    }

    [Fact]
    public void Dragging_up_or_left_zooms_in()
    {
        Assert.True(ViewTransform.ZoomForDrag(1, 0, -50) > 1);
        Assert.True(ViewTransform.ZoomForDrag(1, -50, 0) > 1);
        Assert.True(ViewTransform.ZoomForDrag(1, 0, 50) < 1);
        Assert.True(ViewTransform.ZoomForDrag(1, 50, 0) < 1);
        Assert.Equal(2, ViewTransform.ZoomForDrag(1, 0, -120), 6);
        Assert.Equal(0.5, ViewTransform.ZoomForDrag(1, 120, 0), 6);
    }

    [Fact]
    public void Zoom_is_clamped()
    {
        Assert.Equal(ViewTransform.MaxZoom, ViewTransform.ZoomForDrag(1, 0, -100_000));
        Assert.Equal(ViewTransform.MinZoom, ViewTransform.ZoomForDrag(1, 0, 100_000));
    }
}
