using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Wlrix.Packages.Privileged;

/// <summary>
/// One validated request to the privileged helper: which package manager, what to do, and to
/// what.
///
/// The whole point of this type is that it cannot be constructed from unchecked strings.
/// <see cref="TryParse"/> is the only way in, both in the application that builds the request
/// and in the helper that receives it — the helper re-parses its own argv rather than trusting
/// the caller, because "the caller is our own application" stops being true the moment anyone
/// runs the helper by hand, and it is a program that runs as root.
/// </summary>
public sealed partial class HelperRequest
{
    /// <summary>
    /// The package managers the helper will drive. A backend id that is not on this list is
    /// rejected before anything else is looked at, so a new backend cannot reach root by
    /// existing.
    /// </summary>
    private static readonly string[] KnownBackends = ["pacman", "apt", "zypper"];

    /// <summary>
    /// What a package name may look like. This is the intersection of what the three package
    /// managers permit, deliberately: it is narrower than any one of them, and a name it
    /// rejects is a name the user can still install from a terminal. Leading dashes are
    /// excluded by the first character class, which is what stops a "package name" being read
    /// as an option by the program the helper runs.
    /// </summary>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9@._+-]*$")]
    private static partial Regex PackageName { get; }

    /// <summary>A repository alias: the same rule, without the characters none of them allow here.</summary>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex RepositoryAlias { get; }

    /// <summary>The package file extensions the helper will hand to a package manager.</summary>
    private static readonly string[] PackageFileExtensions =
        [".deb", ".rpm", ".zst", ".xz", ".gz", ".bz2"];

    /// <summary>No package name or path is anywhere near this long; anything that is, is not one.</summary>
    private const int MaximumArgumentLength = 512;

    /// <summary>No transaction names this many packages. A longer one is not a transaction.</summary>
    private const int MaximumTargets = 512;

    private HelperRequest(string backend, HelperVerb verb, IReadOnlyList<string> targets)
    {
        Backend = backend;
        Verb = verb;
        Targets = targets;
    }

    /// <summary>Which package manager: <c>pacman</c>, <c>apt</c> or <c>zypper</c>.</summary>
    public string Backend { get; }

    /// <summary>What to do.</summary>
    public HelperVerb Verb { get; }

    /// <summary>Package names, absolute file paths, or a repository alias and URL.</summary>
    public IReadOnlyList<string> Targets { get; }

    /// <summary>The argv this request is spelled as, for handing to the helper.</summary>
    public IReadOnlyList<string> ToArguments()
    {
        var arguments = new List<string> { Verb.ToString().ToLowerInvariant(), Backend };
        arguments.AddRange(Targets);
        return arguments;
    }

    /// <summary>
    /// Parses <c>&lt;verb&gt; &lt;backend&gt; &lt;target…&gt;</c>, or explains why it will not.
    ///
    /// <paramref name="fileMustExist"/> is the one thing that differs between the two callers.
    /// The helper checks it, because it is about to hand the path to a package manager running
    /// as root. The application does not always: it validates a request it is composing, and on
    /// the application's side the file existing is the user's problem to see reported, not a
    /// reason to refuse to compose the request.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string> arguments, bool fileMustExist,
        [NotNullWhen(true)] out HelperRequest? request, [NotNullWhen(false)] out string? error)
    {
        request = null;
        error = null;

        if (arguments.Count < 2)
        {
            error = "expected: <verb> <backend> [target...]";
            return false;
        }

        if (!Enum.TryParse<HelperVerb>(arguments[0], ignoreCase: true, out var verb)
            || !Enum.IsDefined(verb))
        {
            error = $"unknown verb '{Printable(arguments[0])}'";
            return false;
        }

        var backend = arguments[1];
        if (!KnownBackends.Contains(backend, StringComparer.Ordinal))
        {
            error = $"unknown backend '{Printable(backend)}'";
            return false;
        }

        var targets = arguments.Skip(2).ToList();
        if (targets.Count > MaximumTargets)
        {
            error = $"too many targets ({targets.Count})";
            return false;
        }

        if (targets.Any(target => target.Length is 0 or > MaximumArgumentLength))
        {
            error = "a target is empty or implausibly long";
            return false;
        }

        if (!Validate(verb, targets, fileMustExist, out error))
            return false;

        request = new HelperRequest(backend, verb, targets);
        return true;
    }

    private static bool Validate(HelperVerb verb, IReadOnlyList<string> targets,
        bool fileMustExist, [NotNullWhen(false)] out string? error)
    {
        error = null;

        switch (verb)
        {
            case HelperVerb.Refresh or HelperVerb.Upgrade:
                if (targets.Count > 0)
                {
                    error = $"{verb} takes no targets";
                    return false;
                }

                return true;

            case HelperVerb.Install or HelperVerb.Remove:
                return AtLeastOne(targets, out error)
                       && All(targets, PackageName, "package name", out error);

            case HelperVerb.InstallFile:
                if (!AtLeastOne(targets, out error))
                    return false;

                foreach (var target in targets)
                {
                    if (!ValidateFile(target, fileMustExist, out error))
                        return false;
                }

                return true;

            case HelperVerb.RepoAdd:
                if (targets.Count != 2)
                {
                    error = "repoadd takes an alias and a URL";
                    return false;
                }

                return All([targets[0]], RepositoryAlias, "repository alias", out error)
                       && ValidateUrl(targets[1], out error);

            case HelperVerb.RepoRemove or HelperVerb.RepoEnable or HelperVerb.RepoDisable:
                if (targets.Count != 1)
                {
                    error = $"{verb} takes one repository alias";
                    return false;
                }

                return All(targets, RepositoryAlias, "repository alias", out error);

            default:
                error = $"unhandled verb {verb}";
                return false;
        }
    }

    private static bool AtLeastOne(IReadOnlyList<string> targets, [NotNullWhen(false)] out string? error)
    {
        error = targets.Count == 0 ? "expected at least one target" : null;
        return error is null;
    }

    private static bool All(IReadOnlyList<string> targets, Regex pattern, string what,
        [NotNullWhen(false)] out string? error)
    {
        foreach (var target in targets)
        {
            if (!pattern.IsMatch(target))
            {
                error = $"'{Printable(target)}' is not a valid {what}";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool ValidateFile(string path, bool mustExist, [NotNullWhen(false)] out string? error)
    {
        error = null;

        // Absolute only. A relative path would resolve against the helper's working directory,
        // which under pkexec is not the one the user was looking at.
        if (!Path.IsPathRooted(path))
        {
            error = $"'{Printable(path)}' is not an absolute path";
            return false;
        }

        // No traversal, even though the path is absolute: an absolute path containing ".."
        // still names something other than what it appears to.
        if (path.Split('/').Contains(".."))
        {
            error = $"'{Printable(path)}' is not a plain path";
            return false;
        }

        if (!PackageFileExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            error = $"'{Printable(path)}' is not a package file";
            return false;
        }

        if (mustExist && !File.Exists(path))
        {
            error = $"'{Printable(path)}' does not exist";
            return false;
        }

        return true;
    }

    private static bool ValidateUrl(string url, [NotNullWhen(false)] out string? error)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https" or "ftp"))
        {
            error = $"'{Printable(url)}' is not an http, https or ftp URL";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// A rejected argument, made safe to print. It is about to go into a message the user sees,
    /// and it is the argument that was just refused — so control characters, which could rewrite
    /// the rest of the line in a terminal, are stripped rather than echoed.
    /// </summary>
    private static string Printable(string value)
    {
        var clean = new string(value.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length <= 64 ? clean : clean[..64] + "…";
    }
}
