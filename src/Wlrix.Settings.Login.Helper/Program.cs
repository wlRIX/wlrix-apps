using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Wlrix.Settings.Login.Core;

namespace Wlrix.Settings.Login.Helper;

/// <summary>
/// The privileged half of Login Settings: the program <c>pkexec</c> runs as root.
///
/// Short enough to read end to end, like the Software Manager's helper. The whole of it is:
///
/// <list type="number">
///   <item>parse argv into <c>write</c> with some values, or <c>reset</c>, refusing anything else;</item>
///   <item>for <c>write</c>, splice those values into <c>/etc/wlrix/greeter.toml</c>, have the
///   compositor check the result, and rename it into place;</item>
///   <item>for <c>reset</c>, remove that file.</item>
/// </list>
///
/// The file is fixed. Nothing in a request names a path, and nothing in it is more than a short
/// string of letters, digits and a little punctuation, or a number in range -- see
/// <see cref="GreeterRequest"/>, which runs here even though the panel ran it too, because the
/// caller is not trusted.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class Program
{
    /// <summary>How long the compositor gets to say whether it would accept the file.</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Pinned rather than inherited, as in wlrix-pkg-helper: this resolves a program name as root.</summary>
    private const string SafePath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

    private static int Main(string[] args)
    {
        var check = args is ["--check", ..];
        var rest = check ? args[1..] : args;

        if (rest.Length == 0 || rest[0] is "-h" or "--help")
        {
            Usage();
            return rest.Length == 0 ? 2 : 0;
        }

        if (!GreeterRequest.TryParse(rest, out var request, out var error))
            return Fail(error);

        if (!check && !Environment.IsPrivilegedProcess)
            return Fail("must run as root; the Login Settings panel starts this through pkexec");

        return request.Verb switch
        {
            GreeterVerb.Write => Write(request.Config, check),
            GreeterVerb.Reset => Reset(check),
            _ => Fail($"unknown verb {request.Verb}"),
        };
    }

    private static int Write(GreeterConfig config, bool check)
    {
        var seeding = !File.Exists(GreeterConfigFile.GreeterPath);
        string existing;
        try
        {
            existing = !seeding ? File.ReadAllText(GreeterConfigFile.GreeterPath)
                : File.Exists(GreeterConfigFile.SystemPath) ? File.ReadAllText(GreeterConfigFile.SystemPath)
                : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"could not read the current settings: {ex.Message}");
        }

        var text = GreeterConfigFile.Apply(existing, config, seeding);

        // The splice is line-based, so make sure it said what it was asked to before anything is
        // written: a hand-edited file shaped in a way the splice did not expect is refused here
        // rather than half-applied.
        if (!GreeterConfigFile.TryParse(text, out var written, out var problem))
            return Fail($"the result would not be valid TOML: {problem}");
        if (written != config)
            return Fail($"{GreeterConfigFile.GreeterPath} is laid out in a way this cannot edit safely; edit it by hand");

        // --check prints the file that would be written, without writing it. It is how this is
        // exercised without root.
        if (check)
        {
            Console.Write(text);
            return 0;
        }

        var directory = Path.GetDirectoryName(GreeterConfigFile.GreeterPath)!;
        var temporary = Path.Combine(directory, $".greeter.toml.{Environment.ProcessId}");
        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(temporary, new FileStreamOptions
                   {
                       Mode = FileMode.CreateNew,
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

            if (CheckWithCompositor(temporary) is { } refusal)
            {
                File.Delete(temporary);
                return Fail(refusal);
            }

            File.Move(temporary, GreeterConfigFile.GreeterPath, overwrite: true);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            return Fail($"could not write {GreeterConfigFile.GreeterPath}: {ex.Message}");
        }
    }

    private static int Reset(bool check)
    {
        if (check)
        {
            Console.WriteLine($"would remove {GreeterConfigFile.GreeterPath}");
            return 0;
        }

        try
        {
            File.Delete(GreeterConfigFile.GreeterPath);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"could not remove {GreeterConfigFile.GreeterPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Asks <c>wlrix-compositor --check-config</c> whether it would accept the file, which is the
    /// compositor's own parser and so the only real authority. With <c>deny_unknown_fields</c> a
    /// rejected file is not one wrong setting: it is the login screen on built-in defaults.
    /// Null means accepted.
    /// </summary>
    private static string? CheckWithCompositor(string path)
    {
        var info = new ProcessStartInfo("wlrix-compositor")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("--check-config");
        info.ArgumentList.Add(path);
        info.Environment["PATH"] = SafePath;
        info.Environment["LC_ALL"] = "C";

        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return "could not start wlrix-compositor to check the file";

            var error = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(CheckTimeout))
            {
                process.Kill();
                return "wlrix-compositor did not answer whether it would accept the file";
            }

            var message = error.Result.Trim();
            if (process.ExitCode == 0)
            {
                // Accepted, perhaps with a warning (a scheme this build does not ship). Pass it on.
                if (message.Length > 0)
                    Console.Error.WriteLine(message);
                return null;
            }

            return $"wlrix-compositor would not accept it: {message}";
        }
        catch (Win32Exception ex)
        {
            return $"could not run wlrix-compositor to check the file: {ex.Message}";
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more to do: the message about the write is the one worth reading.
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
            wlrix-login-helper -- the privileged half of the wlRIX Login Settings panel.

            Usage: wlrix-login-helper [--check] write [options]
                   wlrix-login-helper [--check] reset

              write     set the login screen's settings in /etc/wlrix/greeter.toml
              reset     remove that file, so the login screen uses /etc/wlrix/compositor.toml
              --check   validate and print what would be written, without writing it

            write options, all optional:
              --palette ID            --rules S     --model S     --layout S
              --variant S             --options S   --repeat-delay MS   --repeat-rate N
              --output name=DP-1[,enabled=true][,mode=1920x1080@60][,position=0:0]
                       [,scale=1.0][,transform=normal]     (repeat for each display)

            Normally started by Login Settings through pkexec. Prints nothing on success, and
            one line on stderr when it refuses.
            """);
    }
}
