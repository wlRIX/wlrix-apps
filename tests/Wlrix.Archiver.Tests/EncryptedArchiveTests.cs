using Microsoft.Extensions.Logging.Abstractions;
using Wlrix.Archiver.Models;
using Wlrix.Archiver.Services;
using Wlrix.Archiver.Services.Archives;
using Wlrix.Archiver.Services.Encodings;
using Wlrix.Archiver.ViewModels;
using Xunit;

namespace Wlrix.Archiver.Tests;

/// <summary>
/// Telling "this needs a password" apart from everything else that can go wrong.
/// </summary>
/// <remarks>
/// The distinction is the whole feature. Before it, extracting an encrypted rar reported "The
/// password did not match" — SharpCompress's own words, for a password nobody had been asked
/// for. Both halves have to be recognizable: nothing tried yet, and something tried and refused,
/// because they are different questions to put to the user.
/// </remarks>
public class EncryptedArchiveTests
{
    private const string Password = "hunter2";

    private static SharpCompressBackend Managed() => new(new FilenameDecoder());

    /// <summary>
    /// Both zip encryption methods, because SharpCompress fails them differently.
    /// </summary>
    /// <remarks>
    /// AES raises InvalidFormatException — its password-verify value is checked before anything
    /// is decrypted, so a wrong password reads as a malformed entry — where ZipCrypto raises
    /// CryptographicException. A fixture of only one kind would leave half the classifier
    /// untested, and it was the AES half that caught the first version out.
    /// </remarks>
    public static TheoryData<string> BothMethods() => new("encrypted.zip", "encrypted-zipcrypto.zip");

    private static string Encrypted(string name = "encrypted.zip") => Fixture.Path(name);

    [Fact]
    public async Task AnEncryptedArchiveStillListsItsEntries()
    {
        // Zip encrypts contents but not the central directory, so the names are readable
        // without a password — which is why opening must not ask for one.
        var archive = await Managed().OpenAsync(Encrypted(), ArchiveFormat.Zip);

        Assert.Equal(["secret.txt"], archive.Entries.Select(entry => entry.Path));
        Assert.True(archive.Entries[0].IsEncrypted);
    }

    [Theory]
    [MemberData(nameof(BothMethods))]
    public async Task ExtractingWithNoPasswordAsksForOne(string fixture)
    {
        using var destination = new TemporaryDirectory();

        var ex = await Assert.ThrowsAsync<ArchivePasswordException>(() =>
            Managed().ExtractAsync(Encrypted(fixture), ArchiveFormat.Zip, [], destination.Path));

        Assert.False(ex.PasswordSupplied);
    }

    [Theory]
    [MemberData(nameof(BothMethods))]
    public async Task ExtractingWithTheWrongPasswordSaysItWasWrong(string fixture)
    {
        using var destination = new TemporaryDirectory();

        var ex = await Assert.ThrowsAsync<ArchivePasswordException>(() =>
            Managed().ExtractAsync(Encrypted(fixture), ArchiveFormat.Zip, [], destination.Path,
                password: "not it"));

        Assert.True(ex.PasswordSupplied);
    }

    [Theory]
    [MemberData(nameof(BothMethods))]
    public async Task ExtractingWithTheRightPasswordWorks(string fixture)
    {
        using var destination = new TemporaryDirectory();

        await Managed().ExtractAsync(Encrypted(fixture), ArchiveFormat.Zip, [], destination.Path,
            password: Password);

        Assert.Equal("classified",
            (await File.ReadAllTextAsync(Path.Combine(destination.Path, "secret.txt"))).Trim());
    }

    /// <summary>
    /// A password question still reports like any other failure to a caller that cannot ask.
    /// </summary>
    [Fact]
    public async Task ThePasswordQuestionIsStillAnArchiveException()
    {
        using var destination = new TemporaryDirectory();

        await Assert.ThrowsAnyAsync<ArchiveException>(() =>
            Managed().ExtractAsync(Encrypted(), ArchiveFormat.Zip, [], destination.Path));
    }

