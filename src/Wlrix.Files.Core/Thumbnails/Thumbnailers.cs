using ResvgSharp;
using SkiaSharp;

namespace Wlrix.Files.Core.Thumbnails;

/// <summary>Renders a thumbnail of one kind of file.</summary>
/// <remarks>
/// One per family of formats, dispatched by MIME type. Video and PDF are the obvious later
/// additions and are the reason this is an interface rather than a switch.
/// </remarks>
public interface IThumbnailer
{
    /// <summary>Whether this can render that type.</summary>
    bool CanHandle(string mimeType);

    /// <summary>
    /// Renders a thumbnail, or null if the file turns out not to be renderable after all.
    /// </summary>
    /// <returns>An encoded PNG without text chunks; the cache adds those.</returns>
    Task<byte[]?> RenderAsync(string localPath, int size, CancellationToken cancellationToken);

    /// <summary>
    /// The same, for a thumbnailer that needs to know which type it was picked for.
    /// </summary>
    /// <remarks>
    /// Defaulted because most do not: Skia and resvg look at the bytes and neither cares what
    /// the file was called. <see cref="ExternalThumbnailer"/> does care — it holds a program
    /// per type and has nothing to dispatch on otherwise — and overriding one method is
    /// cheaper than making every implementation take a parameter it ignores.
    /// </remarks>
    Task<byte[]?> RenderAsync(string localPath, string mimeType, int size, CancellationToken cancellationToken) =>
        RenderAsync(localPath, size, cancellationToken);

    /// <summary>
    /// Whether rendering costs the whole file, so a size limit is worth applying.
    /// </summary>
    /// <remarks>
    /// True for anything that decodes: Skia reads the bytes and resvg parses the document, and
    /// a directory of raw camera files would read gigabytes to fill one screen. False for a
    /// program that seeks — <c>ffmpegthumbnailer</c> takes one frame out of a film and
    /// <c>pdftoppm</c> renders one page, and neither cares whether the file is four megabytes
    /// or forty gigabytes. Applying an image-sized cap to those would mean no video over the
    /// limit ever got a preview, which is most of them.
    /// </remarks>
    bool ReadsWholeFile => true;
}

/// <summary>Thumbnails for the raster formats Skia decodes.</summary>
public sealed class SkiaImageThumbnailer : IThumbnailer
{
    /// <inheritdoc/>
    /// <remarks>
    /// By prefix rather than a list of types. Skia decodes PNG, JPEG, WebP, GIF, BMP and ICO,
    /// and a type it cannot decode simply fails to decode — which is already handled, and is
    /// a better answer than a hardcoded list that goes stale when Skia gains a format.
    /// </remarks>
    public bool CanHandle(string mimeType) =>
        mimeType.StartsWith("image/", StringComparison.Ordinal)
        && !mimeType.Contains("svg", StringComparison.Ordinal);

    /// <inheritdoc/>
    public Task<byte[]?> RenderAsync(string localPath, int size, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var codec = SKCodec.Create(localPath);
            if (codec is null)
                return null;

            // Scaled during decode rather than after. A twelve-megapixel photograph decoded at
            // full size to make a 128-pixel square is fifty megabytes of pixels thrown away,
            // and a directory of them at once is what makes a file manager run out of memory.
            var scaled = Fit(codec.Info.Width, codec.Info.Height, size);
            var nearest = codec.GetScaledDimensions((float)scaled.Width / codec.Info.Width);
            using var decoded = SKBitmap.Decode(codec, new SKImageInfo(nearest.Width, nearest.Height));
            if (decoded is null)
                return null;

            cancellationToken.ThrowIfCancellationRequested();
            return Encode(decoded, scaled.Width, scaled.Height);
        }, cancellationToken);

    /// <summary>The largest size fitting in a square, keeping the aspect ratio.</summary>
    /// <remarks>
    /// Never enlarged. A 32-pixel icon blown up to 256 is a blurry lie about what the file
    /// contains, and the spec says a thumbnail may be smaller than the nominal size.
    /// </remarks>
    internal static (int Width, int Height) Fit(int width, int height, int size)
    {
        if (width <= 0 || height <= 0)
            return (0, 0);
        if (width <= size && height <= size)
            return (width, height);

        var scale = Math.Min((double)size / width, (double)size / height);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    internal static byte[]? Encode(SKBitmap source, int width, int height)
    {
        using var surface = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        if (!source.ScalePixels(surface, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)))
            return null;

        using var image = SKImage.FromBitmap(surface);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }
}

