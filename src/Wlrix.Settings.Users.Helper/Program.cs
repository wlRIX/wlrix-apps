using System.Runtime.Versioning;
using Wlrix.Settings.Users.Services;

namespace Wlrix.Settings.Users.Helper;

/// <summary>
/// The privileged half of User Accounts: the program <c>pkexec</c> runs as root.
///
/// The whole of it:
///
/// <list type="number">
///   <item>parse argv into <c>fingerprint on|off sudo|polkit</c>, refusing anything else;</item>
///   <item>ask <see cref="PamFingerprint"/> which files change and to what;</item>
///   <item>write each beside itself and rename it into place.</item>
/// </list>
///
/// Nothing in a request names a path: the services are fixed by the target, and the files are
/// always in <c>/etc/pam.d</c>. A service only in <c>/usr/lib/pam.d</c> is written to
/// <c>/etc/pam.d</c>, which PAM reads first, and the vendor file is left alone.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class Program
{
    private static int Main(string[] args)
    {
        var check = args is ["--check", ..];
        var rest = check ? args[1..] : args;

        if (rest is [] or ["-h" or "--help"])
        {
            Usage();
            return rest.Length == 0 ? 2 : 0;
        }

        if (rest is not ["fingerprint", var state and ("on" or "off"), var name and ("sudo" or "polkit")])
            return Fail($"unknown request: {string.Join(' ', rest)}");

        if (!check && !Environment.IsPrivilegedProcess)
            return Fail("must run as root; User Accounts starts this through pkexec");

        var target = name == "sudo" ? PamTarget.Sudo : PamTarget.Polkit;
        var pam = new PamFingerprint();
        if (pam.State(target) == FingerprintUse.BySystem)
            return Fail("the system's shared PAM stack already asks for a fingerprint; change it there");

        IReadOnlyList<(string Path, string? Text)> changes;
        try
        {
            changes = pam.Plan(target, enable: state == "on");
        }
        catch (InvalidOperationException ex)
        {
            return Fail($"cannot change the {name} PAM stack: {ex.Message}");
        }

        // --check prints what would be written, without writing it. It is how this is exercised
        // without root.
        if (check)
        {
            foreach (var (path, text) in changes)
                Console.Write(text is null ? $"==> remove {path}\n" : $"==> {path}\n{text}");
            return 0;
        }

        foreach (var (path, text) in changes)
        {
            if ((text is null ? Remove(path) : Write(path, text)) is { } problem)
                return Fail(problem);
        }

        return 0;
    }

    /// <summary>Write beside the file and rename over it, so PAM never reads half a stack. Null on success.</summary>
    private static string? Write(string path, string text)
    {
        var directory = Path.GetDirectoryName(path)!;
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.wlrix.{Environment.ProcessId}");
        try
        {
            using (var stream = new FileStream(temporary, new FileStreamOptions
                   {
                       Mode = FileMode.CreateNew,
                       Access = FileAccess.Write,
                       // 0644, as every PAM file: root writes, everyone reads.
                       UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                                        | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                   }))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(text);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The message about the write is the one worth reading.
            }

            return $"could not write {path}: {ex.Message}";
        }
    }

    /// <summary>Remove a copy that is the vendor's stack again. Null on success.</summary>
    private static string? Remove(string path)
    {
        try
        {
            File.Delete(path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"could not remove {path}: {ex.Message}";
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static void Usage()
    {
        Console.Error.WriteLine("""
            wlrix-users-helper -- the privileged half of the wlRIX User Accounts panel.

            Usage: wlrix-users-helper [--check] fingerprint on|off sudo|polkit

              fingerprint on    accept a fingerprint in place of the password: adds
                                `-auth sufficient pam_fprintd.so` to /etc/pam.d/sudo (and sudo-i)
                                or /etc/pam.d/polkit-1
              fingerprint off   remove every pam_fprintd line from those files
              --check           print what would be written, without writing it

            Normally started by User Accounts through pkexec. Prints nothing on success, and one
            line on stderr when it refuses.
            """);
    }
}
