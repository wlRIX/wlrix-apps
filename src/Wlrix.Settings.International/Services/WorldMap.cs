using Avalonia.Media.Imaging;
using Wlrix.Icons;

namespace Wlrix.Settings.International.Services;

/// <summary>
/// The world map across the top of the window: NASA's Blue Marble, installed by <c>wlrix-assets</c>
/// to <c>wlrix/maps/world-map.jpg</c> on the XDG data path. Equirectangular, so a time zone's latitude
/// and longitude are its place on it.
/// </summary>
internal static class WorldMap
{
    public const string RelativePath = "wlrix/maps/world-map.jpg";

    /// <summary>The map, or null when it is not installed; the window draws a plain panel instead.</summary>
    public static Bitmap? Load()
    {
        foreach (var dir in DataDirs())
        {
            var path = Path.Combine(dir, RelativePath);
            if (!File.Exists(path))
                continue;
            try
            {
                return new Bitmap(path);
            }
            catch (Exception)
            {
                // An unreadable map costs the picture, not the panel.
            }
        }
        return null;
    }

    private static IEnumerable<string> DataDirs()
    {
        yield return Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } dataHome
            ? dataHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var system = Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } dataDirs
            ? dataDirs
            : "/usr/local/share:/usr/share";
        foreach (var dir in system.Split(':', StringSplitOptions.RemoveEmptyEntries))
            yield return dir;
    }
}

/// <summary>
/// The four buttons' pictures. Each is another panel's icon, found in the icon theme by its app
/// id: this panel's own for Country and Language, Keyboard Settings' and the Clock's for the other
/// two. All are IRIX symbols drawn in the carpet frame, so they fill the button the way the
/// originals did.
/// </summary>
internal static class PanelIcons
{
    /// <summary>The size they are rendered at, a little over the button's face so they stay sharp.</summary>
    public const int Size = 96;

    public static (Bitmap? Country, Bitmap? Language, Bitmap? Keyboard, Bitmap? TimeZone) Load()
    {
        var loader = new IconLoader(new XdgIconTheme());
        Bitmap? Render(string name)
        {
            if (loader.Render(name, Size) is not { } png)
                return null;
            using var stream = new MemoryStream(png);
            return new Bitmap(stream);
        }

        var own = Render("com.wlrix.settings.international");
        return (own, own, Render("com.wlrix.settings.keyboard"), Render("com.wlrix.clock"));
    }
}
