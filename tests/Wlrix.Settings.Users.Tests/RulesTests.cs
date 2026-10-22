using Wlrix.Settings.Users.Services;
using Wlrix.Settings.Users.ViewModels;
using Xunit;

namespace Wlrix.Settings.Users.Tests;

public class AdminRuleTests
{
    private static AccountInfo Account(ulong uid, bool admin, bool locked = false) =>
        new($"/org/freedesktop/Accounts/User{uid}", uid, $"user{uid}", "", "", admin, "", locked, $"/home/user{uid}");

    [Fact]
    public void TheLastAdministratorStaysOneWhenRootIsNotUsable()
    {
        var me = Account(1000, admin: true);
        var other = Account(1001, admin: false);
        Assert.False(AccountRules.MayStopBeingAdmin(me, [me, other], rootUsable: false));
        Assert.True(AccountRules.MayStopBeingAdmin(me, [me, other], rootUsable: true));
    }

    [Fact]
    public void AnotherAdministratorIsEnoughUnlessItIsLocked()
    {
        var me = Account(1000, admin: true);
        Assert.True(AccountRules.MayStopBeingAdmin(me, [me, Account(1001, admin: true)], rootUsable: false));
        Assert.False(AccountRules.MayStopBeingAdmin(me, [me, Account(1001, admin: true, locked: true)], rootUsable: false));
    }

    [Fact]
    public void AStandardAccountIsNeverHeldBack() =>
        Assert.True(AccountRules.MayStopBeingAdmin(Account(1001, admin: false), [Account(1001, admin: false)], rootUsable: false));
}

public class UserNameTests
{
    [Theory]
    [InlineData("alice", null)]
    [InlineData("_svc-1", null)]
    [InlineData("", UserNameProblem.Empty)]
    [InlineData("Alice", UserNameProblem.BadStart)]
    [InlineData("1alice", UserNameProblem.BadStart)]
    [InlineData("al ice", UserNameProblem.BadCharacter)]
    [InlineData("abcdefghijabcdefghijabcdefghijabc", UserNameProblem.TooLong)]
    public void TheRuleUseraddEnforces(string name, UserNameProblem? expected) =>
        Assert.Equal(expected, AccountRules.CheckUserName(name));

    [Theory]
    [InlineData("Victor Fugazzotto", "victor")]
    [InlineData("Zoë Ångström", "zoe")]
    [InlineData("  ", "")]
    [InlineData("42 Nobody", "")]
    public void ASuggestionIsTheFirstNameMadeSafe(string full, string expected) =>
        Assert.Equal(expected, AccountRules.SuggestUserName(full));

    [Theory]
    [InlineData("Victor Fugazzotto", "vic485", "VF")]
    [InlineData("Account Two", "acc2", "AT")]
    [InlineData("Madonna", "m", "M")]
    [InlineData("", "acc2", "A")]
    public void InitialsStandInForAPicture(string real, string user, string expected) =>
        Assert.Equal(expected, AccountRules.Initials(real, user));
}

public class CryptTests
{
    [Fact]
    public void APasswordHashesToSomethingThatChecksAndIsSaltedEachTime()
    {
        var first = Crypt.Hash("correct horse");
        var second = Crypt.Hash("correct horse");
        Assert.StartsWith("$", first);
        Assert.NotEqual(first, second);
        Assert.True(Crypt.Matches("correct horse", first));
        Assert.False(Crypt.Matches("wrong", first));
    }
}

public class PictureTests
{
    [Fact]
    public void ThePictureIsCutToItsCenterSquare()
    {
        var portrait = Pictures.CenterSquare(300, 500);
        Assert.Equal((0, 100, 300, 300), (portrait.X, portrait.Y, portrait.Width, portrait.Height));
        var wide = Pictures.CenterSquare(640, 480);
        Assert.Equal((80, 0, 480, 480), (wide.X, wide.Y, wide.Width, wide.Height));
    }

    [Fact]
    public void AnAccountKeepsItsColor() =>
        Assert.Equal(Pictures.InitialsColor("acc2"), Pictures.InitialsColor("acc2"));
}

public class DialogModelTests
{
    [Fact]
    public void APasswordIsDoneOnlyWhenBothAgree()
    {
        var model = new PasswordModel { Password = "abc" };
        Assert.False(model.CanFinish);
        Assert.Null(model.PasswordProblem);
        model.Confirm = "abd";
        Assert.NotNull(model.PasswordProblem);
        model.Confirm = "abc";
        Assert.True(model.CanFinish);
        model.Clear();
        Assert.Equal("", model.Password);
    }

    [Fact]
    public void TheUserNameFollowsTheFullNameUntilTypedOver()
    {
        var model = new AddUserModel { FullName = "Grace Hopper" };
        Assert.Equal("grace", model.UserName);
        model.UserName = "ghopper";
        model.FullName = "Grace Brewster Hopper";
        Assert.Equal("ghopper", model.UserName);
    }

    [Fact]
    public void ATakenNameBlocksCreateUntilItChanges()
    {
        var model = new AddUserModel { FullName = "Grace", Password = "x", Confirm = "x" };
        Assert.True(model.CanFinish);
        model.Taken = true;
        Assert.False(model.CanFinish);
        model.UserName = "grace2";
        Assert.True(model.CanFinish);
    }

    [Fact]
    public async Task EnrollingCountsStagesAndFinishesOnCompleted()
    {
        await using var model = new FingerprintModel();
        await model.OnStatusAsync("enroll-stage-passed", false);
        Assert.False(string.IsNullOrEmpty(model.Message));
        await model.OnStatusAsync("enroll-completed", true);
        Assert.False(model.Enrolling);
    }
}
