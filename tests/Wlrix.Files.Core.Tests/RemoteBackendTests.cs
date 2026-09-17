using SMBLibrary;
using Wlrix.Files.Core.Remote;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// The parts of the three protocol backends that can be checked without a server.
/// </summary>
/// <remarks>
/// Error classification, mostly, and it is the part most worth checking: everything above
/// these backends — the retry policy, the one silent reconnect, the conflict dialog, whether a
/// move can be a rename — branches on the <see cref="FileErrorKind"/> they produce. A status
/// mapped to the wrong kind does not fail here, it fails three layers up as a copy that
/// retries a permission denial forty times.
///
/// <para>
/// Whether the protocols themselves work against a real server is a written checklist, not a
/// test. See the manual verification notes for M7.
/// </para>
/// </remarks>
public class RemoteBackendTests
{
    private static readonly Location Somewhere = Location.Parse("smb://nas/share/file.txt");

    // --- SMB --------------------------------------------------------------

    [Theory]
    [InlineData(NTStatus.STATUS_OBJECT_NAME_NOT_FOUND, FileErrorKind.NotFound)]
    [InlineData(NTStatus.STATUS_OBJECT_PATH_NOT_FOUND, FileErrorKind.NotFound)]
    [InlineData(NTStatus.STATUS_NO_SUCH_FILE, FileErrorKind.NotFound)]
    [InlineData(NTStatus.STATUS_ACCESS_DENIED, FileErrorKind.AccessDenied)]
    [InlineData(NTStatus.STATUS_LOGON_FAILURE, FileErrorKind.AccessDenied)]
    [InlineData(NTStatus.STATUS_OBJECT_NAME_COLLISION, FileErrorKind.AlreadyExists)]
    [InlineData(NTStatus.STATUS_DIRECTORY_NOT_EMPTY, FileErrorKind.NotEmpty)]
    [InlineData(NTStatus.STATUS_DISK_FULL, FileErrorKind.NoSpace)]
    [InlineData(NTStatus.STATUS_IO_TIMEOUT, FileErrorKind.Timeout)]
    [InlineData(NTStatus.STATUS_NOT_SUPPORTED, FileErrorKind.Unsupported)]
    public void SmbStatusesMapToTheKindTheEngineBranchesOn(NTStatus status, FileErrorKind expected) =>
        Assert.Equal(expected, SmbSession.Classify(status));

    [Theory]
    [InlineData(NTStatus.STATUS_USER_SESSION_DELETED)]
    [InlineData(NTStatus.STATUS_NETWORK_NAME_DELETED)]
    [InlineData(NTStatus.STATUS_INVALID_HANDLE)]
    public void ATornDownSmbSessionIsAConnectionLossSoTheSilentReconnectHappens(NTStatus status)
    {
        // These are the statuses a server produces when it has forgotten about us. Mapped to
        // anything else, the one transparent reconnect never fires and the user sees an error
        // for a share that is perfectly reachable.
        Assert.Equal(FileErrorKind.ConnectionLost, SmbSession.Classify(status));
    }

    [Fact]
    public void ABadNetworkNameIsAMissingShareRatherThanANetworkFault()
    {
        // The name says otherwise, and treating it as a connection loss would retry a share
        // that does not exist and then report the wrong thing about it.
        Assert.Equal(FileErrorKind.NotFound, SmbSession.Classify(NTStatus.STATUS_BAD_NETWORK_NAME));
    }

    [Fact]
    public void AnUnmappedSmbStatusCarriesItsOwnNameSoTheNextOneIsEasyToAdd()
    {
        var error = SmbSession.Failed(NTStatus.STATUS_INVALID_PARAMETER, Somewhere, "open");
        Assert.Equal(FileErrorKind.Unknown, error.Kind);
        Assert.Contains("STATUS_INVALID_PARAMETER", error.Message, StringComparison.Ordinal);
        Assert.Contains("open", error.Message, StringComparison.Ordinal);
    }

