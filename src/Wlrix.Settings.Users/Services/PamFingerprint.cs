namespace Wlrix.Settings.Users.Services;

/// <summary>Where a fingerprint can stand in for the password, machine-wide.</summary>
public enum PamTarget
{
    /// <summary><c>sudo</c> in a terminal: <c>/etc/pam.d/sudo</c>, and <c>sudo-i</c> when it has its own stack.</summary>
    Sudo,

    /// <summary>Administrator prompts: <c>/etc/pam.d/polkit-1</c>, which <c>polkit-agent-helper-1</c> runs.</summary>
    Polkit,
}

/// <summary>Whether a fingerprint is accepted for a <see cref="PamTarget"/>.</summary>
public enum FingerprintUse
{
    Off,
    On,

    /// <summary>The shared stack (<c>system-auth</c>, <c>common-auth</c>) already asks for one; not ours to change.</summary>
    BySystem,

    /// <summary>There is no such PAM service on this machine.</summary>
    Missing,
}

/// <summary>
/// Adding and removing pam_fprintd at the top of a PAM service's auth section, which is the whole
/// of "use a fingerprint for sudo". Shared by the panel, which only reads, and
/// <c>wlrix-users-helper</c>, which writes as root.
/// </summary>
/// <remarks>
/// <para>
/// Two lines go in, just before the first <c>auth</c> line (or Debian's <c>@include common-auth</c>):
/// <see cref="Marker"/> and <see cref="Line"/>. <c>sufficient</c>: a matching finger is enough, and
/// anything else -- a timeout, three mismatches, Ctrl+C -- falls through to the password exactly as
/// before. The leading <c>-</c> tells PAM to skip the line if the module is not installed, so
/// removing fprintd later can never leave <c>sudo</c> refusing everyone.
/// </para>
/// <para>
/// Turning it off removes every line that loads pam_fprintd from that file, ours or not, and our
/// marker. Nothing else in the file is touched.
/// </para>
/// </remarks>
public sealed class PamFingerprint(string root = "/")
{
    public const string Marker = "# wlRIX User Accounts: a fingerprint is accepted in place of the password.";
    public const string Line = "-auth      sufficient   pam_fprintd.so";

    /// <summary>Where a service's file is written, and where a vendor default may be read from.</summary>
    public string EtcDirectory => Path.Combine(root, "etc/pam.d");

    public string VendorDirectory => Path.Combine(root, "usr/lib/pam.d");

    /// <summary>The shared stacks every service includes, by distribution.</summary>
    private static readonly string[] BaseStacks = ["system-auth", "common-auth"];