/// <summary>Thumbnails for SVG, through the same resvg the icon theme uses.</summary>
/// <remarks>
/// Rasterizing an SVG at the thumbnail size rather than decoding and scaling is the whole
/// point of vector artwork, and it costs nothing extra: resvg is already in the process for
/// the icon theme.
/// </remarks>
public sealed class SvgThumbnailer : IThumbnailer
{
    /// <inheritdoc/>
    public bool CanHandle(string mimeType) =>
        mimeType.Contains("svg", StringComparison.Ordinal);

    /// <inheritdoc/>
    public Task<byte[]?> RenderAsync(string localPath, int size, CancellationToken cancellationToken) =>
        Task.Run<byte[]?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var svg = File.ReadAllText(localPath);
            // Answers encoded PNG bytes, which is exactly what the cache stores, so nothing
            // is decoded and re-encoded on the way through.
            return Resvg.RenderToPng(svg, new ResvgOptions { Width = size, Height = size });
        }, cancellationToken);
}

/// <summary>Sends a file to whichever thumbnailer handles its type.</summary>
public sealed class CompositeThumbnailer(IReadOnlyList<IThumbnailer> thumbnailers) : IThumbnailer
{
    /// <summary>The built-in set: raster images through Skia, SVG through resvg.</summary>
    /// <remarks>
    /// What the tests use, and the fallback for a caller that did not choose. It reads no
    /// files and looks at no installed programs, which is what makes it safe to build in a
    /// static initializer — <see cref="Load"/> does both and must not be.
    /// </remarks>
    public static CompositeThumbnailer Default { get; } =
        new([new SkiaImageThumbnailer(), new SvgThumbnailer()]);

    /// <summary>
    /// The built-in set plus whatever this machine installs a <c>.thumbnailer</c> for.
    /// </summary>
    /// <remarks>
    /// The built-in ones come first, so a system thumbnailer claiming <c>image/png</c> — and
    /// glycin's does — does not send every photograph out to another process when Skia can
    /// decode it in this one. What the external set adds is everything Skia and resvg cannot
    /// do: video, audio cover art, HEIF, JPEG XL, office documents and PDF.
    /// </remarks>
    public static CompositeThumbnailer Load() =>
        new([new SkiaImageThumbnailer(), new SvgThumbnailer(), ExternalThumbnailer.Load()]);

    /// <inheritdoc/>
    public bool CanHandle(string mimeType) => Find(mimeType) is not null;

    /// <summary>Whether the thumbnailer for a type would read the whole file.</summary>
    public bool ReadsWholeFile(string mimeType) => Find(mimeType)?.ReadsWholeFile ?? true;

    /// <inheritdoc/>
    public Task<byte[]?> RenderAsync(string localPath, int size, CancellationToken cancellationToken) =>
        Find(FromExtension(localPath)) is { } thumbnailer
            ? thumbnailer.RenderAsync(localPath, size, cancellationToken)
            : Task.FromResult<byte[]?>(null);

    /// <summary>Renders using the thumbnailer for an explicitly given type.</summary>
    public Task<byte[]?> RenderAsync(string localPath, string mimeType, int size, CancellationToken cancellationToken) =>
        Find(mimeType) is { } thumbnailer
            ? thumbnailer.RenderAsync(localPath, mimeType, size, cancellationToken)
            : Task.FromResult<byte[]?>(null);

    private IThumbnailer? Find(string mimeType) =>
        thumbnailers.FirstOrDefault(thumbnailer => thumbnailer.CanHandle(mimeType));

    /// <summary>A last-resort type guess, for the overload that was given no type.</summary>
    private static string FromExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".svg" or ".svgz" => "image/svg+xml",
            _ => "image/unknown"
        };
}
