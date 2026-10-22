using Wlrix.Settings.Users.Services;
using Wlrix.Settings.Users.ViewModels;
using Xunit;

namespace Wlrix.Settings.Users.Tests;

public sealed class FingerprintUsesTests : IDisposable
{
    private const string Sudo = "#%PAM-1.0\nauth include system-auth\naccount include system-auth\n";

    private readonly string _root = Directory.CreateTempSubdirectory("wlrix-uses-").FullName;

    public FingerprintUsesTests()
    {
        Put("usr/lib/security/pam_fprintd.so", string.Empty);
        Put("etc/pam.d/sudo", Sudo);
        Put("usr/lib/pam.d/polkit-1", Sudo);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Put(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>A helper that does what the real one would, under the test root, as if authorized.</summary>
    private Func<PamTarget, bool, Task<HelperResult>> Authorized(PamFingerprint pam) => (target, on) =>
    {
        foreach (var (path, text) in pam.Plan(target, on))
        {
            if (text is null)
                File.Delete(path);
            else
                Put(Path.GetRelativePath(_root, path), text);
        }

        return Task.FromResult(new HelperResult(HelperOutcome.Succeeded, null));
    };

    [Fact]
    public async Task ASwitchShowsWhatTheFilesSayAfterTheHelperRan()
    {
        var pam = new PamFingerprint(_root);
        var uses = new FingerprintUses(pam, Authorized(pam));
        Assert.True(uses.Available);
        await uses.LoadAsync();
        Assert.False(uses.Sudo);
        Assert.True(uses.CanSudo);

        uses.Sudo = true;
        await WaitFor(() => uses.Settled);
        Assert.True(uses.Sudo);
        Assert.Equal(FingerprintUse.On, pam.State(PamTarget.Sudo));
        Assert.False(uses.Polkit);

        uses.Polkit = true;
        await WaitFor(() => uses.Settled);
        Assert.True(uses.Polkit);
        uses.Polkit = false;
        await WaitFor(() => uses.Settled);
        Assert.False(uses.Polkit);
        Assert.False(File.Exists(Path.Combine(_root, "etc/pam.d/polkit-1")));
    }

    [Fact]
    public async Task ADismissedPasswordDialogLeavesTheSwitchWhereItWas()
    {
        var pam = new PamFingerprint(_root);
        var uses = new FingerprintUses(pam, (_, _) => Task.FromResult(new HelperResult(HelperOutcome.NotAuthorized, null)));
        await uses.LoadAsync();
        uses.Sudo = true;
        await WaitFor(() => uses.Settled);
        Assert.False(uses.Sudo);
        Assert.Equal(string.Empty, uses.Message);
    }

    [Fact]
    public async Task AFingerprintInTheSharedStackIsNotOursToSwitch()
    {
        Put("etc/pam.d/system-auth", "auth sufficient pam_fprintd.so\n");
        var uses = new FingerprintUses(new PamFingerprint(_root), (_, _) => throw new InvalidOperationException("not to be called"));
        await uses.LoadAsync();
        Assert.True(uses.BySystem);
        Assert.True(uses.Sudo);
        Assert.False(uses.CanSudo);
        Assert.False(uses.CanPolkit);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }
}