    // --- FTP --------------------------------------------------------------

    [Theory]
    [InlineData("421", FileErrorKind.ConnectionLost)]
    [InlineData("425", FileErrorKind.ConnectionLost)]
    [InlineData("426", FileErrorKind.ConnectionLost)]
    [InlineData("450", FileErrorKind.Interrupted)]
    [InlineData("452", FileErrorKind.NoSpace)]
    [InlineData("552", FileErrorKind.NoSpace)]
    [InlineData("530", FileErrorKind.AccessDenied)]
    [InlineData("553", FileErrorKind.AccessDenied)]
    public void FtpReplyCodesMapToTheKindTheEngineBranchesOn(string code, FileErrorKind expected) =>
        Assert.Equal(expected, FtpSession.FromCode(code, string.Empty));

    [Fact]
    public void The550CatchAllIsReadFromTheTextBecauseTheCodeCannotSay()
    {
        // 550 is "requested action not taken" and covers a missing file, a permission denial
        // and a wrong file type, depending on the server's mood. Servers do at least agree on
        // the words, so the text is the only thing left to read.
        Assert.Equal(FileErrorKind.AccessDenied, FtpSession.FromCode("550", "Permission denied"));
        Assert.Equal(FileErrorKind.AccessDenied, FtpSession.FromCode("550", "Access is DENIED"));
        Assert.Equal(FileErrorKind.NotFound, FtpSession.FromCode("550", "No such file or directory"));
        // The common case, and the safer default: far more 550s are a path that is not there.
        Assert.Equal(FileErrorKind.NotFound, FtpSession.FromCode("550", "Failed to open file."));
    }

    [Fact]
    public void AnUnrecognizedFtpCodeIsUnknownRatherThanGuessed()
    {
        Assert.Equal(FileErrorKind.Unknown, FtpSession.FromCode("999", "whatever"));
        Assert.Equal(FileErrorKind.Unknown, FtpSession.FromCode(null, "no code at all"));
    }

    [Fact]
    public void ADeadSocketIsAConnectionLossWhicheverLibraryReportsIt()
    {
        // Every one of these means the same thing to the layer above, and only a
        // ConnectionLost gets the silent reconnect.
        foreach (var ex in new Exception[]
                 {
                     new System.Net.Sockets.SocketException(),
                     new IOException("broken pipe"),
                     new ObjectDisposedException("client")
                 })
        {
            Assert.Equal(FileErrorKind.ConnectionLost, FtpSession.Classify(ex, Somewhere).Kind);
            Assert.Equal(FileErrorKind.ConnectionLost, SftpSession.Classify(ex, Somewhere).Kind);
        }
    }

    [Fact]
    public void EveryClassifierKeepsTheOriginalExceptionForTheLog()
    {
        var original = new IOException("the cable came out");
        Assert.Same(original, FtpSession.Classify(original, Somewhere).InnerException);
        Assert.Same(original, SftpSession.Classify(original, Somewhere).InnerException);
    }

    // --- SFTP -------------------------------------------------------------

    [Fact]
    public void AnSshAuthenticationFailureIsAnAccessDenialAndIsNotRetried()
    {
        // Retrying a wrong password is how an account gets locked out.
        var error = SftpSession.Classify(
            new Renci.SshNet.Common.SshAuthenticationException("bad password"), Somewhere);
        Assert.Equal(FileErrorKind.AccessDenied, error.Kind);
    }

    [Fact]
    public void AnSshConnectionFailureIsAConnectionLoss()
    {
        var error = SftpSession.Classify(
            new Renci.SshNet.Common.SshConnectionException("gone"), Somewhere);
        Assert.Equal(FileErrorKind.ConnectionLost, error.Kind);
    }

