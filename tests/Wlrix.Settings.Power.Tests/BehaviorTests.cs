// SPDX-License-Identifier: GPL-3.0-or-later
using Wlrix.Settings.Power.Services;
using Wlrix.Settings.Power.ViewModels;
using Xunit;

namespace Wlrix.Settings.Power.Tests;

public class LidChoiceTests
{
    [Fact]
    public void EveryActionWlrixIdleKnowsIsOffered()
    {
        // wlrix-idle's LidAction, as serde spells it; a value missing here could never be chosen,
        // and one wlrix-idle does not know would be refused by --check-config.
        Assert.Equal(
            ["suspend", "hibernate", "lock", "blank", "nothing"],
            LidChoice.All.Select(choice => choice.Value));
    }

    [Fact]
    public void AnUnknownValueShowsTheDefault()
    {
        Assert.Equal("hibernate", LidChoice.Find("hibernate", "suspend").Value);
        Assert.Equal("suspend", LidChoice.Find("shutdown", "suspend").Value);
        Assert.Equal("nothing", LidChoice.Find(null, "nothing").Value);
    }
}

public class LidTests
{
    [Fact]
    public void ALidIsADirectoryUnderAcpi()
    {
        var directory = Directory.CreateTempSubdirectory("wlrix-power-lid-");
        try
        {
            Assert.False(Lid.Present(directory.FullName), "an empty directory is a desktop");
            Directory.CreateDirectory(Path.Combine(directory.FullName, "LID0"));
            Assert.True(Lid.Present(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
        Assert.False(Lid.Present(directory.FullName), "no directory, no lid");
    }
}
