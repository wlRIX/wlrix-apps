using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Wlrix.Packages.AppStream;

/// <summary>The AppStream metadata on this system.</summary>
public interface IAppStreamCatalog
{
    /// <summary>
    /// Reads every catalog into an index. Slow enough to belong off the UI thread. Never throws
    /// over a catalog: a package with no metadata is an ordinary package, so whatever cannot be
    /// read only means fewer products.
    /// </summary>
    AppStreamIndex Load();
}

/// <summary>
/// Reads the catalogs out of the directories AppStream defines, without root and without
/// <c>appstreamcli</c>, which has no way to ask by package. The three roots cover the distributions
/// the backends do, and none of this needs to know which one it is on:
///
/// <list type="bullet">
/// <item>Arch's <c>archlinux-appstream-data</c> installs XML under <c>/usr/share/swcatalog</c>.</item>
/// <item>Debian's <c>appstream</c> links YAML into <c>/var/lib/swcatalog</c> after <c>apt update</c>.</item>
/// <item>openSUSE's <c>libzypp-plugin-appdata</c> puts XML under <c>/var/cache/swcatalog</c>.</item>
/// </list>
/// </summary>
public sealed class AppStreamCatalog : IAppStreamCatalog
{
    private static readonly string[] SystemRoots = ["/usr/share/swcatalog", "/var/lib/swcatalog", "/var/cache/swcatalog"];

    // Rows draw their icon at 16 pixels, doubled for a scaled output.
    private const int IconPixels = 32;

    private readonly ILogger<AppStreamCatalog> _logger;
    private readonly IReadOnlyList<string> _roots;
    private readonly string? _language;

    public AppStreamCatalog(ILogger<AppStreamCatalog> logger) : this(logger, SystemRoots, null)
    {
    }

    internal AppStreamCatalog(ILogger<AppStreamCatalog> logger, IReadOnlyList<string> roots, string? language)
    {
        _logger = logger;
        _roots = roots;
        _language = language;
    }

    public AppStreamIndex Load()
    {
        var language = _language ?? CultureInfo.CurrentUICulture.Name;
        var components = new List<AppStreamComponent>();
        var files = 0;

        foreach (var root in _roots)
        {
            foreach (var file in CatalogFiles(root))
            {
                try
                {
                    var catalog = Parse(file, language);
                    files++;
                    components.AddRange(catalog.Components.Select(component => component with
                    {
                        IconFiles = IconCandidates(root, catalog.Origin, component.CachedIcons, IconPixels),
                    }));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException
                                               or InvalidDataException)
                {
                    _logger.ZLogWarning(ex, $"Skipping the AppStream catalog {file}.");
                }
            }
        }

        var index = AppStreamIndex.Build(components);
        _logger.ZLogInformation($"Read {components.Count} AppStream components from {files} catalogs, covering {index.Count} packages.");
        return index;
    }

    private static IEnumerable<string> CatalogFiles(string root)
    {
        foreach (var directory in new[] { "xml", "yaml" })
        {
            var path = Path.Combine(root, directory);
            string[] files;
            try
            {
                files = Directory.Exists(path) ? Directory.GetFiles(path) : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // Sorted, because the first catalog read wins a tie and that should not depend on the
            // file system.
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var file in files.Where(IsCatalogFile))
                yield return file;
        }
    }

    private static bool IsCatalogFile(string path) =>
        path.EndsWith(".xml", StringComparison.Ordinal) || path.EndsWith(".xml.gz", StringComparison.Ordinal)
        || path.EndsWith(".yml", StringComparison.Ordinal) || path.EndsWith(".yml.gz", StringComparison.Ordinal);

    private static ParsedCatalog Parse(string path, string language)
    {
        using var file = File.OpenRead(path);
        using Stream stream = path.EndsWith(".gz", StringComparison.Ordinal)
            ? new GZipStream(file, CompressionMode.Decompress)
            : file;

        if (Path.GetFileName(path).Contains(".xml", StringComparison.Ordinal))
            return CatalogXml.Read(stream, language);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Dep11Yaml.Read(reader, language);
    }

    /// <summary>
    /// Where a component's cached icons should be, smallest that is big enough first, then the
    /// rest from largest down. Icons belong to the root their catalog is in, and nowhere else: the
    /// specification says so, and two roots can hold different files for one origin. An icon with
    /// no stated size could be in the sizes the specification recommends, or directly under the
    /// origin as older catalogs have it.
    /// </summary>
    internal static IReadOnlyList<string> IconCandidates(string root, string origin,
        IReadOnlyList<CachedIcon> icons, int pixels)
    {
        if (!CatalogRules.IsPathSegment(origin))
            return [];

        var directory = Path.Combine(root, "icons", origin);
        var files = new List<string>();

        foreach (var icon in icons
                     .OrderByDescending(icon => icon.Width * icon.Scale >= pixels)
                     .ThenBy(icon => icon.Width * icon.Scale >= pixels ? icon.Width * icon.Scale : -icon.Width * icon.Scale))
        {
            if (icon.Width > 0)
            {
                var size = icon.Scale > 1
                    ? $"{icon.Width}x{icon.Height}@{icon.Scale}"
                    : $"{icon.Width}x{icon.Height}";
                files.Add(Path.Combine(directory, size, icon.FileName));
            }
            else
            {
                files.Add(Path.Combine(directory, "64x64", icon.FileName));
                files.Add(Path.Combine(directory, "128x128", icon.FileName));
                files.Add(Path.Combine(directory, icon.FileName));
            }
        }

        return files;
    }
}
