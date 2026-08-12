namespace Wlrix.Packages.Privileged;

/// <summary>One program and its arguments, as the helper will run it.</summary>
/// <param name="Program">The executable name, resolved on the <c>PATH</c>.</param>
/// <param name="Arguments">Its arguments, already separated — never a command line.</param>
/// <param name="Environment">Extra environment the program needs, on top of a clean one.</param>
public sealed record HelperCommand(
    string Program,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment)
{
    /// <summary>
    /// How the command reads in the Command pane. For display only — nothing is ever built back
    /// out of this string, which is why no quoting is attempted.
    /// </summary>
    public override string ToString() => $"{Program} {string.Join(' ', Arguments)}".TrimEnd();
}

/// <summary>
/// Turns a validated <see cref="HelperRequest"/> into the package-manager invocation that
/// carries it out.
///
/// Shared between the helper, which runs the result, and the helper's <c>--check</c> mode and
/// the tests, which only print it. That sharing is the point: what <c>--check</c> shows is
/// exactly what would run, not a reconstruction of it.
/// </summary>
public static class HelperCommands
{
    /// <summary>
    /// The command for <paramref name="request"/>, or <c>null</c> if that package manager has
    /// no way to carry it out — pacman and repository changes being the case that matters.
    /// </summary>
    public static HelperCommand? For(HelperRequest request) => request.Backend switch
    {
        "pacman" => Pacman(request),
        "apt" => Apt(request),
        "zypper" => Zypper(request),
        _ => null,
    };

    private static HelperCommand? Pacman(HelperRequest request)
    {
        // --noconfirm because there is nobody at the helper's terminal to answer. The
        // confirmation the user gave is the polkit prompt, and before that, the plan they saw.
        var arguments = request.Verb switch
        {
            HelperVerb.Install => Concat(["-S", "--noconfirm", "--needed", "--"], request.Targets),
            HelperVerb.InstallFile => Concat(["-U", "--noconfirm", "--"], request.Targets),
            // -Rs, so removing a package also removes what only it needed. Without the s, an
            // uninstall leaves the dependency tree behind and "remove" does not mean what the
            // user thinks it means.
            HelperVerb.Remove => Concat(["-Rs", "--noconfirm", "--"], request.Targets),
            HelperVerb.Refresh => ["-Sy", "--noconfirm"],
            HelperVerb.Upgrade => ["-Syu", "--noconfirm"],
            // pacman has no repository commands. See the README: the only way is to edit
            // pacman.conf, and that is not something to do on a user's behalf.
            _ => null,
        };

        return arguments is null ? null : new HelperCommand("pacman", arguments, NoExtraEnvironment);
    }

    private static HelperCommand? Apt(HelperRequest request)
    {
        // DEBIAN_FRONTEND=noninteractive is what stops a maintainer script opening a dialog on
        // a terminal that is not there and waiting for ever. -y answers apt's own prompts.
        var noninteractive = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DEBIAN_FRONTEND"] = "noninteractive",
        };

        return request.Verb switch
        {
            HelperVerb.Install => new HelperCommand("apt-get",
                Concat(["install", "-y", "--"], request.Targets), noninteractive),
            // apt-get install takes a path for a local package and pulls its dependencies from
            // the archive; dpkg -i would install it and leave the dependencies unmet.
            HelperVerb.InstallFile => new HelperCommand("apt-get",
                Concat(["install", "-y", "--"], request.Targets), noninteractive),
            HelperVerb.Remove => new HelperCommand("apt-get",
                Concat(["remove", "-y", "--"], request.Targets), noninteractive),
            HelperVerb.Refresh => new HelperCommand("apt-get", ["update", "-y"], noninteractive),
            // dist-upgrade rather than upgrade: plain upgrade refuses anything that would remove
            // a package, which on a release upgrade means it silently does most of the job.
            HelperVerb.Upgrade => new HelperCommand("apt-get", ["dist-upgrade", "-y"], noninteractive),
            HelperVerb.RepoAdd => new HelperCommand("apt-add-repository",
                ["-y", "--", request.Targets[1]], noninteractive),
            HelperVerb.RepoRemove => new HelperCommand("apt-add-repository",
                ["-y", "--remove", "--", request.Targets[0]], noninteractive),
            // apt has no enable/disable: a source is either in a file or commented out in it.
            _ => null,
        };
    }

    private static HelperCommand? Zypper(HelperRequest request)
    {
        // --non-interactive is zypper's -y, and also makes it decline rather than ask when it
        // hits an unsigned repository.
        string[] common = ["--non-interactive"];

        var arguments = request.Verb switch
        {
            HelperVerb.Install => Concat([.. common, "install", "--"], request.Targets),
            HelperVerb.InstallFile => Concat([.. common, "install", "--allow-unsigned-rpm", "--"],
                request.Targets),
            // --clean-deps, matching pacman's -Rs: remove what only this package needed.
            HelperVerb.Remove => Concat([.. common, "remove", "--clean-deps", "--"], request.Targets),
            HelperVerb.Refresh => [.. common, "refresh"],
            HelperVerb.Upgrade => [.. common, "update", "--type", "package"],
            HelperVerb.RepoAdd => [.. common, "addrepo", "--", request.Targets[1], request.Targets[0]],
            HelperVerb.RepoRemove => [.. common, "removerepo", "--", request.Targets[0]],
            HelperVerb.RepoEnable => [.. common, "modifyrepo", "--enable", "--", request.Targets[0]],
            HelperVerb.RepoDisable => [.. common, "modifyrepo", "--disable", "--", request.Targets[0]],
            _ => null,
        };

        return arguments is null ? null : new HelperCommand("zypper", arguments, NoExtraEnvironment);
    }

    private static readonly Dictionary<string, string> NoExtraEnvironment = new(StringComparer.Ordinal);

    private static string[] Concat(IEnumerable<string> head, IEnumerable<string> tail) =>
        [.. head, .. tail];
}
