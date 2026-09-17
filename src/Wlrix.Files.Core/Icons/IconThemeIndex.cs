namespace Wlrix.Files.Core.Icons;

/// <summary>How a theme directory's size is interpreted.</summary>
public enum IconSizeType
{
    /// <summary>Exactly <see cref="IconDirectory.Size"/> and nothing else.</summary>
    Fixed,
    /// <summary>Anything between MinSize and MaxSize; the art scales.</summary>
    Scalable,
    /// <summary>Within Threshold of Size. The spec's default.</summary>
    Threshold
}

/// <summary>One subdirectory of an icon theme, as <c>index.theme</c> describes it.</summary>
public sealed class IconDirectory
{
    public required string Path { get; init; }
    public required int Size { get; init; }
    public int Scale { get; init; } = 1;
    public string? Context { get; init; }
    public IconSizeType Type { get; init; } = IconSizeType.Threshold;
    public int MinSize { get; init; }
    public int MaxSize { get; init; }
    public int Threshold { get; init; } = 2;

    /// <summary>Whether this directory serves the requested size exactly.</summary>
    public bool Matches(int size, int scale)
    {
        if (Scale != scale)
            return false;
        return Type switch
        {
            IconSizeType.Fixed => Size == size,
            IconSizeType.Scalable => MinSize <= size && size <= MaxSize,
            _ => Size - Threshold <= size && size <= Size + Threshold
        };
    }

    /// <summary>
    /// How far this directory is from the requested size, for picking the least-bad one when
    /// nothing matches exactly.
    /// </summary>
    /// <remarks>
    /// Compares in device pixels (size × scale) rather than logical ones, which is what makes
    /// a 24@2x directory a better answer for a 48px request than a 32@1x one.
    /// </remarks>
    public int Distance(int size, int scale)
    {
        var wanted = size * scale;
        switch (Type)
        {
            case IconSizeType.Fixed:
                return Math.Abs(Size * Scale - wanted);
            case IconSizeType.Scalable:
                if (wanted < MinSize * Scale)
                    return MinSize * Scale - wanted;
                if (wanted > MaxSize * Scale)
                    return wanted - MaxSize * Scale;
                return 0;
            default:
                if (wanted < (Size - Threshold) * Scale)
                    return (Size - Threshold) * Scale - wanted;
                if (wanted > (Size + Threshold) * Scale)
                    return wanted - (Size + Threshold) * Scale;
                return 0;
        }
    }
}

/// <summary>A parsed <c>index.theme</c>.</summary>
public sealed class IconThemeIndex
{
    public required string Name { get; init; }

    /// <summary>The theme's subdirectories, in the order <c>Directories=</c> lists them.</summary>
    public required IReadOnlyList<IconDirectory> Directories { get; init; }

    /// <summary>Themes to fall back to, in order. Every chain ends at <c>hicolor</c>.</summary>
    public required IReadOnlyList<string> Inherits { get; init; }

    /// <summary>Parses an <c>index.theme</c>'s lines.</summary>
    /// <remarks>
    /// The format is the same INI-ish shape as a desktop entry: an <c>[Icon Theme]</c> group
    /// naming the directories, then one group per directory. Unlisted groups are ignored, and
    /// a directory named in <c>Directories=</c> with no group of its own is skipped rather
    /// than defaulted — without a size there is nothing sensible to assume.
    /// </remarks>
    public static IconThemeIndex Parse(string name, IEnumerable<string> lines)
    {
        var groups = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        Dictionary<string, string>? current = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                var group = line[1..^1];
                if (!groups.TryGetValue(group, out current))
                    groups[group] = current = new Dictionary<string, string>(StringComparer.Ordinal);
                continue;
            }

            if (current is null)
                continue;
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            current.TryAdd(line[..eq].TrimEnd(), line[(eq + 1)..].TrimStart());
        }

        var header = groups.GetValueOrDefault("Icon Theme") ?? [];
        var inherits = Split(header.GetValueOrDefault("Inherits"));

        var directories = new List<IconDirectory>();
        // ScaledDirectories are additional entries for HiDPI; they parse identically and the
        // Scale key inside each group is what distinguishes them.
        foreach (var path in Split(header.GetValueOrDefault("Directories"))
                     .Concat(Split(header.GetValueOrDefault("ScaledDirectories"))))
        {
            if (!groups.TryGetValue(path, out var group) || !TryInt(group, "Size", out var size))
                continue;

            var type = group.GetValueOrDefault("Type") switch
            {
                "Fixed" => IconSizeType.Fixed,
                "Scalable" => IconSizeType.Scalable,
                _ => IconSizeType.Threshold
            };

            directories.Add(new IconDirectory
            {
                Path = path,
                Size = size,
                Scale = TryInt(group, "Scale", out var scale) ? scale : 1,
                Context = group.GetValueOrDefault("Context"),
                Type = type,
                // The spec defaults both bounds to Size, so a Scalable directory that omits
                // them serves exactly one size rather than everything.
                MinSize = TryInt(group, "MinSize", out var min) ? min : size,
                MaxSize = TryInt(group, "MaxSize", out var max) ? max : size,
                Threshold = TryInt(group, "Threshold", out var threshold) ? threshold : 2
            });
        }

        return new IconThemeIndex
        {
            Name = name,
            Directories = directories,
            Inherits = inherits
        };
    }

    private static List<string> Split(string? value) =>
        string.IsNullOrEmpty(value)
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static bool TryInt(Dictionary<string, string> group, string key, out int value)
    {
        value = 0;
        return group.TryGetValue(key, out var text) && int.TryParse(text, out value);
    }
}
