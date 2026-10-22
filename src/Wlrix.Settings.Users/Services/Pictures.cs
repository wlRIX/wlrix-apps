using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Wlrix.Settings.Users.Services;

/// <summary>
/// Account pictures: the stock ones offered, turning any picture into a square one, and where an
/// account's picture is put so everything that shows it finds it.
/// </summary>
/// <remarks>
/// AccountsService keeps one copy per account in <c>/var/lib/AccountsService/icons</c>, which
/// <c>SetIconFile</c> writes. <c>wlrix-greeter</c> and <c>wlrix-lock</c> look in the home folder
/// first, though -- <c>~/.face.icon</c>, then <c>~/.face</c>, the files KDE writes -- so for the
/// panel's own account those are written too, or an old <c>~/.face</c> would go on being shown
/// at login. Another account's home is not this process's to write.
/// </remarks>
internal static class Pictures
{
    /// <summary>The size a chosen picture is stored at: square, and plenty for any login screen.</summary>
    public const int Size = 256;

    /// <summary>
    /// The stock pictures: <c>wlrix/avatars</c> on the XDG data path, the user's own folder first,
    /// each name once.
    /// </summary>
    public static IReadOnlyList<string> Stock()
    {
        var dirs = new List<string>();
        var home = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } dataHome
            ? dataHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        dirs.Add(home);
        var system = Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } dataDirs
            ? dataDirs
            : "/usr/local/share:/usr/share";
        dirs.AddRange(system.Split(':', StringSplitOptions.RemoveEmptyEntries));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<string>();
        foreach (var dir in dirs.Select(dir => Path.Combine(dir, "wlrix", "avatars")).Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.png").Order(StringComparer.Ordinal))
            {
                if (seen.Add(Path.GetFileName(file)))
                    found.Add(file);
            }
        }
        return found;
    }

    /// <summary>
    /// The largest centered square of an image of this size: what a picture is cropped to, so a
    /// portrait photo keeps its face and loses its top and bottom.
    /// </summary>
    public static PixelRect CenterSquare(int width, int height)
    {
        var side = Math.Min(width, height);
        return new PixelRect((width - side) / 2, (height - side) / 2, side, side);
    }

    /// <summary>
    /// Crop <paramref name="file"/> to its center square at <see cref="Size"/> pixels and save it as
    /// a PNG of its own, which is returned. Throws if the file is not a picture.
    /// </summary>
    public static string MakeSquare(string file)
    {
        using var source = new Bitmap(file);
        var square = CenterSquare(source.PixelSize.Width, source.PixelSize.Height);
        using var target = new RenderTargetBitmap(new PixelSize(Size, Size));
        using (var context = target.CreateDrawingContext())
        {
            context.DrawImage(
                source,
                new Rect(square.X, square.Y, square.Width, square.Height),
                new Rect(0, 0, Size, Size));
        }
        var output = Path.Combine(Path.GetTempPath(), $"wlrix-account-picture-{Environment.ProcessId}-{Guid.NewGuid():N}.png");
        target.Save(output, new PngBitmapEncoderOptions());
        return output;
    }

    /// <summary>Write the panel user's own <c>~/.face.icon</c> and <c>~/.face</c>; see the remarks.</summary>
    public static void WriteOwnFace(string squarePng)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var name in new[] { ".face.icon", ".face" })
            File.Copy(squarePng, Path.Combine(home, name), overwrite: true);
    }

    /// <summary>Remove the panel user's own face files, so initials show everywhere.</summary>
    public static void RemoveOwnFace()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var name in new[] { ".face.icon", ".face" })
        {
            var path = Path.Combine(home, name);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// A color for an account's initials, the same every time for the same login name: the
    /// palette's accent colors, picked by a stable hash rather than `GetHashCode`, which changes
    /// between runs.
    /// </summary>
    public static Color InitialsColor(string userName)
    {
        Color[] colors =
        [
            Color.FromRgb(0x4a, 0x6f, 0xb5), Color.FromRgb(0x2e, 0x8b, 0x6e), Color.FromRgb(0xa8, 0x5a, 0x32),
            Color.FromRgb(0x7c, 0x4f, 0xa0), Color.FromRgb(0xb0, 0x3a, 0x48), Color.FromRgb(0x3f, 0x86, 0x9c),
            Color.FromRgb(0x8a, 0x7a, 0x2a), Color.FromRgb(0x5c, 0x6b, 0x7a),
        ];
        var hash = 2166136261u;
        foreach (var c in userName)
            hash = (hash ^ c) * 16777619u;
        return colors[hash % (uint)colors.Length];
    }
}
