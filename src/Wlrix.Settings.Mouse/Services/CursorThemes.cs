// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Wlrix.Settings.Mouse.Services;

/// <summary>An installed cursor theme: its directory name, which is what the config holds, and
/// the name its <c>index.theme</c> gives it, for the list.</summary>
public sealed record CursorTheme(string Name, string Display);

/// <summary>One image out of an XCursor file, as <c>B, G, R, A</c> premultiplied bytes.</summary>
internal sealed record CursorImage(int Width, int Height, int XHot, int YHot, byte[] Bgra);

/// <summary>
/// The cursor themes installed where an XCursor loader looks, and pictures of their pointers.
///
/// "Installed" means the same thing it does to <c>xdg-desktop-portal-wlrix</c>: a directory with
/// a <c>cursors</c> directory of its own. An icon theme of the same name (<c>wlrix</c> is one)
/// is not a cursor theme, and a theme that only inherits its pointers (the stock <c>default</c>)
/// would be offering a name for some other theme's pictures.
/// </summary>
internal static class CursorThemes
{
    /// <summary>
    /// Where an XCursor loader looks for themes, earliest first: the user's data directory, then
    /// <c>~/.icons</c>, then each system data directory.
    /// </summary>
    public static IReadOnlyList<string> SearchPath()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } dh
            ? dh
            : home is null ? null : Path.Combine(home, ".local", "share");
        var dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } dd
            ? dd
            : "/usr/local/share:/usr/share";

        var path = new List<string>();
        if (dataHome is not null)
            path.Add(Path.Combine(dataHome, "icons"));
        if (home is not null)
            path.Add(Path.Combine(home, ".icons"));
        path.AddRange(dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, "icons")));
        return path;
    }

    /// <summary>Every cursor theme under <paramref name="searchPath"/>, by display name. A theme
    /// in two places is listed once, as the earlier one -- the one a loader would find.</summary>
    public static IReadOnlyList<CursorTheme> List(IEnumerable<string> searchPath)
    {
        var found = new Dictionary<string, CursorTheme>(StringComparer.Ordinal);
        foreach (var root in searchPath)
        {
            if (!Directory.Exists(root))
                continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (found.ContainsKey(name) || !Directory.Exists(Path.Combine(dir, "cursors")))
                    continue;
                found[name] = new CursorTheme(name, DisplayName(dir) ?? name);
            }
        }
        return found.Values
            .OrderBy(theme => theme.Display, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The file for <paramref name="shape"/> in <paramref name="theme"/>, trying each name in
    /// turn -- the modern name and its legacy aliases, which many themes still use.
    /// </summary>
    public static string? Find(IEnumerable<string> searchPath, string theme, params string[] shape)
    {
        foreach (var root in searchPath)
        {
            var cursors = Path.Combine(root, theme, "cursors");
            if (!Directory.Exists(cursors))
                continue;
            foreach (var name in shape)
            {
                var file = Path.Combine(cursors, name);
                if (File.Exists(file))
                    return file;
            }
            // The theme was found here; a loader would not look further for it.
            return null;
        }
        return null;
    }

    /// <summary>
    /// The image nearest <paramref name="size"/> in an XCursor file, or null if it is not one.
    ///
    /// The format: a header (<c>Xcur</c>, header length, version, entry count), a table of
    /// entries (type, nominal size, file position), and at each image entry's position a chunk
    /// header -- its length, type, nominal size, version, width, height, hotspot, frame delay --
    /// followed by the pixels as little-endian ARGB words, which are <c>B, G, R, A</c> in memory
    /// and premultiplied. Only the first frame of an animated cursor is taken.
    /// </summary>
    public static CursorImage? Read(ReadOnlySpan<byte> file, int size)
    {
        const uint ImageType = 0xfffd0002;
        if (file.Length < 16 || !file[..4].SequenceEqual("Xcur"u8))
            return null;

        uint U32(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);

        var headerLength = (int)U32(file, 4);
        var entries = U32(file, 12);
        int? best = null;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < entries; i++)
        {
            var at = headerLength + i * 12;
            if (at + 12 > file.Length)
                return null;
            if (U32(file, at) != ImageType)
                continue;
            var distance = Math.Abs((int)U32(file, at + 4) - size);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = (int)U32(file, at + 8);
            }
        }
        if (best is not { } chunk || chunk + 36 > file.Length)
            return null;

        var width = (int)U32(file, chunk + 16);
        var height = (int)U32(file, chunk + 20);
        var pixels = chunk + (int)U32(file, chunk);
        // The format caps both sides at 0x7fff; anything bigger is a corrupt file.
        if (width is <= 0 or > 0x7fff || height is <= 0 or > 0x7fff
            || pixels + width * height * 4 > file.Length)
            return null;

        return new CursorImage(
            width,
            height,
            (int)U32(file, chunk + 24),
            (int)U32(file, chunk + 28),
            file.Slice(pixels, width * height * 4).ToArray());
    }

    /// <summary><c>Name=</c> under <c>[Icon Theme]</c> in the theme's <c>index.theme</c>.</summary>
    private static string? DisplayName(string themeDir)
    {
        try
        {
            var section = string.Empty;
            foreach (var raw in File.ReadLines(Path.Combine(themeDir, "index.theme")))
            {
                var line = raw.Trim();
                if (line.StartsWith('[') && line.EndsWith(']'))
                    section = line[1..^1];
                else if (section == "Icon Theme" && line.StartsWith("Name=", StringComparison.Ordinal))
                    return line["Name=".Length..].Trim() is { Length: > 0 } name ? name : null;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return null;
    }
}
