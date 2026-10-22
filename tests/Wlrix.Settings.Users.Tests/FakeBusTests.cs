using Wlrix.Settings.Users.Services;
using Xunit;

namespace Wlrix.Settings.Users.Tests;

/// <summary>
/// The clients against a stand-in AccountsService and fprintd on a private system bus, never the
/// real ones: each test returns at once unless <c>WLRIX_USERS_FAKE</c> is set, which only the test
/// script that starts the stand-in and points <c>DBUS_SYSTEM_BUS_ADDRESS</c> at it does. Run in
/// order, against the stand-in's three accounts: root, 1000 (administrator) and 1001.
/// </summary>
[Collection("fake bus")]
public class FakeBusTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("WLRIX_USERS_FAKE") == "1";

    [Fact]
    public async Task AccountsAreListedAndChanged()
    {
        if (!Enabled)
            return;
        using var client = await AccountsClient.ConnectAsync();
        var accounts = await client.ListAsync();
        Assert.Contains(accounts, account => account.UserName == "acc2" && !account.IsAdmin);
        var acc2 = accounts.Single(account => account.UserName == "acc2");

        await client.SetRealNameAsync(acc2.Path, "Account Deux");
        await client.SetEmailAsync(acc2.Path, "deux@example.org");
        await client.SetAdminAsync(acc2.Path, true);
        var changed = (await client.ListAsync()).Single(account => account.Uid == acc2.Uid);
        Assert.Equal(("Account Deux", "deux@example.org", true), (changed.RealName, changed.Email, changed.IsAdmin));
        await client.SetAdminAsync(acc2.Path, false);

        Assert.True(await client.UserNameTakenAsync("acc2"));
        Assert.False(await client.UserNameTakenAsync("nobody-here"));
    }

    [Fact]
    public async Task AnAccountIsCreatedWithAPasswordAndDeleted()
    {
        if (!Enabled)
            return;
        using var client = await AccountsClient.ConnectAsync();
        var path = await client.CreateAsync("grace", "Grace Hopper", admin: false, "cobol");
        var grace = (await client.ListAsync()).Single(account => account.Path == path);
        Assert.Equal("Grace Hopper", grace.RealName);
        await client.DeleteAsync(grace.Uid, removeFiles: true);
        Assert.DoesNotContain(await client.ListAsync(), account => account.Path == path);
    }

    [Fact]
    public async Task RootIsUsableOnlyAsTheStandInSays()
    {
        if (!Enabled)
            return;
        using var client = await AccountsClient.ConnectAsync();
        var expected = Environment.GetEnvironmentVariable("WLRIX_USERS_ROOT") == "usable";
        Assert.Equal(expected, await client.RootUsableAsync());
    }

    [Fact]
    public async Task AFingerIsEnrolledAndDeleted()
    {
        if (!Enabled)
            return;
        Assert.True(await Fingerprints.AvailableAsync());
        await using var reader = await Fingerprints.OpenAsync();
        Assert.Equal(5, await reader.StagesAsync());
        Assert.Contains("right-index-finger", await reader.EnrolledAsync());

        var results = new List<string>();
        var finished = new TaskCompletionSource();
        using (await reader.EnrollAsync("left-thumb", (result, done) =>
               {
                   lock (results)
                       results.Add(result);
                   if (done)
                       finished.TrySetResult();
               }))
        {
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        await reader.StopEnrollAsync();
        Assert.Equal("enroll-completed", results[^1]);
        Assert.Contains("enroll-retry-scan", results);
        Assert.Contains("left-thumb", await reader.EnrolledAsync());

        await reader.DeleteAllAsync();
        Assert.Empty(await reader.EnrolledAsync());
    }
}
