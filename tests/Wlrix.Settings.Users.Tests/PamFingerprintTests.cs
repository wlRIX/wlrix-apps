using Wlrix.Settings.Users.Services;
using Xunit;

namespace Wlrix.Settings.Users.Tests;

public sealed class PamFingerprintTests : IDisposable
{
    private const string ArchSudo = """
        #%PAM-1.0
        auth		include		system-auth
        account		include		system-auth
        session		include		system-auth

        """;

    private const string ArchPolkit = """
        #%PAM-1.0

        auth       include      system-auth
        account    include      system-auth
        password   include      system-auth
        session    include      system-auth

        """;

    private const string DebianSudo = """
        #%PAM-1.0

        # Set up user limits from /etc/security/limits.conf.
        session    required   pam_limits.so

        @include common-auth
        @include common-account
        @include common-session-noninteractive

        """;

    private readonly string _root = Directory.CreateTempSubdirectory("wlrix-pam-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Put(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void TheFingerprintGoesJustBeforeTheFirstAuthLine()
    {
        var text = PamFingerprint.Enable(ArchSudo);
        var lines = text.Split('\n');
        Assert.Equal("#%PAM-1.0", lines[0]);
        Assert.Equal(PamFingerprint.Marker, lines[1]);
        Assert.Equal(PamFingerprint.Line, lines[2]);
        Assert.StartsWith("auth\t\tinclude", lines[3], StringComparison.Ordinal);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OnDebianItGoesBeforeTheCommonAuthInclude()
    {
        var lines = PamFingerprint.Enable(DebianSudo).Split('\n').ToList();
        var line = lines.IndexOf(PamFingerprint.Line);
        Assert.Equal("@include common-auth", lines[line + 1]);
        // Not before the session line that came first in the file.
        Assert.True(line > lines.IndexOf("session    required   pam_limits.so"));
    }

    [Theory]
    [InlineData(ArchSudo)]
    [InlineData(ArchPolkit)]
    [InlineData(DebianSudo)]
    public void OffUndoesOnExactly(string original)
    {
        var on = PamFingerprint.Enable(original);
        Assert.True(PamFingerprint.LoadsFprintd(on));
        Assert.Equal(on, PamFingerprint.Enable(on));
        Assert.Equal(original, PamFingerprint.Disable(on));
        Assert.Equal(original, PamFingerprint.Disable(original));
    }

    [Fact]
    public void OffRemovesAHandAddedLineToo()
    {
        var hand = ArchSudo.Replace("#%PAM-1.0\n", "#%PAM-1.0\nauth sufficient pam_fprintd.so max-tries=2\n", StringComparison.Ordinal);
        Assert.Equal(ArchSudo, PamFingerprint.Disable(hand));
    }

    [Fact]
    public void CachyOSHardwareDetectionsLineCountsAsOnAndComesOffWhole()
    {
        // chwd's fprint profile (profiles/usb/fprint) puts this before the first auth line of
        // /etc/pam.d/sudo when it finds a reader, marked by the trailing comment.
        var chwd = ArchSudo.Replace("auth\t\tinclude", "auth sufficient pam_fprintd.so # chwd-fprintd\nauth\t\tinclude", StringComparison.Ordinal);
        Assert.True(PamFingerprint.LoadsFprintd(chwd));
        Assert.Equal(chwd, PamFingerprint.Enable(chwd));
        Assert.Equal(ArchSudo, PamFingerprint.Disable(chwd));
    }

    [Fact]
    public void ACommentedOutLineIsNotTheFingerprint()
    {
        var commented = ArchSudo.Replace("#%PAM-1.0\n", "#%PAM-1.0\n#auth sufficient pam_fprintd.so\n", StringComparison.Ordinal);
        Assert.False(PamFingerprint.LoadsFprintd(commented));
        Assert.Equal(commented, PamFingerprint.Disable(commented));
    }

    [Fact]
    public void AStackWithNoAuthSectionIsRefused() =>
        Assert.Throws<InvalidOperationException>(() => PamFingerprint.Enable("#%PAM-1.0\nsession include system-auth\n"));

    [Fact]
    public void StateFollowsTheFiles()
    {
        var pam = new PamFingerprint(_root);
        Assert.Equal(FingerprintUse.Missing, pam.State(PamTarget.Sudo));
        Put("etc/pam.d/sudo", ArchSudo);
        Assert.Equal(FingerprintUse.Off, pam.State(PamTarget.Sudo));
        Put("etc/pam.d/sudo", PamFingerprint.Enable(ArchSudo));
        Assert.Equal(FingerprintUse.On, pam.State(PamTarget.Sudo));
        Put("etc/pam.d/system-auth", "auth sufficient pam_fprintd.so\nauth required pam_unix.so\n");
        Assert.Equal(FingerprintUse.BySystem, pam.State(PamTarget.Sudo));
    }

    [Fact]
    public void AVendorStackIsCopiedToEtcAndTheCopyGoesWhenTurnedOff()
    {
        var pam = new PamFingerprint(_root);
        Put("usr/lib/pam.d/polkit-1", ArchPolkit);
        Assert.Equal(FingerprintUse.Off, pam.State(PamTarget.Polkit));

        var on = Assert.Single(pam.Plan(PamTarget.Polkit, enable: true));
        Assert.Equal(Path.Combine(_root, "etc/pam.d/polkit-1"), on.Path);
        Put("etc/pam.d/polkit-1", on.Text!);
        Assert.Equal(FingerprintUse.On, pam.State(PamTarget.Polkit));

        var off = Assert.Single(pam.Plan(PamTarget.Polkit, enable: false));
        Assert.Null(off.Text);
    }

    [Fact]
    public void ASudoIThatIncludesSudoIsLeftAlone()
    {
        var pam = new PamFingerprint(_root);
        Put("etc/pam.d/sudo", ArchSudo);
        Put("etc/pam.d/sudo-i", "#%PAM-1.0\nauth include sudo\naccount include sudo\n");
        Assert.Single(pam.Plan(PamTarget.Sudo, enable: true));

        Put("etc/pam.d/sudo-i", ArchSudo);
        Assert.Equal(2, pam.Plan(PamTarget.Sudo, enable: true).Count);
    }

    [Fact]
    public void NothingToDoIsAnEmptyPlan()
    {
        var pam = new PamFingerprint(_root);
        Put("etc/pam.d/sudo", ArchSudo);
        Assert.Empty(pam.Plan(PamTarget.Sudo, enable: false));
    }

    [Fact]
    public void TheModuleIsFoundOnArchAndDebianLayouts()
    {
        var pam = new PamFingerprint(_root);
        Assert.False(pam.ModuleInstalled());
        Put("usr/lib/x86_64-linux-gnu/security/pam_fprintd.so", string.Empty);
        Assert.True(pam.ModuleInstalled());
    }
}