    [Fact]
    public async Task SftpRefusesAnAnonymousLoginRatherThanTryingOne()
    {
        // There is no anonymous SFTP: the protocol runs inside an authenticated SSH session,
        // and a server accepting anonymous logins would be handing out a shell.
        var session = new SftpSession("example.invalid", -1, ShareCredentials.Anonymously);
        var error = await Assert.ThrowsAsync<FileOperationException>(
            () => session.ConnectAsync(CancellationToken.None));

        Assert.Equal(FileErrorKind.AccessDenied, error.Kind);
        Assert.Contains("anonymous", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SmbRefusesAPortItCannotReachRatherThanConnectingElsewhere()
    {
        // SMB2Client.DirectTCPPort is a readonly constant in the library, so a location naming
        // another port cannot be honored -- and connecting to 445 anyway would be going
        // somewhere the user did not ask for.
        var session = new SmbSession("nas", 4450, ShareCredentials.Anonymously);
        var error = await Assert.ThrowsAsync<FileOperationException>(
            () => session.ConnectAsync(CancellationToken.None));

        Assert.Equal(FileErrorKind.Unsupported, error.Kind);
        Assert.Contains("445", error.Message, StringComparison.Ordinal);
    }

    // --- shares and credentials -------------------------------------------

    [Fact]
    public void AnSmbShareRefTakesTheShareOffThePathAndTheRestDoesNot()
    {
        // SMB authenticates per share; FTP and SFTP authenticate per connection. Getting this
        // backwards would ask for a password once per directory on an FTP server.
        Assert.Equal("media", ShareRef.For(Location.Parse("smb://nas/media/photos/a.jpg")).Share);
        Assert.Equal(string.Empty, ShareRef.For(Location.Parse("sftp://host/srv/data")).Share);
        Assert.Equal(string.Empty, ShareRef.For(Location.Parse("ftp://host/pub/file")).Share);
    }

    [Fact]
    public void TwoUsersOnOneShareAreTwoDifferentCredentials()
    {
        var asVic = ShareRef.For(Location.Parse("smb://nas/media"), "vic");
        var asGuest = ShareRef.For(Location.Parse("smb://nas/media"), "guest");
        Assert.NotEqual(asVic.Key, asGuest.Key);
    }

    [Fact]
    public void ACredentialNeverPrintsItsPassword()
    {
        // The three places a password leaks are a log line, a saved file and a URI. This is
        // the first of them.
        var credentials = new ShareCredentials("vic", "hunter2");
        Assert.DoesNotContain("hunter2", credentials.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", $"{credentials}", StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTransientStoreRemembersWithinASessionAndSaysItWillNotOutliveOne()
    {
        var store = new TransientCredentialStore();
        var share = ShareRef.For(Location.Parse("smb://nas/media"), "vic");

        Assert.False(store.IsPersistent);
        Assert.Null(await store.GetAsync(share));

        await store.SaveAsync(share, new ShareCredentials("vic", "hunter2"));
        Assert.Equal("hunter2", (await store.GetAsync(share))!.Password);

        await store.RemoveAsync(share);
        Assert.Null(await store.GetAsync(share));
    }

    [Fact]
    public async Task ACredentialForOneShareIsNotHandedToAnother()
    {
        var store = new TransientCredentialStore();
        await store.SaveAsync(ShareRef.For(Location.Parse("smb://nas/media"), "vic"),
            new ShareCredentials("vic", "hunter2"));

        Assert.Null(await store.GetAsync(ShareRef.For(Location.Parse("smb://other/media"), "vic")));
        Assert.Null(await store.GetAsync(ShareRef.For(Location.Parse("smb://nas/backups"), "vic")));
    }

    // --- the two bugs a real server found ---------------------------------

    [Fact]
    public void ACompletedDirectoryListingIsNotAnError()
    {
        // SMBLibrary's QueryDirectory asks repeatedly until the server says there is nothing
        // more, and returns *that* status -- so a listing that worked ends in
        // STATUS_NO_MORE_FILES with every entry populated. Confirmed against a real server:
        // twelve entries alongside that status. Classifying it as a failure reported a
        // perfectly good share as broken.
        Assert.Equal(FileErrorKind.Unknown, SmbSession.Classify(NTStatus.STATUS_NO_MORE_FILES));

        // The classification above is only reached if something decides it is a failure at
        // all, and the listing path no longer does.
        Assert.True(SmbSession.IsListingComplete(NTStatus.STATUS_SUCCESS));
        Assert.True(SmbSession.IsListingComplete(NTStatus.STATUS_NO_MORE_FILES));
        // Some servers answer this instead when the wildcard matched nothing, which is what
        // an empty directory looks like to them.
        Assert.True(SmbSession.IsListingComplete(NTStatus.STATUS_NO_SUCH_FILE));

        // ...and a real failure is still a failure.
        Assert.False(SmbSession.IsListingComplete(NTStatus.STATUS_ACCESS_DENIED));
        Assert.False(SmbSession.IsListingComplete(NTStatus.STATUS_NETWORK_NAME_DELETED));
    }

    [Fact]
    public async Task ACredentialSavedWithAUsernameIsFoundByALookupWithoutOne()
    {
        // The connect dialog saves under the username it was given; what later asks for the
        // credential is a *navigation* to a URI, and a URI carries no username because
        // Location strips userinfo. Keyed strictly, the lookup missed what had just been
        // saved, the connection fell back to anonymous, and a server that wanted a password
        // answered STATUS_ACCESS_DENIED. That is the bug this pins.
        var store = new TransientCredentialStore();
        var typed = ShareRef.For(Location.Parse("smb://10.0.0.100/nas"), "vic");
        await store.SaveAsync(typed, new ShareCredentials("vic", "hunter2"));

        var navigated = ShareRef.For(Location.Parse("smb://10.0.0.100/nas"));
        Assert.Equal(string.Empty, navigated.Username);

        var found = await store.GetAsync(navigated);
        Assert.NotNull(found);
        Assert.Equal("vic", found.Username);
    }

    [Fact]
    public async Task NamingAUsernameStillGetsThatUsersCredentialAndNotTheLastOne()
    {
        // The fallback must not override an exact match, or connecting as a second user would
        // silently reuse the first one's password.
        var store = new TransientCredentialStore();
        var share = Location.Parse("smb://nas/media");
        await store.SaveAsync(ShareRef.For(share, "vic"), new ShareCredentials("vic", "one"));
        await store.SaveAsync(ShareRef.For(share, "guest"), new ShareCredentials("guest", "two"));

        Assert.Equal("one", (await store.GetAsync(ShareRef.For(share, "vic")))!.Password);
        Assert.Equal("two", (await store.GetAsync(ShareRef.For(share, "guest")))!.Password);
        // With no username named, the most recent one wins.
        Assert.Equal("two", (await store.GetAsync(ShareRef.For(share)))!.Password);
    }

    [Fact]
    public async Task TheFallbackDoesNotReachAcrossSharesOrHosts()
    {
        var store = new TransientCredentialStore();
        await store.SaveAsync(ShareRef.For(Location.Parse("smb://nas/media"), "vic"),
            new ShareCredentials("vic", "hunter2"));

        Assert.Null(await store.GetAsync(ShareRef.For(Location.Parse("smb://nas/backups"))));
        Assert.Null(await store.GetAsync(ShareRef.For(Location.Parse("smb://other/media"))));
    }

    [Fact]
    public async Task RemovingACredentialAlsoStopsItBeingFoundByTheFallback()
    {
        var store = new TransientCredentialStore();
        var typed = ShareRef.For(Location.Parse("smb://nas/media"), "vic");
        await store.SaveAsync(typed, new ShareCredentials("vic", "hunter2"));
        await store.RemoveAsync(typed);

        Assert.Null(await store.GetAsync(ShareRef.For(Location.Parse("smb://nas/media"))));
    }
}
