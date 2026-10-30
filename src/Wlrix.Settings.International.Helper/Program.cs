using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Wlrix.Settings.International.Core;
using Wlrix.Common;

namespace Wlrix.Settings.International.Helper;

/// <summary>
/// The privileged half of the International panel: the program <c>pkexec</c> runs as root to make
/// a locale usable.
///
/// The whole of it is:
///
/// <list type="number">
///   <item>take <c>generate</c> and one locale name, refusing anything else;</item>
///   <item>refuse a name that is not on glibc's <c>SUPPORTED</c> list;</item>
///   <item>enable it in <c>/etc/locale.gen</c>, written to a temporary file and renamed into place;</item>
///   <item>run <c>locale-gen</c>.</item>
/// </list>
///
/// Both files are fixed. The one thing a request carries is a name, which has to be spelled like a
/// locale and be on a list root owns, so the caller -- who is not trusted -- cannot make this write
/// anything else.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class Program
{
    /// <summary>How long <c>locale-gen</c> gets. It compiles every enabled locale, a few seconds each.</summary>
    private static readonly TimeSpan GenerateTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Pinned rather than inherited, as in the other helpers: this resolves a program name as root.
    /// Passed to <see cref="Executables.StartInfoFor"/> as well as set on the child, because
    /// Process.Start would search the current directory and ignore a PATH set only on the child.
    /// </summary>
    private const string SafePath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

    private static int Main(string[] args)
    {
        var check = args is ["--check", ..];
        var rest = check ? args[1..] : args;

        if (rest is ["-h" or "--help"])
        {
            Usage();
            return 0;
        }
        if (rest is not ["generate", var name])
        {
            Usage();
            return 2;
        }

        if (!LocaleGen.IsWellFormed(name))
            return Fail($"{name} is not a locale name");

        SupportedLocale? locale;
        try
        {
            locale = LocaleGen.ParseSupported(File.ReadAllText(LocaleGen.SupportedPath))
                .FirstOrDefault(entry => entry.Name == name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"could not read {LocaleGen.SupportedPath}: {ex.Message}");
        }
        if (locale is null)
            return Fail($"{name} is not a locale glibc can build");

        if (!check && !Environment.IsPrivilegedProcess)
            return Fail("must run as root; the International panel starts this through pkexec");

        return Generate(locale, check);
    }

    private static int Generate(SupportedLocale locale, bool check)
    {
        string existing;
        try
        {
            existing = File.Exists(LocaleGen.LocaleGenPath) ? File.ReadAllText(LocaleGen.LocaleGenPath) : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"could not read {LocaleGen.LocaleGenPath}: {ex.Message}");
        }

        var text = LocaleGen.Enable(existing, locale);
        if (check)
        {
            Console.WriteLine(text == existing
                ? $"{locale.Name} is already enabled; would run locale-gen"
                : $"would enable {locale.Name} {locale.Charset} in {LocaleGen.LocaleGenPath} and run locale-gen");
            return 0;
        }

        if (text != existing && Write(text) is { } problem)
            return Fail(problem);

        return RunLocaleGen();
    }

    /// <summary>Replace <c>locale.gen</c> in one rename, so a crash leaves the old file or the new one.</summary>
    private static string? Write(string text)
    {
        var temporary = LocaleGen.LocaleGenPath + ".wlrix-new";
        try
        {
            using (var stream = new FileStream(temporary, new FileStreamOptions
                   {
                       Mode = FileMode.Create,
                       Access = FileAccess.Write,
                       UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                                        | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                   }))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(text);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, LocaleGen.LocaleGenPath, overwrite: true);
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
            return $"could not write {LocaleGen.LocaleGenPath}: {ex.Message}";
        }
    }

    private static int RunLocaleGen()
    {
        var info = Executables.StartInfoFor("locale-gen", SafePath);
        info.UseShellExecute = false;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.Environment["PATH"] = SafePath;
        info.Environment["LC_ALL"] = "C";

        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return Fail("could not start locale-gen");

            var error = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(GenerateTimeout))
            {
                process.Kill();
                return Fail("locale-gen did not finish");
            }

            return process.ExitCode == 0 ? 0 : Fail($"locale-gen failed: {error.Result.Trim()}");
        }
        catch (Win32Exception ex)
        {
            return Fail($"could not run locale-gen: {ex.Message}");
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
            wlrix-locale-helper -- the privileged half of the wlRIX International panel.

            Usage: wlrix-locale-helper [--check] generate LOCALE

              generate  enable LOCALE in /etc/locale.gen and run locale-gen; LOCALE must be
                        listed in /usr/share/i18n/SUPPORTED, e.g. de_DE.UTF-8
              --check   validate and say what would happen, without doing it

            Normally started by the International panel through pkexec. Prints nothing on
            success, and one line on stderr when it refuses.
            """);
    }
}
