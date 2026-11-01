using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Wlrix.Icons;
using Wlrix.Packages.AppStream;
using Wlrix.Packages.Processes;

namespace Wlrix.SoftwareManager.Services;

/// <summary>The icons of the Software Inventory's rows.</summary>
public interface IProductIcons
{
    /// <summary>The product's icon, or null when it has none that can be drawn.</summary>
    Task<Bitmap?> Get(AppStreamProduct product);
}

/// <summary>
/// Application icons, rendered off the UI thread and kept.
///
/// The icon theme goes first, as the AppStream specification asks, so an installed application
/// looks the way it does everywhere else on the desktop. The catalog's icon cache is for what
/// the theme has no name for, which is everything not installed yet. Rendered at twice the size
/// they are shown, so they stay sharp on a scaled output.
/// </summary>
public sealed class ProductIconCache : IProductIcons
{
    private const int Pixels = 32;

    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _icons = new(StringComparer.Ordinal);
    private readonly IconLoader _loader = new(new XdgIconTheme());
    private readonly JxlIconConverter _jxl;

    public ProductIconCache(IProcessRunner runner) => _jxl = new JxlIconConverter(runner, CacheDirectory());

    public Task<Bitmap?> Get(AppStreamProduct product) =>
        _icons.GetOrAdd($"{product.StockIcon}\n{string.Join('\n', product.IconFiles)}",
            _ => Task.Run(() => RenderAsync(product)));

    private async Task<Bitmap?> RenderAsync(AppStreamProduct product)
    {
        try
        {
            var png = product.StockIcon is { } stock ? _loader.Render(stock, Pixels) : null;

            foreach (var file in product.IconFiles)
            {
                if (png is not null)
                    break;

                if (!File.Exists(file))
                    continue;

                var source = file.EndsWith(".jxl", StringComparison.OrdinalIgnoreCase)
                    ? await _jxl.ToPngAsync(file).ConfigureAwait(false)
                    : file;

                if (source is not null)
                    png = IconLoader.RenderFile(source, Pixels);
            }

            return png is null ? null : new Bitmap(new MemoryStream(png));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string CacheDirectory()
    {
        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (string.IsNullOrEmpty(cache) || !Path.IsPathRooted(cache))
            cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");

        return Path.Combine(cache, "wlrix", "software-manager", "icons");
    }
}
