using System.Diagnostics;

namespace Wlrix.Files.Core.Thumbnails;

/// <summary>One installed <c>.thumbnailer</c>: what it handles and how to run it.</summary>
/// <param name="Command">The <c>Exec</c> line, field codes and all.</param>
/// <param name="TryExec">The program to look for before believing the entry, if it named one.</param>
/// <param name="MimeTypes">The types it claims.</param>
/// <param name="Source">Where it came from, for the log.</param>
/// <param name="OutputSuffix">
/// What the program appends to the name it was given, when it does not write exactly there.
/// Empty for everything the spec describes, and only ever set on the built-in entries below:
/// an installed <c>.thumbnailer</c> has no way to say this and none needs to.
/// </param>
public sealed record ThumbnailerEntry(
    string Command,
    string? TryExec,
    IReadOnlySet<string> MimeTypes,
    string Source,
    string OutputSuffix = "")
{
    /// <summary>Whether the program it needs is actually installed.</summary>
    /// <remarks>
    /// An entry outlives its package often enough to be worth checking: the file is dropped in
    /// a shared directory and nothing removes it if the binary goes. Running a missing program
    /// is a failure per file rather than one that can be answered once.
    /// </remarks>
    public bool IsAvailable => TryExec is null || Which(TryExec) is not null;

    internal static string? Which(string program)
    {
        if (program.Contains('/'))
            return File.Exists(program) ? program : null;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, program);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}

