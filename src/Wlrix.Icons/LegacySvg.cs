using System.Text.RegularExpressions;
using ResvgSharp;

namespace Wlrix.Icons;

/// <summary>
/// What IRIX-era SVG exports need: the namespace they predate, and recognizing the frame they
/// were drawn in.
/// </summary>
/// <remarks>
/// The IRIX icons were exported against a 2000 working draft of SVG: a 50pt square, a
/// <c>0 0 50 50</c> view box, and everything drawn at <c>scale(0.5)</c> in a 100-unit frame. The
/// magic carpets are drawn in that frame, and so is an IRIX application's symbol, which was laid
/// over its carpet 1:1 rather than shrunk onto it -- it is how the Writer's pencil erases lines
/// written on the carpet's own paper. <c>wlrix-desktop</c>'s <c>icon_theme.rs</c> applies the
/// same rule, kept in step by hand.
/// </remarks>
public static partial class LegacySvg
{
    private const string SvgNamespace = "http://www.w3.org/2000/svg";

    /// <summary>Declares the SVG namespace on a drawing that does not.</summary>
    /// <remarks>
    /// The draft the exports were written against predates the namespace, and the resvg behind
    /// ResvgSharp refuses a root element outside it. Adding it at load keeps the files byte for
    /// byte what the Rust side, which accepts them as they are, draws.
    /// </remarks>
    public static string WithNamespace(string svg)
    {
        if (svg.Contains(SvgNamespace, StringComparison.Ordinal))
            return svg;
        var root = svg.IndexOf("<svg", StringComparison.Ordinal);
        return root < 0 ? svg : svg.Insert(root + "<svg".Length, $" xmlns=\"{SvgNamespace}\"");
    }

    /// <summary>Rasterizes a drawing to a <paramref name="size"/>-pixel square, as PNG; null if it will not parse.</summary>
    /// <remarks>
    /// For artwork an application carries itself rather than finds in a theme -- the Shut Down
    /// System dialog's picture, say -- which is IRIX-era too and needs the same namespace.
    /// </remarks>
    public static byte[]? Render(string svg, int size)
    {
        if (size <= 0)
            return null;
        try
        {
            return Resvg.RenderToPng(WithNamespace(svg), new ResvgOptions { Width = size, Height = size });
        }
        catch (ResvgSharp.Exceptions.ResvgException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a drawing is in the IRIX carpet frame: a 50pt square root over a
    /// <c>0 0 50 50</c> view box.
    /// </summary>
    public static bool IsCarpetFrame(string svg)
    {
        var root = RootElement().Match(svg);
        if (!root.Success)
            return false;
        var attributes = root.Value;
        return Attribute(attributes, "width") == "50pt"
               && Attribute(attributes, "height") == "50pt"
               && Attribute(attributes, "viewBox") is { } viewBox
               && Separators().Split(viewBox.Trim()) is ["0", "0", "50", "50"];
    }

    /// <summary><see cref="IsCarpetFrame(string)"/> for a file; false for anything unreadable or not SVG.</summary>
    public static bool IsCarpetFrameFile(string path)
    {
        if (!path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            // The root element is at the top; a theme icon can be large, and nothing past the
            // first couple of kilobytes decides this.
            using var reader = new StreamReader(path);
            var head = new char[4096];
            var read = reader.ReadBlock(head, 0, head.Length);
            return IsCarpetFrame(new string(head, 0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? Attribute(string element, string name)
    {
        var match = Regex.Match(element, $@"\s{name}\s*=\s*(?:""([^""]*)""|'([^']*)')");
        return match.Success ? (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) : null;
    }

    [GeneratedRegex(@"<svg\b[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex RootElement();

    [GeneratedRegex(@"[\s,]+", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();
}
