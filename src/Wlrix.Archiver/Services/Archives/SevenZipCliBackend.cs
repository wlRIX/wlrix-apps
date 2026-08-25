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
            ? ArchiveCapabilities.All
            : ArchiveCapabilities.None;

    public async Task<OpenArchive> OpenAsync(string path, ArchiveFormat format,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // `7z l` prints nothing until it is done, so there is no percentage to report — only
        // that the read has started. A phase with no fraction is what the status line shows as
        // an indeterminate "Reading archive...".
        progress?.Report(new ArchiveProgress(ArchivePhase.Reading));
        var entries = await ListAsync(path, cancellationToken).ConfigureAwait(false);
        return new OpenArchive(path, format, Supports(format), entries);
    }

    public async Task ExtractAsync(string path, ArchiveFormat format,
        IReadOnlyList<string> entryPaths, string destinationDirectory, bool flatten = false,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDirectory);
        progress?.Report(new ArchiveProgress(ArchivePhase.Extracting));

        // `x` keeps paths, `e` flattens. Handing 7z the mode rather than extracting fully and
        // moving files afterwards keeps the two backends' `flatten` meaning identical.
        var arguments = new List<string> { flatten ? "e" : "x" };
        arguments.AddRange(CommonSwitches);
        arguments.Add("-o" + destinationDirectory);
        arguments.Add("--");
        arguments.Add(path);
        // Directories are expanded to their contents here rather than left to 7z, so that
        // selecting a folder means the same thing it means in the managed backend.
        arguments.AddRange(await ExpandAsync(path, entryPaths, cancellationToken)
            .ConfigureAwait(false));

        await RunAsync(arguments, path, cancellationToken).ConfigureAwait(false);
    }

    public async Task AddAsync(string path, ArchiveFormat format,
        IReadOnlyList<string> sourcePaths, string destinationPrefix = "",
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
        arguments.Add("--");
        arguments.Add(path);
        arguments.AddRange(sourcePaths);

        await RunAsync(arguments, path, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string path, ArchiveFormat format,
        IReadOnlyList<string> entryPaths, CancellationToken cancellationToken = default)
    {
        if (entryPaths.Count == 0)
            return;

        var arguments = new List<string> { "d" };
        arguments.AddRange(CommonSwitches);
        arguments.Add("--");
        arguments.Add(path);
        arguments.AddRange(await ExpandAsync(path, entryPaths, cancellationToken)
            .ConfigureAwait(false));

        await RunAsync(arguments, path, cancellationToken).ConfigureAwait(false);
    }

    public Task CreateAsync(string path, ArchiveFormat format,
        CancellationToken cancellationToken = default) =>
        // 7z will not write an archive with nothing in it, and there is no switch that makes
        // it. The file is created when the first entry is added; until then File / New has
        // nothing to write, which is why this is a no-op rather than an error.
        Task.CompletedTask;

    /// <summary>Resolves a selection to the concrete entry paths it covers.</summary>
    private async Task<IReadOnlyList<string>> ExpandAsync(string path,
        IReadOnlyList<string> entryPaths, CancellationToken cancellationToken)
    {
        if (entryPaths.Count == 0)
            return [];

        var selection = new EntrySelection(entryPaths);
        var entries = await ListAsync(path, cancellationToken).ConfigureAwait(false);
        return entries
            .Where(entry => !entry.IsDirectory && selection.Contains(entry.Path))
            .Select(entry => entry.Path)
            .ToList();
    }

    /// <summary>Reads the entry list out of <c>7z l -slt</c>.</summary>
    private async Task<IReadOnlyList<ArchiveEntry>> ListAsync(string path,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "l", "-slt" };
        arguments.AddRange(CommonSwitches);
        arguments.Add("--");
        arguments.Add(path);

        var result = await RunAsync(arguments, path, cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
    {
        if (_sevenZip is null)
            throw new ArchiveException("7z is not installed.");

        var result = await _runner.RunAsync(_sevenZip, arguments, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            // 7z puts the useful line on stdout as often as on stderr, so both are candidates
            // and the last non-blank one is the message rather than the banner.
            var message = LastLine(result.StandardError) ?? LastLine(result.StandardOutput)
                ?? $"7z exited with {result.ExitCode}";
            throw new ArchiveException($"{System.IO.Path.GetFileName(path)}: {message}");
        }

        return result;
    }

    private static string? LastLine(string text) => text
        .Split('\n')
        .Select(line => line.Trim())
        .LastOrDefault(line => line.Length != 0);
}
