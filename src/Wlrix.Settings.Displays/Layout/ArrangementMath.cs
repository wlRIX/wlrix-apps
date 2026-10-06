using Wlrix.Settings.Displays.Models;

namespace Wlrix.Settings.Displays.Layout;

/// <summary>A display's place in the layout, in logical pixels.</summary>
public readonly record struct Box(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public Box At(int x, int y) => this with { X = x, Y = y };

    /// <summary>Whether the two share any area. Touching edges do not count.</summary>
    public bool Overlaps(Box other) =>
        X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    /// <summary>Whether the two meet along a stretch of edge, not just at a corner.</summary>
    public bool Touches(Box other)
    {
        var sharedRows = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
        var sharedColumns = Math.Min(Right, other.Right) - Math.Max(X, other.X);
        return ((Right == other.X || other.Right == X) && sharedRows > 0)
               || ((Bottom == other.Y || other.Bottom == Y) && sharedColumns > 0);
    }
}

/// <summary>
/// The rules the arrangement keeps: displays never overlap, every display meets another along
/// an edge, and the layout starts at 0,0. The compositor would accept a layout that broke them,
/// but a gap between screens is somewhere the pointer cannot go and windows get lost, and an
/// overlap shows the same desktop twice.
/// </summary>
internal static class ArrangementMath
{
    /// <summary>
    /// The size a display takes up in the layout: its mode turned by the transform, divided by
    /// the scale, and rounded as the compositor rounds it.
    /// </summary>
    public static (int Width, int Height) LogicalSize(int width, int height, OutputTransform transform, double scale)
    {
        if (IsSideways(transform))
            (width, height) = (height, width);
        return ((int)Math.Round(width / scale), (int)Math.Round(height / scale));
    }

    /// <summary>Whether the transform turns the picture a quarter turn either way.</summary>
    public static bool IsSideways(OutputTransform transform) => ((int)transform & 1) == 1;

    /// <summary>
    /// The nearest place to <paramref name="dropped"/> where it meets one of
    /// <paramref name="others"/> along an edge and overlaps none of them.
    /// </summary>
    /// <param name="align">
    /// How close, in logical pixels, an edge has to come to its neighbor's matching edge to be
    /// pulled into line with it -- tops with tops, bottoms with bottoms, centers with centers.
    /// </param>
    public static Box Snap(Box dropped, IReadOnlyList<Box> others, int align)
    {
        if (others.Count == 0)
            return dropped;

        Box? best = null;
        var bestDistance = long.MaxValue;
        foreach (var candidate in Candidates(dropped, others, align))
        {
            if (others.Any(candidate.Overlaps))
                continue;
            var dx = (long)(candidate.X - dropped.X);
            var dy = (long)(candidate.Y - dropped.Y);
            var distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        // Every side of every display is blocked, which takes a very odd layout. Beside the
        // rightmost display is always free.
        return best ?? dropped.At(others.Max(o => o.Right), others.MinBy(o => o.X)!.Y);
    }

    private static IEnumerable<Box> Candidates(Box box, IReadOnlyList<Box> others, int align)
    {
        foreach (var other in others)
        {
            // Beside it, at the dropped height but never so far up or down that they stop meeting.
            var y = AlongEdge(box.Y, box.Height, other.Y, other.Height, align);
            yield return box.At(other.Right, y);
            yield return box.At(other.X - box.Width, y);

            // Above or below it.
            var x = AlongEdge(box.X, box.Width, other.X, other.Width, align);
            yield return box.At(x, other.Bottom);
            yield return box.At(x, other.Y - box.Height);
        }
    }

    /// <summary>
    /// Where along a neighbor's edge a display of <paramref name="length"/> lands, given that it
    /// was dropped at <paramref name="start"/>: kept where it was if it still meets the
    /// neighbor, pulled into line when it comes close, and slid back until it meets otherwise.
    /// </summary>
    private static int AlongEdge(int start, int length, int otherStart, int otherLength, int align)
    {
        var otherEnd = otherStart + otherLength;
        int[] lines =
        [
            otherStart,                                  // starts together
            otherEnd - length,                           // ends together
            otherStart + (otherLength - length) / 2,     // centered
        ];
        foreach (var line in lines)
        {
            if (Math.Abs(start - line) <= align)
                return line;
        }

        // At least one pixel of shared edge.
        return Math.Clamp(start, otherStart - length + 1, otherEnd - 1);
    }

    /// <summary>
    /// Mend a layout after one display has moved or changed size: that one stays put, and every
    /// other display either stays where it is, if it still fits and still meets the rest, or is
    /// moved to the nearest place where it does. Displays nearest the fixed one are settled
    /// first, so the layout grows outward from it.
    /// </summary>
    public static Box[] Settle(IReadOnlyList<Box> boxes, int fixedIndex, int align)
    {
        var result = boxes.ToArray();
        if (result.Length == 0)
            return result;

        var anchor = result[fixedIndex];
        var order = Enumerable.Range(0, result.Length)
            .Where(i => i != fixedIndex)
            .OrderBy(i => CenterDistance(result[i], anchor))
            .ToList();

        var placed = new List<Box> { anchor };
        foreach (var i in order)
        {
            var box = result[i];
            if (placed.Any(box.Overlaps) || !placed.Any(box.Touches))
                box = Snap(box, placed, align);
            result[i] = box;
            placed.Add(box);
        }

        return Normalize(result);
    }

    /// <summary>Shift everything so the layout's top-left corner is 0,0.</summary>
    public static Box[] Normalize(IReadOnlyList<Box> boxes)
    {
        if (boxes.Count == 0)
            return [];
        var left = boxes.Min(b => b.X);
        var top = boxes.Min(b => b.Y);
        return boxes.Select(b => b.At(b.X - left, b.Y - top)).ToArray();
    }

    private static long CenterDistance(Box a, Box b)
    {
        long dx = (a.X * 2L + a.Width) - (b.X * 2L + b.Width);
        long dy = (a.Y * 2L + a.Height) - (b.Y * 2L + b.Height);
        return dx * dx + dy * dy;
    }
}