    /// <summary>The services a target covers, the one that decides its state first.</summary>
    public static IReadOnlyList<string> Services(PamTarget target) => target switch
    {
        PamTarget.Sudo => ["sudo", "sudo-i"],
        PamTarget.Polkit => ["polkit-1"],
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    /// <summary>Whether pam_fprintd is installed at all; without it there is nothing to offer.</summary>
    public bool ModuleInstalled()
    {
        foreach (var directory in new[] { "usr/lib/security", "lib/security", "usr/lib64/security", "lib64/security" })
        {
            if (File.Exists(Path.Combine(root, directory, "pam_fprintd.so")))
                return true;
        }

        // Debian's multiarch directories: /usr/lib/x86_64-linux-gnu/security and the like.
        foreach (var lib in new[] { "usr/lib", "lib" })
        {
            var directory = Path.Combine(root, lib);
            if (!Directory.Exists(directory))
                continue;
            try
            {
                if (Directory.EnumerateDirectories(directory, "*-linux-gnu*")
                    .Any(arch => File.Exists(Path.Combine(arch, "security", "pam_fprintd.so"))))
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return false;
    }

    /// <summary>Whether a fingerprint is accepted for <paramref name="target"/> now.</summary>
    public FingerprintUse State(PamTarget target)
    {
        if (BaseStacks.Any(name => Read(name) is { } text && LoadsFprintd(text)))
            return FingerprintUse.BySystem;
        var text = Read(Services(target)[0]);
        if (text is null)
            return FingerprintUse.Missing;
        return LoadsFprintd(text) ? FingerprintUse.On : FingerprintUse.Off;
    }

    /// <summary>
    /// The files to write for <paramref name="target"/>, each with its new text: only those that
    /// change, and never a <c>sudo-i</c> that just includes <c>sudo</c>. A null text means remove
    /// the file: turning the fingerprint off put a copy of a vendor stack back to exactly what the
    /// vendor ships, and the copy would only stop it receiving updates. Throws
    /// <see cref="InvalidOperationException"/> when a file cannot be edited safely.
    /// </summary>
    public IReadOnlyList<(string Path, string? Text)> Plan(PamTarget target, bool enable)
    {
        var changes = new List<(string, string?)>();
        foreach (var service in Services(target))
        {
            var text = Read(service);
            if (text is null)
                continue;
            if (service == "sudo-i" && IncludesService(text, "sudo"))
                continue;
            var updated = enable ? Enable(text) : Disable(text);
            if (updated == text)
                continue;
            var vendor = Path.Combine(VendorDirectory, service);
            var restoresVendor = !enable && File.Exists(vendor) && File.ReadAllText(vendor) == updated;
            changes.Add((Path.Combine(EtcDirectory, service), restoresVendor ? null : updated));
        }

        return changes;
    }

    /// <summary>The service's file, from <c>/etc/pam.d</c> or else the vendor directory; null if neither has it.</summary>
    public string? Read(string service)
    {
        foreach (var directory in new[] { EtcDirectory, VendorDirectory })
        {
            var path = Path.Combine(directory, service);
            try
            {
                if (File.Exists(path))
                    return File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>The text with pam_fprintd added before the first auth line; unchanged if it is there already.</summary>
    internal static string Enable(string text)
    {
        if (LoadsFprintd(text))
            return text;
        var lines = SplitLines(text, out var newline, out var trailing);
        var at = lines.FindIndex(IsAuthLine);
        if (at < 0)
            throw new InvalidOperationException("it has no auth section to add the fingerprint to");
        lines.InsertRange(at, [Marker, Line]);
        return Join(lines, newline, trailing);
    }

    /// <summary>The text without any line that loads pam_fprintd, and without our marker.</summary>
    internal static string Disable(string text)
    {
        var lines = SplitLines(text, out var newline, out var trailing);
        var kept = lines.Where(line => line.Trim() != Marker && !LoadsFprintdLine(line)).ToList();
        if (kept.Count == lines.Count)
            return text;
        if (!kept.Any(IsAuthLine))
            throw new InvalidOperationException("removing the fingerprint would leave it with no auth section");
        return Join(kept, newline, trailing);
    }

    internal static bool LoadsFprintd(string text) =>
        text.Split('\n').Any(LoadsFprintdLine);

    private static bool LoadsFprintdLine(string line)
    {
        var hash = line.IndexOf('#');
        var code = hash < 0 ? line : line[..hash];
        return code.Contains("pam_fprintd", StringComparison.Ordinal);
    }

    /// <summary>An <c>auth</c> rule, or Debian's <c>@include common-auth</c>.</summary>
    private static bool IsAuthLine(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('#'))
            return false;
        var first = trimmed.Split((char[])[' ', '\t'], 2)[0];
        return first is "auth" or "-auth"
            || (first == "@include" && trimmed.Contains("auth", StringComparison.Ordinal));
    }

    /// <summary>Whether the stack includes another service's whole: <c>auth include sudo</c>, <c>@include sudo</c>.</summary>
    private static bool IncludesService(string text, string service) =>
        text.Split('\n').Select(line => line.Split('#')[0].Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            .Any(words => words is ["@include", var name] && name == service
                || words is [_, "include" or "substack", var other] && other == service);

    private static List<string> SplitLines(string text, out string newline, out bool trailing)
    {
        newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        trailing = text.EndsWith('\n');
        var body = trailing ? text[..^newline.Length] : text;
        return body.Split(newline).ToList();
    }

    private static string Join(List<string> lines, string newline, bool trailing) =>
        string.Join(newline, lines) + (trailing ? newline : string.Empty);
}
