using System.Globalization;
using Wlrix.Archiver.Models;
using Wlrix.Common;

namespace Wlrix.Archiver.Services.Archives;

/// <summary>Writes 7z archives, by driving the <c>7z</c> command.</summary>
/// <remarks>
/// SharpCompress reads 7z but has no encoder for it, so without this the format would be
/// permanently read-only — you could look inside a 7z and never change one. The LZMA encoder is
/// a large piece of work to bring in-process for one format, and p7zip is packaged everywhere,
/// so this shells out.
///
/// It is optional by construction: with no <c>7z</c> on the PATH,
/// <see cref="Supports"/> answers <see cref="ArchiveCapabilities.None"/>, the registry falls
/// back to <see cref="SharpCompressBackend"/>, and 7z archives are simply read-only. Nothing
/// fails, and no menu item lies about what it will do.
///
/// Filename encoding is a non-issue here, unlike everywhere else in this app: the 7z format
/// mandates UTF-16 for names, so there is no legacy code page to guess at.
/// </remarks>
public sealed class SevenZipCliBackend : IArchiveBackend
{
    /// <summary>Switches every invocation wants.</summary>
    /// <remarks>
    /// <c>-y</c> answers the prompts (there is no console to answer them on), and <c>-spd</c>
    /// turns off wildcard matching so an entry named <c>report[2].txt</c> is a filename rather
    /// than a pattern that matches nothing.
    /// </remarks>
    private static readonly string[] CommonSwitches = ["-y", "-spd"];

    private readonly IProcessRunner _runner;
    private readonly string? _sevenZip;

    public SevenZipCliBackend(IProcessRunner runner)
    {
        _runner = runner;
        // Resolved once at construction: the PATH does not change under a running app, and a
        // lookup per operation would be a syscall storm for a fixed answer.
        _sevenZip = Executables.Which("7z") ?? Executables.Which("7za");
    }

    public string Name => "7z";

    /// <summary>Whether the external tool was found. Public so the About box can say so.</summary>
    public bool IsAvailable => _sevenZip is not null;

    public ArchiveCapabilities Supports(ArchiveFormat format) =>
        _sevenZip is not null && format == ArchiveFormat.SevenZip
            // Encrypt as well, which no other backend here claims: `7z a -p` encrypts what it
            // writes, so an archive created through this one can actually be protected.
            ? ArchiveCapabilities.All | ArchiveCapabilities.Encrypt
            : ArchiveCapabilities.None;

    public async Task<OpenArchive> OpenAsync(string path, ArchiveFormat format,
        string? password = null,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // `7z l` prints nothing until it is done, so there is no percentage to report — only
        // that the read has started. A phase with no fraction is what the status line shows as
        // an indeterminate "Reading archive...".
        progress?.Report(new ArchiveProgress(ArchivePhase.Reading));
        var entries = await ListAsync(path, password, cancellationToken).ConfigureAwait(false);
        return new OpenArchive(path, format, Supports(format), entries);
    }

    public async Task ExtractAsync(string path, ArchiveFormat format,
        IReadOnlyList<string> entryPaths, string destinationDirectory, bool flatten = false,
        string? password = null,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDirectory);
        progress?.Report(new ArchiveProgress(ArchivePhase.Extracting));

        // `x` keeps paths, `e` flattens. Handing 7z the mode rather than extracting fully and
        // moving files afterwards keeps the two backends' `flatten` meaning identical.
        var arguments = new List<string> { flatten ? "e" : "x" };
        arguments.AddRange(CommonSwitches);
        AddReadPassword(arguments, password);
        arguments.Add("-o" + destinationDirectory);
        arguments.Add("--");
        arguments.Add(path);
        // Directories are expanded to their contents here rather than left to 7z, so that
        // selecting a folder means the same thing it means in the managed backend.
        arguments.AddRange(await ExpandAsync(path, entryPaths, password, cancellationToken)
            .ConfigureAwait(false));