    /// <summary>
    /// 7z says the same thing whichever half it is, so the phrase is the signal and the
    /// arguments decide which question was being answered.
    /// </summary>
    /// <remarks>
    /// Against captured output rather than the real tool, like <see cref="SevenZipListingTests"/>:
    /// these have to pass on a machine with no 7z, which is the machine the fallback exists for.
    /// </remarks>
    [Theory]
    [InlineData("", false)]
    [InlineData(Password, true)]
    public async Task SevenZipRecognizesItsOwnWrongPasswordLine(string password, bool expectedSupplied)
    {
        const string Output = """
            7-Zip 26.02 (x64) : Copyright (c) 1999-2026 Igor Pavlov : 2026-06-25

            ERROR: Data Error in encrypted file. Wrong password? : secret.txt

            Sub items Errors: 1
            """;
        var backend = new SevenZipCliBackend(new StubRunner(new ProcessResult(2, Output, "")));
        using var destination = new TemporaryDirectory();

        var ex = await Assert.ThrowsAsync<ArchivePasswordException>(() =>
            backend.ExtractAsync("/archives/locked.7z", ArchiveFormat.SevenZip, [],
                destination.Path, password: password));

        Assert.Equal(expectedSupplied, ex.PasswordSupplied);
    }

    [Fact]
    public async Task SevenZipLeavesEveryOtherFailureAlone()
    {
        var backend = new SevenZipCliBackend(
            new StubRunner(new ProcessResult(2, "ERROR: locked.7z : Unexpected end of archive", "")));
        using var destination = new TemporaryDirectory();

        var ex = await Assert.ThrowsAsync<ArchiveException>(() =>
            backend.ExtractAsync("/archives/locked.7z", ArchiveFormat.SevenZip, [],
                destination.Path));

        Assert.IsNotType<ArchivePasswordException>(ex);
    }

    /// <summary>
    /// A zip holding anything encrypted is not offered as writable.
    /// </summary>
    /// <remarks>
    /// The library reads encryption and cannot write it, so the alternative to withholding the
    /// capability is a menu item that decrypts the archive and reports success.
    /// </remarks>
    [Theory]
    [MemberData(nameof(BothMethods))]
    public async Task AnEncryptedArchiveIsNotWritable(string fixture)
    {
        var archive = await Managed().OpenAsync(Encrypted(fixture), ArchiveFormat.Zip);

        Assert.True(archive.Capabilities.HasFlag(ArchiveCapabilities.Read));
        Assert.True(archive.Capabilities.HasFlag(ArchiveCapabilities.Extract));
        Assert.False(archive.Capabilities.HasFlag(ArchiveCapabilities.Add));
        Assert.False(archive.Capabilities.HasFlag(ArchiveCapabilities.Remove));
    }

