using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;
using Wlrix.Common;

namespace Wlrix.Files.Core.Mime;

/// <summary>
/// One type as the user changed it. A null half was left as the system has it.
/// </summary>
/// <param name="Type">The type, <c>media/subtype</c>.</param>
/// <param name="Patterns">The filename patterns, replacing the system's, or null.</param>
/// <param name="Description">What the type is called, replacing the system's, or null.</param>
public sealed record UserMimeType(string Type, IReadOnlyList<string>? Patterns, string? Description);

/// <summary>
/// The user's own type definitions: new types, and changes to the system's.
/// </summary>
/// <remarks>
/// shared-mime-info is a compiled database, so nothing edits <c>globs2</c> directly. A user's
/// changes are source packages under <c>$XDG_DATA_HOME/mime/packages</c>, and
/// <c>update-mime-database</c> compiles them into <c>$XDG_DATA_HOME/mime</c>, which every
/// reader consults before the system's. One package per type, named for it, so reverting a
/// type is deleting a file rather than editing one shared document.
///
/// <para>
/// Changing a system type's patterns writes <c>&lt;glob-deleteall/&gt;</c> before the new
/// ones. Without it the system's patterns still count and the user's are only added to them,
/// so removing <c>*.txt</c> from <c>text/plain</c> would do nothing.
/// </para>
/// </remarks>
public sealed class UserMimeTypes(string dataHome)
{
    private static readonly XNamespace Ns = "http://www.freedesktop.org/standards/shared-mime-info";

    /// <summary>The user's, from <c>XDG_DATA_HOME</c>.</summary>
    public static UserMimeTypes ForSession() =>
        new(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data
            ? data
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"));

    /// <summary>The compiled database these packages go into.</summary>
    public string MimeDirectory => Path.Combine(dataHome, "mime");

    /// <summary>Where the packages are.</summary>
    public string PackagesDirectory => Path.Combine(MimeDirectory, "packages");

    /// <summary>The package for one type, whether or not it exists.</summary>
    /// <remarks>
    /// Prefixed, so a package another tool wrote under the plain name is never this one's to
    /// overwrite or delete.
    /// </remarks>
    public string PathFor(string mimeType) =>
        Path.Combine(PackagesDirectory, $"wlrix-{mimeType.Replace('/', '-')}.xml");

    /// <summary>Whether the user has changed or made this type.</summary>
    public bool Has(string mimeType) => File.Exists(PathFor(mimeType));

    /// <summary>Every type with a package here.</summary>
    public IReadOnlyList<UserMimeType> All()
    {
        if (!Directory.Exists(PackagesDirectory))
            return [];
        return [.. Directory.EnumerateFiles(PackagesDirectory, "wlrix-*.xml")
            .Select(ReadFile)
            .OfType<UserMimeType>()
            .OrderBy(static type => type.Type, StringComparer.Ordinal)];
    }

    /// <summary>The user's change to a type, or null if there is none.</summary>
    public UserMimeType? Read(string mimeType) => ReadFile(PathFor(mimeType));

    /// <summary>Writes a type's package, replacing any there was.</summary>
    public void Write(UserMimeType type)
    {
        var path = PathFor(type.Type);
        ApplicationPaths.EnsureDirectory(PackagesDirectory);
        var temporary = path + ".wlrix-new";
        File.WriteAllText(temporary, Render(type));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Deletes a type's package. For a system type this is a revert.</summary>
    /// <returns>False if there was none.</returns>
    public bool Delete(string mimeType)
    {
        var path = PathFor(mimeType);
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    /// <summary>The package document for a type.</summary>
    public static string Render(UserMimeType type)
    {
        var element = new XElement(Ns + "mime-type", new XAttribute("type", type.Type));
        if (type.Description is { Length: > 0 } description)
            element.Add(new XElement(Ns + "comment", description));
        if (type.Patterns is { } patterns)
        {
            element.Add(new XElement(Ns + "glob-deleteall"));
            foreach (var pattern in patterns)
                element.Add(new XElement(Ns + "glob", new XAttribute("pattern", pattern)));
        }

        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement(Ns + "mime-info", element));
        return document.Declaration + Environment.NewLine + document.Root + Environment.NewLine;
    }

    /// <summary>Recompiles the user's database, so the packages take effect.</summary>
    /// <returns>Null on success, otherwise what went wrong, for the status line.</returns>
    public async Task<string?> UpdateDatabaseAsync(CancellationToken cancellationToken = default)
    {
        ApplicationPaths.EnsureDirectory(PackagesDirectory);
        var info = Executables.StartInfoFor("update-mime-database");
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;
        info.ArgumentList.Add(MimeDirectory);

        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return "update-mime-database did not start";
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0 ? null : (await error.ConfigureAwait(false)).Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return ex.Message;
        }
    }

    private static UserMimeType? ReadFile(string path)
    {
        XElement root;
        try
        {
            if (!File.Exists(path))
                return null;
            root = XElement.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }

        if (root.Element(Ns + "mime-type") is not { } element || (string?)element.Attribute("type") is not { Length: > 0 } type)
            return null;

        var description = element.Elements(Ns + "comment")
            .FirstOrDefault(static e => e.Attribute(XNamespace.Xml + "lang") is null)?.Value;
        IReadOnlyList<string>? patterns = element.Element(Ns + "glob-deleteall") is null
            ? null
            : [.. element.Elements(Ns + "glob").Select(static e => (string?)e.Attribute("pattern")).OfType<string>()];
        return new UserMimeType(type, patterns, description);
    }
}