/// <summary>
/// Thumbnails from the programs the system already installs for the job.
/// </summary>
/// <remarks>
/// The other half of the Thumbnail Managing Standard. M8 implemented the cache — where a
/// thumbnail goes, what it is called and how it is invalidated — and this is the part that says
/// who makes one: a <c>.thumbnailer</c> file in <c>$XDG_DATA_DIRS/thumbnailers</c> naming a
/// program, the types it handles, and a command line with <c>%i</c>, <c>%u</c>, <c>%o</c> and
/// <c>%s</c> in it.
///
/// <para>
/// One implementation, and video, audio cover art, HEIF, JPEG XL and office documents all
/// arrive at once — whatever this machine has installed, which on the one this was written on
/// is <c>ffmpegthumbnailer</c>, four <c>glycin</c> entries and <c>gsf-office-thumbnailer</c>.
/// Writing a video thumbnailer instead would have meant a new dependency to do worse what
/// every other file manager on the system already does this way.
/// </para>
///
/// <para>
/// <b>These are other people's programs run on the user's files.</b> Each one gets a deadline
/// and is killed when it passes: <c>ffmpegthumbnailer</c> on a truncated video is the case
/// that hangs, and a hung thumbnailer with no deadline takes a queue slot with it forever.
/// Output goes to a temporary file that is deleted whatever happens, and nothing is believed
/// unless the program both exits cleanly and leaves a file behind.
/// </para>
/// </remarks>
public sealed class ExternalThumbnailer(
    IReadOnlyList<ThumbnailerEntry> entries,
    TimeSpan? deadline = null) : IThumbnailer
{
    /// <summary>How long a thumbnailer gets before it is killed.</summary>
    /// <remarks>
    /// Generous, because a first frame from a large video on a cold cache is genuinely slow,
    /// and the cost of being wrong in this direction is one missing preview.
    /// </remarks>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(20);

    private readonly TimeSpan _deadline = deadline ?? DefaultDeadline;

    /// <summary>The entries installed on this machine, plus the built-in fallbacks.</summary>
    public static ExternalThumbnailer Load() => new(Discover(SearchPaths()));

    /// <summary>The types anything here can handle.</summary>
    public IReadOnlyList<ThumbnailerEntry> Entries => entries;

    /// <inheritdoc/>
    public bool CanHandle(string mimeType) => Find(mimeType) is not null;

    /// <inheritdoc/>
    /// <remarks>
    /// These seek. A frame out of a film and a page out of a document cost the same whatever
    /// the rest of the file weighs, so the image-sized cap must not apply — it would rule out
    /// most videos, which are exactly what this was added for.
    /// </remarks>
    public bool ReadsWholeFile => false;

    /// <inheritdoc/>
    /// <remarks>
    /// The overload without a type cannot be answered: these entries are keyed by MIME type and
    /// nothing here should be guessing one from an extension when the caller knows it.
    /// </remarks>
    public Task<byte[]?> RenderAsync(string localPath, int size, CancellationToken cancellationToken) =>
        Task.FromResult<byte[]?>(null);

    /// <summary>Renders by running whichever program claims the type.</summary>
    public async Task<byte[]?> RenderAsync(
        string localPath, string mimeType, int size, CancellationToken cancellationToken)
    {
        if (Find(mimeType) is not { } entry)
            return null;

        var output = Path.Combine(
            Path.GetTempPath(), $"wlrix-thumb-{Guid.NewGuid():N}.png");

        try
        {
            if (!await RunAsync(entry, localPath, output, size, cancellationToken).ConfigureAwait(false))
                return null;

            var written = output + entry.OutputSuffix;

            // Both halves matter. A program can exit zero having written nothing, and one that
            // wrote something and then failed has left a partial file.
            return File.Exists(written)
                ? await File.ReadAllBytesAsync(written, cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            try
            {
                File.Delete(output + entry.OutputSuffix);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A thumbnail nobody can delete is a stray file in the temp directory, which
                // the system clears anyway. Not worth failing the render over.
            }
        }
    }

    private ThumbnailerEntry? Find(string mimeType) =>
        entries.FirstOrDefault(entry => entry.MimeTypes.Contains(mimeType));

    private async Task<bool> RunAsync(
        ThumbnailerEntry entry, string input, string output, int size, CancellationToken cancellationToken)
    {
        var arguments = Substitute(entry.Command, input, output, size);
        if (arguments.Count == 0)
            return false;

        using var process = new Process();
        process.StartInfo.FileName = arguments[0];
        foreach (var argument in arguments.Skip(1))
            process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.UseShellExecute = false;
        // Nothing reads either, and a thumbnailer that decides to complain at length would
        // otherwise fill a pipe nobody is draining and block forever.
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;

        try
        {
            if (!process.Start())
                return false;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_deadline);

        try
        {
            // Drained rather than ignored, for the reason above.
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            Kill(process);

            // A deadline that passed is a failure, and the caller records it as one so the
            // file is not tried again on every scroll. A cancellation from above is the
            // window going away, and that is not the file's fault.
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                       or System.ComponentModel.Win32Exception)
        {
            // It exited between the check and the kill, or the kernel will not say. Either
            // way there is nothing further to do about it.
        }
    }

    /// <summary>
    /// The command line with the spec's field codes filled in.
    /// </summary>
    /// <remarks>
    /// <c>%i</c> is the input path, <c>%u</c> its URI, <c>%o</c> where to write the PNG and
    /// <c>%s</c> the size in pixels. Split on whitespace outside quotes and substituted per
    /// argument, never by pasting into a shell: a file called <c>; rm -rf ~</c> is a perfectly
    /// legal name, and there is no shell here for it to mean anything to.
    /// </remarks>
    internal static List<string> Substitute(string command, string input, string output, int size)
    {
        var uri = Location.FromLocalPath(input).ToUriString();
        var arguments = new List<string>();

        foreach (var token in Split(command))
        {
            arguments.Add(token
                .Replace("%i", input, StringComparison.Ordinal)
                .Replace("%u", uri, StringComparison.Ordinal)
                .Replace("%o", output, StringComparison.Ordinal)
                .Replace("%s", size.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        }

        return arguments;
    }

    /// <summary>Splits a command line on whitespace, honoring quotes.</summary>
    internal static List<string> Split(string command)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';

        foreach (var c in command)
        {
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                else
                    current.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>The directories a <c>.thumbnailer</c> may live in, in precedence order.</summary>
    public static IEnumerable<string> SearchPaths()
    {
        var home = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(home))
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile))
                home = Path.Combine(profile, ".local", "share");
        }
        if (!string.IsNullOrEmpty(home))
            yield return Path.Combine(home, "thumbnailers");

        var dirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        if (string.IsNullOrEmpty(dirs))
            dirs = "/usr/local/share:/usr/share";
        foreach (var dir in dirs.Split(':', StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(dir, "thumbnailers");
    }

    /// <summary>
    /// Reads the installed entries, and adds the built-in ones for types nothing claims.
    /// </summary>
    /// <remarks>
    /// First directory wins per type, which is what lets a user override a system thumbnailer
    /// by dropping a file in their own data directory.
    /// </remarks>
    public static IReadOnlyList<ThumbnailerEntry> Discover(IEnumerable<string> directories)
    {
        var entries = new List<ThumbnailerEntry>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var directory in directories)
        {
            string[] files;
            try
            {
                if (!Directory.Exists(directory))
                    continue;
                files = Directory.GetFiles(directory, "*.thumbnailer");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(files, StringComparer.Ordinal);
            foreach (var file in files)
                Add(entries, claimed, Parse(file));
        }

        foreach (var builtin in Builtins)
            Add(entries, claimed, builtin);

        return entries;
    }

    private static void Add(List<ThumbnailerEntry> entries, HashSet<string> claimed, ThumbnailerEntry? entry)
    {
        if (entry is null || !entry.IsAvailable)
            return;

        var fresh = entry.MimeTypes.Where(claimed.Add).ToHashSet(StringComparer.Ordinal);
        if (fresh.Count > 0)
            entries.Add(entry with { MimeTypes = fresh });
    }

    /// <summary>
    /// Thumbnailers for types the system usually ships no <c>.thumbnailer</c> for.
    /// </summary>
    /// <remarks>
    /// PDF is the one that matters, and it is a gap rather than an oversight: GNOME's PDF
    /// thumbnailer lives inside Evince and is installed with it, so a machine with poppler but
    /// no Evince — which is a normal machine — can render a PDF and has nothing declaring that
    /// it can. Expressed as an entry rather than as code so that it goes through the same
    /// deadline, the same argument handling and the same availability check as the rest, and so
    /// that a real <c>.thumbnailer</c> for PDF silently takes precedence when one is installed.
    /// </remarks>
    public static IReadOnlyList<ThumbnailerEntry> Builtins { get; } =
    [
        new ThumbnailerEntry(
            // -singlefile stops it numbering the pages, and -scale-to asks in pixels where -r
            // would ask in DPI. It still appends ".png" to the name it is given, with or
            // without -singlefile and even when the name already ends in .png — which is why
            // this entry declares a suffix and a spec-compliant one does not.
            "pdftoppm -png -singlefile -scale-to %s %i %o",
            "pdftoppm",
            new HashSet<string>(StringComparer.Ordinal) { "application/pdf" },
            "built-in",
            OutputSuffix: ".png")
    ];

    /// <summary>Reads one <c>.thumbnailer</c> file, or null if it is not one.</summary>
    /// <remarks>
    /// Desktop-entry syntax with a group of its own. Hand-read rather than routed through the
    /// desktop entry parser because this is three keys and the two formats agree only by
    /// coincidence of syntax — a <c>.thumbnailer</c> is not a desktop entry and gaining a
    /// <c>Type</c> or a <c>Name</c> would be meaningless.
    /// </remarks>
    public static ThumbnailerEntry? Parse(string path)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var inGroup = false;
        string? command = null;
        string? tryExec = null;
        var types = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            if (line[0] == '[')
            {
                if (inGroup)
                    break;
                inGroup = string.Equals(line, "[Thumbnailer Entry]", StringComparison.Ordinal);
                continue;
            }

            if (!inGroup)
                continue;

            var equals = line.IndexOf('=');
            if (equals <= 0)
                continue;

            var key = line[..equals].TrimEnd();
            var value = line[(equals + 1)..].TrimStart();
            switch (key)
            {
                case "Exec":
                    command = value;
                    break;
                case "TryExec":
                    tryExec = value;
                    break;
                case "MimeType":
                    foreach (var type in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        types.Add(type);
                    break;
            }
        }

        return string.IsNullOrWhiteSpace(command) || types.Count == 0
            ? null
            : new ThumbnailerEntry(command, tryExec, types, path);
    }
}