    /// <summary>
    /// And the backend refuses even when asked directly, rather than trusting the capability.
    /// </summary>
    /// <remarks>
    /// The failure this guards against is silent and irreversible: the archive would be rewritten
    /// in the clear and would look no different. A caller that reached past the capability check
    /// has to hit something.
    /// </remarks>
    [Fact]
    public async Task WritingToAnEncryptedArchiveIsRefusedByTheBackendToo()
    {
        using var scratch = new TemporaryDirectory();
        var copy = Path.Combine(scratch.Path, "encrypted.zip");
        File.Copy(Encrypted(), copy);
        var before = await File.ReadAllBytesAsync(copy);

        var ex = await Assert.ThrowsAsync<ArchiveException>(() =>
            Managed().AddAsync(copy, ArchiveFormat.Zip, [Fixture.Path("encrypted.zip")]));

        Assert.Contains("encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
        // The archive is untouched, which is the part that matters.
        Assert.Equal(before, await File.ReadAllBytesAsync(copy));
    }

    /// <summary>
    /// 7z is given its password on a write, because without one it writes in the clear.
    /// </summary>
    /// <remarks>
    /// Measured, not assumed: `7z a` against an encrypted archive with no `-p` succeeds and adds
    /// an *unencrypted* member beside the encrypted ones, saying nothing. The empty password is
    /// equally not an option -- `7z a -p""` prompts on a terminal this process does not have and
    /// waits for ever -- so the switch appears exactly when there is something to put after it.
    /// </remarks>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(Password, true)]
    public async Task SevenZipPassesAPasswordOnAWriteOnlyWhenItHasOne(string? password, bool expected)
    {
        var runner = new RecordingRunner();
        var backend = new SevenZipCliBackend(runner);

        await backend.AddAsync("/archives/locked.7z", ArchiveFormat.SevenZip, ["/tmp/new.txt"],
            password: password);

        var passed = runner.Arguments.Any(a => a.StartsWith("-p", StringComparison.Ordinal));
        Assert.Equal(expected, passed);
        if (expected)
            Assert.Contains("-p" + Password, runner.Arguments);
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<ProcessResult> RunAsync(string program, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Arguments = arguments;
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }

    /// <summary>
    /// The window asks, retries with what it was told, and says which question it is asking.
    /// </summary>
    /// <remarks>
    /// Through the view model rather than a backend, because the retry loop is the part that
    /// lives there: a backend only ever says "this needs a password", and turning that into a
    /// question, an answer and another attempt is the window's job. Driven with a stub backend
    /// so the test is about the loop and not about any archive format.
    /// </remarks>
    [Fact]
    public async Task TheWindowAsksForAPasswordAndTriesAgainWithIt()
    {
        var backend = new LockedBackend(Password);
        var registry = new ArchiveBackendRegistry([backend]);
        var model = new MainWindowViewModel(registry, new FilenameDecoder(),
            new DragStaging(registry), NullLogger<MainWindowViewModel>.Instance);

        var asked = new List<bool>();
        model.PasswordRequested += (_, retry) =>
        {
            asked.Add(retry);
            // Wrong the first time, right the second, so both wordings are exercised and the
            // loop has to survive being told something useless.
            return Task.FromResult<string?>(asked.Count == 1 ? "not it" : Password);
        };

        await model.OpenAsync("/archives/locked.zip");

        Assert.Equal([false, true], asked);
        Assert.NotNull(model.Archive);
        Assert.Equal(3, backend.Attempts);
    }

    /// <summary>Declining the prompt cancels rather than failing.</summary>
    [Fact]
    public async Task DecliningToTypeAPasswordIsNotAnError()
    {
        var registry = new ArchiveBackendRegistry([new LockedBackend(Password)]);
        var model = new MainWindowViewModel(registry, new FilenameDecoder(),
            new DragStaging(registry), NullLogger<MainWindowViewModel>.Instance);

        var errors = new List<string>();
        model.ErrorRaised += errors.Add;
        model.PasswordRequested += (_, _) => Task.FromResult<string?>(null);

        await model.OpenAsync("/archives/locked.zip");

        Assert.Empty(errors);
        Assert.Null(model.Archive);
    }

    /// <summary>A backend that wants one particular password and counts what it was offered.</summary>
    private sealed class LockedBackend(string password) : IArchiveBackend
    {
        public int Attempts { get; private set; }

        public string Name => "locked";

        public ArchiveCapabilities Supports(ArchiveFormat format) => ArchiveCapabilities.All;

        public Task<OpenArchive> OpenAsync(string path, ArchiveFormat format, string? supplied = null,
            IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
        {
            Attempts++;
            return string.Equals(supplied, password, StringComparison.Ordinal)
                ? Task.FromResult(new OpenArchive(path, format, ArchiveCapabilities.All, []))
                : throw new ArchivePasswordException("locked",
                    passwordSupplied: !string.IsNullOrEmpty(supplied));
        }

        public Task ExtractAsync(string path, ArchiveFormat format, IReadOnlyList<string> entries,
            string destination, bool flatten = false, string? supplied = null,
            IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task AddAsync(string path, ArchiveFormat format, IReadOnlyList<string> sources,
            string prefix = "", string? supplied = null, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RemoveAsync(string path, ArchiveFormat format, IReadOnlyList<string> entries,
            string? supplied = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task CreateAsync(string path, ArchiveFormat format, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class StubRunner(ProcessResult result) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string program, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }
}
