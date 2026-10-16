using Wlrix.PolkitAgent.Services;
using Xunit;

namespace Wlrix.PolkitAgent.Tests;

public class IdentitiesTests
{
    private sealed class Accounts : IAccountLookup
    {
        public string CurrentUser { get; init; } = "vic";

        public string? UserName(uint uid) => uid switch { 0 => "root", 1000 => "vic", 1001 => "ann", _ => null };

        public IReadOnlyList<string> GroupMembers(uint gid) => gid == 998 ? ["ann", "vic"] : [];
    }

    [Fact]
    public void GroupsAreExpandedAndDuplicatesDropped()
    {
        var (users, _) = Identities.Resolve(
            [new PolkitIdentity("unix-user", 0), new PolkitIdentity("unix-group", 998), new PolkitIdentity("unix-user", 1000)],
            new Accounts());
        Assert.Equal(["root", "ann", "vic"], users);
    }

    [Fact]
    public void ThePersonAtTheKeyboardIsPreselected()
    {
        var (users, selected) = Identities.Resolve(
            [new PolkitIdentity("unix-user", 0), new PolkitIdentity("unix-user", 1000)], new Accounts());
        Assert.Equal("vic", users[selected]);
    }

    [Fact]
    public void OtherwiseTheFirstIs()
    {
        var (_, selected) = Identities.Resolve(
            [new PolkitIdentity("unix-user", 0), new PolkitIdentity("unix-user", 1001)], new Accounts());
        Assert.Equal(0, selected);
    }

    [Fact]
    public void UnknownAccountsAndKindsAreSkipped()
    {
        var (users, _) = Identities.Resolve(
            [new PolkitIdentity("unix-user", 4242), new PolkitIdentity("unix-netgroup", 1), new PolkitIdentity("unix-user", 0)],
            new Accounts());
        Assert.Equal(["root"], users);
    }
}
