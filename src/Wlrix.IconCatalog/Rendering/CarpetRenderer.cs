// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
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
/// symbol stands on it at the same place in both carpet states, so starting an application
/// turns the carpet under a symbol that stays put.
///
/// <para>
/// How the symbol stands on the carpet depends on how it was drawn. An IRIX drawing is in the
/// carpet's own frame (<see cref="LegacySvg.IsCarpetFrame(string)"/>) and is laid over it 1:1,
/// in register, and tinted with it -- that is the IRIX composition, and what lets a symbol draw
/// on the carpet itself. Anything else, an application's ordinary theme icon, keeps its own
/// colors and is shrunk to <see cref="SymbolScale"/> and stood on the carpet at
/// <see cref="SymbolBaseline"/>.
/// </para>
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

    // Whether a resolved symbol file is in the carpet frame. Renders run on the thread pool,
    // several at once, hence concurrent; the theme's files do not change under a running app
    // often enough to be worth watching.
    private readonly ConcurrentDictionary<string, bool> _registered = new(StringComparer.Ordinal);

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

        // The carpet always takes the tint: it is wlRIX's own artwork, so the IRIX rule applies
        // exactly. The symbol takes it only if it is an IRIX drawing too, below.
        if (carpet is null || IconStatePainter.Apply(carpet, state, colors) is not { } tinted)
            return null;

        // X-WLRIX-Running-Icon replaces Icon while the application is up.
        var names = running && icon.RunningIcon is { } runningIcon
            ? [runningIcon, .. icon.IconNames]
            : icon.IconNames;
        var symbolSize = (int)MathF.Round(size * SymbolScale);
        var (symbol, registered) = Symbol(names, size, symbolSize);

        // An IRIX symbol is tinted with its carpet, as the IRIX icon catalog drew it: a selected
        // keyboard's white body turns yellow along with the carpet under it. Its checkerboard
        // lines up with the carpet's, the two being the same size. An ordinary theme icon keeps
        // the colors it was drawn in, which the tint would only muddy.
        if (registered && symbol is not null)
            symbol = IconStatePainter.Apply(symbol, state, colors);

        return Compose(tinted, symbol, size, registered ? null : symbolSize);
    }

    /// <summary>
    /// The first of <paramref name="names"/> that resolves, rendered for its frame: full size if
    /// it is drawn in register with the carpet, shrunk otherwise.
    /// </summary>
    private (byte[]? Png, bool Registered) Symbol(IReadOnlyList<string> names, int size, int symbolSize)
    {
        foreach (var name in names)
        {
            if (_loader.Theme.Lookup(name, symbolSize) is not { } path)
                continue;
            var registered = _registered.GetOrAdd(path, LegacySvg.IsCarpetFrameFile);
            return (_loader.Render(name, registered ? size : symbolSize), registered);
        }
        return (null, false);
    }

    /// <param name="symbolSize">
    /// The shrunk symbol's size, or null for one in the carpet's frame, drawn over it 1:1.
    /// </param>
    private static byte[]? Compose(byte[] carpet, byte[]? symbol, int size, int? symbolSize)
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
                var destination = symbolSize is { } shrunk
                    ? SKRect.Create((size - shrunk) / 2, MathF.Round(size * SymbolBaseline) - shrunk, shrunk, shrunk)
                    : SKRect.Create(0, 0, size, size);
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
        // The carpets are IRIX exports too, kept byte for byte the ones wlrix-desktop draws.
        return LegacySvg.WithNamespace(reader.ReadToEnd());
    }
}
