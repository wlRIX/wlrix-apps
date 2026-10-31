using System.Text.RegularExpressions;

namespace Wlrix.Packages.Backends.Parsing;

/// <summary>What a transaction did to one package.</summary>
public enum PackageOperationKind
{
    Install,
    Upgrade,
    Remove,
}

/// <summary>One package a transaction changed.</summary>
/// <param name="Kind">What happened to it.</param>
/// <param name="Name">The package name.</param>
public sealed record PackageOperation(PackageOperationKind Kind, string Name);

/// <summary>
/// Reads a transaction's output for the packages it changed. This is also how the window knows
/// the point of no return has passed: the first of these lines means files are being replaced.
///
/// pacman only. apt reports a needed restart with a file and zypper with an exit code, so neither
/// needs its output read for this, and Stop is not offered on either.
/// </summary>
public static partial class PackageOperations
{
    /// <summary>
    /// <c>upgrading foo...</c> when there is no terminal, which is how the helper runs it, or
    /// <c>( 1/70) upgrading foo</c> when there is.
    /// </summary>
    [GeneratedRegex(
        @"^(?:\(\s*\d+/\d+\)\s+)?(?<action>installing|upgrading|reinstalling|downgrading|removing)\s+(?<name>\S+?)(?:\.\.\.)?(?:\s|$)",
        RegexOptions.ExplicitCapture)]
    private static partial Regex PacmanOperation { get; }

    /// <summary>The operation <paramref name="line"/> reports, or <c>null</c> if it reports none.</summary>
    public static PackageOperation? Read(string backend, string line)
    {
        if (backend != "pacman" || PacmanOperation.Match(line) is not { Success: true } match)
            return null;

        var kind = match.Groups["action"].Value switch
        {
            "installing" => PackageOperationKind.Install,
            "removing" => PackageOperationKind.Remove,
            _ => PackageOperationKind.Upgrade,
        };

        return new PackageOperation(kind, match.Groups["name"].Value);
    }
}
