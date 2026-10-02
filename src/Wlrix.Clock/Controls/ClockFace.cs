// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Wlrix.Clock.Controls;

/// <summary>
/// The IRIX desktop clock's face: dark gray, twelve light marks, and three beveled hands that
/// cast a shadow down and to the right.
/// </summary>
/// <remarks>
/// <para>
/// Everything is laid out on a 100-unit square centered in the control and scaled to the
/// shorter side, so the face keeps its proportions at any window size; the numbers below were
/// measured off a screenshot of the original, whose face was 100 pixels across. The background
/// fills the whole control, so a window stretched out of square is gray to its edges rather than
/// a face on a strip of something else.
/// </para>
/// <para>
/// The colors are fixed rather than taken from the session's scheme. IRIX's clock was the same
/// dark face whatever the desktop was colored, and a clock face that went pale under a light
/// scheme would lose the light hands against it.
/// </para>
/// </remarks>
public sealed class ClockFace : Control
{
    public static readonly StyledProperty<TimeSpan> TimeProperty =
        AvaloniaProperty.Register<ClockFace, TimeSpan>(nameof(Time));

    static ClockFace() => AffectsRender<ClockFace>(TimeProperty);

    /// <summary>The time of day the hands show.</summary>
    public TimeSpan Time
    {
        get => GetValue(TimeProperty);
        set => SetValue(TimeProperty, value);
    }

    private static readonly IBrush Face = new ImmutableSolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x3D));
    private static readonly IBrush Mark = new ImmutableSolidColorBrush(Color.FromRgb(0xD6, 0xD6, 0xD6));
    private static readonly IBrush HandLight = new ImmutableSolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));
    private static readonly IBrush HandDark = new ImmutableSolidColorBrush(Color.FromRgb(0x96, 0x96, 0x96));
    private static readonly IBrush Shadow = new ImmutableSolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18));

    /// <summary>The face, in its own units.</summary>
    private const double Size = 100;

    /// <summary>How far the marks' centers sit from the middle.</summary>
    private const double MarkRadius = 41;

    /// <summary>How far down and right a hand's shadow falls.</summary>
    private const double ShadowOffset = 1.5;

    private static readonly Hand HourHand = new(Length: 27, Tail: 6, HalfWidth: 2.6, WidestAt: 7);
    private static readonly Hand MinuteHand = new(Length: 44, Tail: 6, HalfWidth: 2.6, WidestAt: 9);
    private const double SecondLength = 44;
    private const double SecondTail = 8;
    private const double SecondWidth = 0.9;

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Face, bounds);

        var scale = Math.Min(bounds.Width, bounds.Height) / Size;
        if (scale <= 0)
            return;

        var transform = Matrix.CreateScale(scale, scale)
                        * Matrix.CreateTranslation(bounds.Width / 2, bounds.Height / 2);
        using (context.PushTransform(transform))
        {
            DrawMarks(context);

            var hands = ClockHands.At(Time);
            var shadow = new Vector(ShadowOffset, ShadowOffset);

            // Every shadow before any hand, so no hand's shadow lands on top of another hand.
            context.DrawGeometry(Shadow, null, HourHand.Outline(hands.Hour, shadow));
            context.DrawGeometry(Shadow, null, MinuteHand.Outline(hands.Minute, shadow));
            DrawSecondHand(context, hands.Second, Shadow, shadow);

            HourHand.Draw(context, hands.Hour);
            MinuteHand.Draw(context, hands.Minute);
            DrawSecondHand(context, hands.Second, HandLight, default);
        }
    }

    /// <summary>
    /// A tall bar at twelve, so the face reads the right way up; squares at three, six and nine;
    /// and small squares turned to face the middle at the other eight hours.
    /// </summary>
    private static void DrawMarks(DrawingContext context)
    {
        for (var hour = 0; hour < 12; hour++)
        {
            var angle = hour * 30.0;
            var (w, h) = hour switch
            {
                0 => (4.0, 8.0),
                3 or 6 or 9 => (4.0, 4.0),
                _ => (2.6, 2.6),
            };

            var rotation = Matrix.CreateTranslation(0, -MarkRadius) * Matrix.CreateRotation(Radians(angle));
            using (context.PushTransform(rotation))
                context.FillRectangle(Mark, new Rect(-w / 2, -h / 2, w, h));
        }
    }

    private static void DrawSecondHand(DrawingContext context, double angle, IBrush brush, Vector offset)
    {
        var from = Rotate(new Point(0, SecondTail), angle) + offset;
        var to = Rotate(new Point(0, -SecondLength), angle) + offset;
        context.DrawLine(new Pen(brush, SecondWidth, lineCap: PenLineCap.Flat), from, to);
    }

    /// <summary>
    /// An hour or minute hand: a long thin diamond from a short tail behind the pivot out to the
    /// point, widest a little way along it.
    /// </summary>
    private sealed record Hand(double Length, double Tail, double HalfWidth, double WidestAt)
    {
        /// <summary>The whole hand, for its shadow.</summary>
        public Geometry Outline(double angle, Vector offset) =>
            Polygon(offset, Tip(angle), Side(angle, 1), TailPoint(angle), Side(angle, -1));

        /// <summary>
        /// The hand itself, split down its length into a lit half and a shaded one. The light
        /// comes from the top left, as it does everywhere on the desktop, so which half is
        /// which turns over as the hand goes round.
        /// </summary>
        public void Draw(DrawingContext context, double angle)
        {
            var tip = Tip(angle);
            var tail = TailPoint(angle);

            // The right-hand side (seen from the pivot looking out) faces the light when its
            // outward direction points up and to the left.
            var right = Rotate(new Point(1, 0), angle);
            var rightLit = right.X + right.Y < 0;

            context.DrawGeometry(rightLit ? HandLight : HandDark, null,
                Polygon(default, tip, Side(angle, 1), tail));
            context.DrawGeometry(rightLit ? HandDark : HandLight, null,
                Polygon(default, tip, tail, Side(angle, -1)));
        }

        private Point Tip(double angle) => Rotate(new Point(0, -Length), angle);
        private Point TailPoint(double angle) => Rotate(new Point(0, Tail), angle);
        private Point Side(double angle, int side) => Rotate(new Point(side * HalfWidth, -WidestAt), angle);
    }

    private static Geometry Polygon(Vector offset, params Point[] points)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(points[0] + offset, true);
        foreach (var point in points.Skip(1))
            ctx.LineTo(point + offset);
        ctx.EndFigure(true);
        return geometry;
    }

    /// <summary><paramref name="point"/> turned clockwise by <paramref name="degrees"/>, about the pivot.</summary>
    private static Point Rotate(Point point, double degrees)
    {
        var (sin, cos) = Math.SinCos(Radians(degrees));
        return new Point(point.X * cos - point.Y * sin, point.X * sin + point.Y * cos);
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180.0;
}
