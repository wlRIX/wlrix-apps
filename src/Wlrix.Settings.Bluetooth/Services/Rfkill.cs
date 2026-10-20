namespace Wlrix.Settings.Bluetooth.Services;

/// <summary>
/// The kernel's radio switches: whether Bluetooth is off at the hardware switch, which nothing in
/// software can undo, so the panel grays out its switch and says why.
/// </summary>
internal static class Rfkill
{
    private const string Root = "/sys/class/rfkill";

    public static bool HardBlocked() => HardBlocked(Root);

    internal static bool HardBlocked(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root).Any(dir =>
                Read(dir, "type") == "bluetooth" && Read(dir, "hard") == "1");
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Read(string dir, string name)
    {
        try
        {
            return File.ReadAllText(Path.Combine(dir, name)).Trim();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}
