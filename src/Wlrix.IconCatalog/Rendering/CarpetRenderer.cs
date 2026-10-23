// SPDX-License-Identifier: GPL-3.0-or-later

using ResvgSharp;
using SkiaSharp;
using Wlrix.IconCatalog.Models;
using Wlrix.Icons;

namespace Wlrix.IconCatalog.Rendering;

/// <summary>Which carpet artwork an application stands on.</summary>
public enum CarpetStyle
{
    /// <summary>The desktop's own: every application launcher.</summary>
    Generic,

    /// <summary>wlRIX's control panels.</summary>
    ControlPanel,
}

/// <summary>
/// Draws an icon as the catalog shows it: an application's symbol standing on its magic carpet,
/// or a file's themed icon, in one of IRIX's icon states.
/// </summary>
/// <remarks>
/// The composition is <c>wlrix-desktop</c>'s (<c>ui/paint.rs</c>), so a launcher looks the same
/// in the catalog as on the desktop: the carpet fills the square, lying flat when the
/// application is not running and standing upright when it is, and carries the state tint; the
/// symbol stands on it untinted, at the same place in both carpet states, so starting an
/// application turns the carpet under a symbol that stays put.
///
/// <para>
/// Plain Skia and resvg, with no Avalonia, so it can be tested without a display. Output is PNG
/// bytes, which the window wraps in a bitmap.
/// </para>
/// </remarks>
public sealed class CarpetRenderer
{
    /// <summary>How big the symbol is, as a fraction of the square; the rest is carpet.</summary>
    public const float SymbolScale = 0.56f;

    /// <summary>Where the symbol's bottom sits, as a fraction down the square, in both carpet states.</summary>
    public const float SymbolBaseline = 0.66f;

    private readonly IconLoader _loader;
    private readonly Dictionary<(CarpetStyle, bool Running), string> _carpets = [];

    public CarpetRenderer(IconLoader loader)
    {
        _loader = loader;
        foreach (var style in Enum.GetValues<CarpetStyle>())
        {
            foreach (var running in new[] { false, true })
            {
                if (ReadCarpet(style, running) is { } svg)
                    _carpets[(style, running)] = svg;
            }
        }
    }

    /// <summary>Whether this build carries its own artwork for <paramref name="style"/>.</summary>
    /// <remarks>
    /// A style without both its closed and open drawings under <c>Assets/Carpets</c> stands on
    /// the generic carpet instead.
    /// </remarks>
    public bool HasArtwork(CarpetStyle style) =>
        _carpets.ContainsKey((style, false)) && _carpets.ContainsKey((style, true));

    /// <summary>Renders <paramref name="icon"/> in <paramref name="state"/>, as PNG; null if nothing would draw.</summary>
    public byte[]? Render(CatalogIcon icon, bool running, IconState state, IconStateColors colors, int size)
    {
        if (size <= 0)
            return null;

        if (icon.Entry is null)
        {
            // A file or a folder: its themed icon, tinted the way the desktop tints files.
            return _loader.Render(icon.IconNames, size) is { } plain
                ? IconStatePainter.Apply(plain, state, colors)
                : null;
        }

        var style = icon.IsControlPanel && HasArtwork(CarpetStyle.ControlPanel)
            ? CarpetStyle.ControlPanel
            : CarpetStyle.Generic;
        if (!_carpets.TryGetValue((style, running), out var carpetSvg))
            return null;

        byte[]? carpet;
        try
        {
            carpet = Resvg.RenderToPng(carpetSvg, new ResvgOptions { Width = size, Height = size });
        }
        catch (ResvgSharp.Exceptions.ResvgException)
        {
            return null;
        }

        // The carpet takes the tint, not the symbol: it is wlRIX's own artwork, so the IRIX
        // rule applies exactly, while an application's icon keeps the colors it was drawn in.
        if (carpet is null || IconStatePainter.Apply(carpet, state, colors) is not { } tinted)
            return null;

        // X-WLRIX-Running-Icon replaces Icon while the application is up.
        var names = running && icon.RunningIcon is { } runningIcon
            ? [runningIcon, .. icon.IconNames]
            : icon.IconNames;
        var symbolSize = (int)MathF.Round(size * SymbolScale);
        var symbol = names.Count > 0 ? _loader.Render(names, symbolSize) : null;

        return Compose(tinted, symbol, size, symbolSize);
    }

    private static byte[]? Compose(byte[] carpet, byte[]? symbol, int size, int symbolSize)
    {
        // No symbol at all: the carpet alone still says running or not.
        if (symbol is null)
            return carpet;

        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
            return carpet;
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using (var carpetImage = SKImage.FromEncodedData(carpet))
        {
            if (carpetImage is not null)
                canvas.DrawImage(carpetImage, 0, 0);
        }

        using (var symbolImage = SKImage.FromEncodedData(symbol))
        {
            if (symbolImage is not null)
            {
                var bottom = MathF.Round(size * SymbolBaseline);
                var destination = SKRect.Create((size - symbolSize) / 2, bottom - symbolSize, symbolSize, symbolSize);
                canvas.DrawImage(symbolImage, destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            }
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray() ?? carpet;
    }

    private static string? ReadCarpet(CarpetStyle style, bool running)
    {
        var name = $"Carpets.{(style == CarpetStyle.ControlPanel ? "control" : "generic")}.{(running ? "open" : "closed")}.svg";
        using var stream = typeof(CarpetRenderer).Assembly.GetManifestResourceStream(name);
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream);
        return WithSvgNamespace(reader.ReadToEnd());
    }

    /// <summary>
    /// Declares the SVG namespace on a drawing that does not.
    /// </summary>
    /// <remarks>
    /// The carpets are IRIX-era exports, written against a 2000 working draft that predates the
    /// namespace, and the resvg behind ResvgSharp refuses a root element outside it. Declaring it
    /// here keeps the files byte for byte the ones wlrix-desktop draws.
    /// </remarks>
    internal static string WithSvgNamespace(string svg)
    {
        const string Namespace = "http://www.w3.org/2000/svg";
        if (svg.Contains(Namespace, StringComparison.Ordinal))
            return svg;
        var root = svg.IndexOf("<svg", StringComparison.Ordinal);
        return root < 0 ? svg : svg.Insert(root + "<svg".Length, $" xmlns=\"{Namespace}\"");
    }
}
