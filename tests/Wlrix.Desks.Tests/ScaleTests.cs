using Wlrix.Desks.Models;
using Wlrix.Desks.Services;
using Wlrix.Desks.ViewModels;
using Xunit;

namespace Wlrix.Desks.Tests;

/// <summary>
/// That the Scaling submenu's presets and its user-defined amount stay two separate things.
/// </summary>
/// <remarks>
/// The submenu lists both at once, so choosing one must not lose the other, and the radio
/// group must check exactly one item even when the user-defined amount equals a preset.
/// </remarks>
public sealed class ScaleTests : IDisposable
{
    private readonly MainWindowViewModel _model = new(new SampleDeskFeed(), new DesksSettings());

    public void Dispose() => _model.Dispose();

    [Fact]
    public void TheDefaultIsEightPercentWithNothingUserDefined()
    {
        Assert.Equal(DeskScale.Default, _model.Scale);
        Assert.Equal(DeskScale.Default, _model.CheckedPresetScale);
        Assert.False(_model.HasUserDefinedScale);
    }

    [Fact]
    public void AUserDefinedAmountEqualToAPresetChecksOnlyTheUserDefinedItem()
    {
        _model.SetUserDefinedScale(8);

        Assert.Equal(8, _model.Scale);
        Assert.Null(_model.CheckedPresetScale);
        Assert.True(_model.UseUserDefinedScale);
    }

    [Fact]
    public void ChoosingAPresetKeepsTheUserDefinedAmountForLater()
    {
        _model.SetUserDefinedScale(18);
        _model.ChoosePresetScale(6);

        Assert.Equal(6, _model.Scale);
        Assert.Equal(18, _model.UserDefinedScale);

        _model.ChooseUserDefinedScale();
        Assert.Equal(18, _model.Scale);
    }

    [Fact]
    public void TheCustomAmountIsHeldToTheSlidersRange()
    {
        _model.SetUserDefinedScale(40);

        Assert.Equal(DeskScale.Maximum, _model.Scale);
    }

    [Fact]
    public void AChangeReportsHowMuchTallerASnapshotBecame()
    {
        // The default world is 1920x1080: 8% of its height rounds to 86, 10% is 108.
        double? delta = null;
        _model.ScaleChanged += change => delta = change;

        _model.ChoosePresetScale(10);

        Assert.Equal(22, delta);
    }
}