        await RunAsync(arguments, path, password, ReadInput(password), cancellationToken).ConfigureAwait(false);
    }

    public async Task AddAsync(string path, ArchiveFormat format,
        IReadOnlyList<string> sourcePaths, string destinationPrefix = "",
        string? password = null, bool encryptNames = false,
        CancellationToken cancellationToken = default)
    {
        if (sourcePaths.Count == 0)
            return;

        if (destinationPrefix.Length != 0)
        {
            // 7z stores what it is given relative to the current directory and offers no "put
            // it under this prefix" switch. Rather than pretend, the UI only ever adds at the
            // root for this backend.
            throw new ArchiveException(
                "Adding into a subdirectory of a 7z archive is not supported.");
        }

        var arguments = new List<string> { "a" };
        arguments.AddRange(CommonSwitches);
        AddWritePassword(arguments, password);
        // Header encryption, which only means anything alongside a password: -mhe with none is
        // nothing to encrypt the headers with. Passing it on a later add is harmless -- an
        // archive written with it keeps encrypted headers either way, measured -- but passing it
        // to an archive that does not have them would convert one, so the caller says when.
        if (encryptNames && !string.IsNullOrEmpty(password))
            arguments.Add("-mhe=on");

        arguments.Add("--");
        arguments.Add(path);
        arguments.AddRange(sourcePaths);

        await RunAsync(arguments, path, password, WriteInput(password), cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string path, ArchiveFormat format,
        IReadOnlyList<string> entryPaths, string? password = null,
        CancellationToken cancellationToken = default)
    {
        if (entryPaths.Count == 0)
            return;

        var arguments = new List<string> { "d" };
        arguments.AddRange(CommonSwitches);
        AddWritePassword(arguments, password);
        arguments.Add("--");
        arguments.Add(path);
        arguments.AddRange(await ExpandAsync(path, entryPaths, password, cancellationToken)
            .ConfigureAwait(false));

        await RunAsync(arguments, path, password, WriteInput(password), cancellationToken).ConfigureAwait(false);
    }

    public Task CreateAsync(string path, ArchiveFormat format,
        CancellationToken cancellationToken = default) =>
        // 7z will not write an archive with nothing in it, and there is no switch that makes
        // it. The file is created when the first entry is added; until then File / New has
        // nothing to write, which is why this is a no-op rather than an error.
        Task.CompletedTask;

    /// <summary>Resolves a selection to the concrete entry paths it covers.</summary>
    private async Task<IReadOnlyList<string>> ExpandAsync(string path,
        IReadOnlyList<string> entryPaths, string? password, CancellationToken cancellationToken)
    {
        if (entryPaths.Count == 0)
            return [];

        var selection = new EntrySelection(entryPaths);
        var entries = await ListAsync(path, password, cancellationToken).ConfigureAwait(false);
        return entries
            .Where(entry => !entry.IsDirectory && selection.Contains(entry.Path))
            .Select(entry => entry.Path)
            .ToList();
    }

    /// <summary>Reads the entry list out of <c>7z l -slt</c>.</summary>
    private async Task<IReadOnlyList<ArchiveEntry>> ListAsync(string path, string? password,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "l", "-slt" };
        arguments.AddRange(CommonSwitches);
        AddReadPassword(arguments, password);
        arguments.Add("--");
        arguments.Add(path);

        var result = await RunAsync(arguments, path, password, ReadInput(password), cancellationToken).ConfigureAwait(false);
        return ParseListing(result.StandardOutput);
    }

    /// <summary>
    /// Parses the <c>-slt</c> "technical" listing: <c>Key = Value</c> lines in blocks separated
    /// by a rule of dashes.
    /// </summary>
    /// <remarks>
    /// The rule of dashes is the load-bearing part, and skipping it is the mistake to avoid: the
    /// header that comes before it is itself a block of <c>Key = Value</c> lines, including a
    /// <c>Path =</c> naming the archive. Parse from the top and the archive appears as its own
    /// first entry.
    ///
    /// Internal rather than private so the tests can feed it captured output without a 7z on the
    /// machine running them.
    /// </remarks>
    internal static IReadOnlyList<ArchiveEntry> ParseListing(string output)
    {
        var entries = new List<ArchiveEntry>();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var started = false;

        void Flush()
        {
            if (fields.Count != 0 && fields.TryGetValue("Path", out var entryPath))
                entries.Add(ToEntry(entryPath, fields));
            fields.Clear();
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("----------", StringComparison.Ordinal))
            {
                // Everything gathered so far is the header about the archive itself.
                fields.Clear();
                started = true;
                continue;
            }

            if (!started)
                continue;

            var separator = line.IndexOf(" = ", StringComparison.Ordinal);
            if (separator < 0)
            {
                // A blank line ends a block. 7z also prints a trailing summary of blank-ish
                // lines, which flushes harmlessly because it carries no `Path`.
                Flush();
                continue;
            }

            var key = line[..separator];
            // A second `Path` without an intervening blank line means the writer omitted the
            // separator; treat it as the start of the next block rather than losing an entry.
            if (key == "Path" && fields.ContainsKey("Path"))
                Flush();

            fields[key] = line[(separator + 3)..];
        }

        Flush();
        return entries;
    }

    private static ArchiveEntry ToEntry(string entryPath, Dictionary<string, string> fields)
    {
        // "Attributes = D drwxr-xr-x" for a directory, "A -rw-r--r--" for a file. The Unix mode
        // string is only present when the archive was written on a Unix host.
        var attributes = fields.GetValueOrDefault("Attributes", string.Empty);
        var isDirectory = attributes.StartsWith('D');

        return new ArchiveEntry(
            Path: ArchiveTree.Normalize(entryPath),
            IsDirectory: isDirectory,
            Size: ParseLong(fields.GetValueOrDefault("Size")),
            CompressedSize: ParseLong(fields.GetValueOrDefault("Packed Size")),
            Modified: ParseDate(fields.GetValueOrDefault("Modified")),
            Mode: ParseMode(attributes),
            Owner: null,
            Group: null,
            IsEncrypted: fields.GetValueOrDefault("Encrypted") == "+");
    }

    private static long? ParseLong(string? text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static DateTime? ParseDate(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : null;

    /// <summary>Reads the <c>drwxr-xr-x</c> tail of an attributes field back into mode bits.</summary>
    private static int? ParseMode(string attributes)
    {
        var space = attributes.IndexOf(' ');
        if (space < 0)
            return null;

        var text = attributes[(space + 1)..];
        if (text.Length != 10)
            return null;

        var mode = 0;
        for (var i = 0; i < 9; i++)
        {
            if (text[i + 1] != '-')
                mode |= 1 << (8 - i);
        }

        return mode;
    }

    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, string path,
        string? password, string? standardInput, CancellationToken cancellationToken)
    {
        if (_sevenZip is null)
            throw new ArchiveException("7z is not installed.");

        var result = await _runner.RunAsync(_sevenZip, arguments, standardInput, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            // 7z puts the useful line on stdout as often as on stderr, so both are candidates
            // and the last non-blank one is the message rather than the banner.
            var message = LastLine(result.StandardError) ?? LastLine(result.StandardOutput)
                ?? $"7z exited with {result.ExitCode}";

            // 7z says the same thing whichever half it is -- "Cannot open encrypted archive.
            // Wrong password?" for a header-encrypted archive, "Data Error in encrypted file.
            // Wrong password?" for an encrypted member -- and says it whether a password was
            // wrong or simply absent. The phrase is the signal; which question to ask is decided
            // from whether we supplied one.
            if (result.StandardOutput.Contains("Wrong password", StringComparison.OrdinalIgnoreCase)
                || result.StandardError.Contains("Wrong password", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArchivePasswordException(message, passwordSupplied: !string.IsNullOrEmpty(password));
            }

            throw new ArchiveException($"{System.IO.Path.GetFileName(path)}: {message}");
        }

        return result;
    }

    /// <summary>Adds <c>-p</c> to a write, but only when there is a real password.</summary>
    /// <remarks>
    /// The opposite rule to a read, and both halves are measured rather than assumed.
    ///
    /// <para>
    /// It has to be passed when there is one: <c>7z a</c> without <c>-p</c> against an encrypted
    /// archive succeeds and writes the new member <em>in the clear</em>, sitting beside encrypted
    /// ones. Nothing reports it. With <c>-p</c>, the new entry is encrypted like the rest and an
    /// archive written with <c>-mhe=on</c> keeps its encrypted headers.
    /// </para>
    ///
    /// <para>
    /// And it must not be passed when there is not: a bare <c>-p</c> on a write is a request to
    /// encrypt, and it asks for the password to do it with.
    /// </para>
    ///
    /// <para>
    /// <b>The switch never carries the password.</b> The arguments of a running process are
    /// readable by every user on the machine. A bare <c>-p</c> asks for the password, and
    /// <see cref="WriteInput"/> answers on standard input. Measured against 7-Zip 26.05, non-ASCII
    /// passwords included, which come out the same either way under the C locale.
    /// </para>
    /// </remarks>
    private static void AddWritePassword(List<string> arguments, string? password)
    {
        if (!string.IsNullOrEmpty(password))
            arguments.Add("-p");
    }

    /// <summary>The <c>-p</c> switch on a read, which is there only when there is no password.</summary>
    /// <remarks>
    /// With no password it is passed empty, and that is the point: given no <c>-p</c> at all, 7z
    /// asks for one, and an empty answer to that is "Break signaled" rather than the "Wrong
    /// password?" that <see cref="RunAsync"/> recognizes. With one, it is left off, so that 7z asks
    /// when it reaches something encrypted and <see cref="ReadInput"/> answers.
    ///
    /// <para>
    /// Reads only. <c>a</c> and <c>d</c> never get the empty one, because <c>-p</c> on a write is
    /// an instruction to encrypt, and quietly encrypting somebody's archive with the empty string
    /// would be a far worse bug than the one this fixes.
    /// </para>
    /// </remarks>
    private static void AddReadPassword(List<string> arguments, string? password)
    {
        if (string.IsNullOrEmpty(password))
            arguments.Add("-p");
    }

    /// <summary>What 7z reads on standard input for a read: the password, once.</summary>
    private static string? ReadInput(string? password) =>
        string.IsNullOrEmpty(password) ? null : password + "\n";

    /// <summary>What 7z reads on standard input for a write: the password, and again to verify it.</summary>
    private static string? WriteInput(string? password) =>
        string.IsNullOrEmpty(password) ? null : password + "\n" + password + "\n";

    private static string? LastLine(string text) => text
        .Split('\n')
        .Select(line => line.Trim())
        .LastOrDefault(line => line.Length != 0);
}
