using System.Numerics;

namespace Egami.Drawing;

/// <content>Ready-made shapes.</content>
public sealed partial class Path
{
    /// <summary>Creates a rectangle, clockwise from its top-left corner.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <returns>The path.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="shapes"/>
    /// </example>
    public static Path Rect(RectF rect) => new PathBuilder().AddRect(rect).ToPath();

    /// <summary>Creates a round rectangle, clockwise.</summary>
    /// <param name="roundRect">The round rectangle.</param>
    /// <returns>The path.</returns>
    public static Path RoundRect(RoundRect roundRect) => new PathBuilder().AddRoundRect(roundRect).ToPath();

    /// <summary>Creates the ellipse that fills a rectangle, clockwise.</summary>
    /// <param name="bounds">The rectangle.</param>
    /// <returns>The path.</returns>
    public static Path Oval(RectF bounds) => new PathBuilder().AddOval(bounds).ToPath();

    /// <summary>Creates a circle, clockwise.</summary>
    /// <param name="center">The center.</param>
    /// <param name="radius">The radius.</param>
    /// <returns>The path.</returns>
    public static Path Circle(Vector2 center, float radius) => new PathBuilder().AddCircle(center, radius).ToPath();

    /// <summary>Creates the open arc of an ellipse; 0 degrees is the right end of the horizontal axis and positive angles turn clockwise.</summary>
    /// <param name="oval">The rectangle the ellipse fills.</param>
    /// <param name="startDegrees">Where the arc starts.</param>
    /// <param name="sweepDegrees">How far it turns, at most a full turn either way.</param>
    /// <returns>The path.</returns>
    public static Path Arc(RectF oval, float startDegrees, float sweepDegrees) => new PathBuilder().AddArc(oval, startDegrees, sweepDegrees).ToPath();

    /// <summary>Creates a pie slice: the arc of an ellipse closed through its center.</summary>
    /// <param name="oval">The rectangle the ellipse fills.</param>
    /// <param name="startDegrees">Where the arc starts.</param>
    /// <param name="sweepDegrees">How far it turns.</param>
    /// <returns>The path.</returns>
    public static Path Pie(RectF oval, float startDegrees, float sweepDegrees)
    {
        PathBuilder builder = new PathBuilder().AddArc(oval, startDegrees, sweepDegrees);
        Path arc = builder.ToPath();
        PathBuilder pie = new PathBuilder().MoveTo(oval.Center);
        ReadOnlySpan<Vector2> points = arc.Points;
        pie.LineTo(points[0]);
        for (int i = 1; i + 2 < points.Length; i += 3)
        {
            pie.CubicTo(points[i], points[i + 1], points[i + 2]);
        }

        return pie.Close().ToPath();
    }

    /// <summary>
    /// Creates the ring between two round rectangles: <paramref name="outer"/> clockwise and <paramref name="inner"/> counterclockwise,
    /// with <see cref="FillRule.EvenOdd"/>, which is what Skia's drawDRRect draws and what PDF writes directly. The inner shape
    /// should lie inside the outer one.
    /// </summary>
    /// <param name="outer">The outer edge.</param>
    /// <param name="inner">The inner edge: the hole.</param>
    /// <returns>The path.</returns>
    public static Path Ring(RoundRect outer, RoundRect inner)
        => new PathBuilder().AddRoundRect(outer).AddRoundRect(inner, PathDirection.CounterClockwise).ToPath(FillRule.EvenOdd);

    /// <summary>Creates a polygon through the points.</summary>
    /// <param name="points">The corners, in order.</param>
    /// <param name="close">Whether the last point joins the first.</param>
    /// <returns>The path.</returns>
    public static Path Polygon(ReadOnlySpan<Vector2> points, bool close = true) => new PathBuilder().AddPolygon(points, close).ToPath();

    /// <summary>Creates a straight line, for stroking.</summary>
    /// <param name="start">Where it starts.</param>
    /// <param name="end">Where it ends.</param>
    /// <returns>The path.</returns>
    public static Path Line(Vector2 start, Vector2 end) => new PathBuilder().MoveTo(start).LineTo(end).ToPath();

    /// <summary>Creates a regular polygon with its first corner straight up from the center, turned clockwise by <paramref name="rotationDegrees"/>.</summary>
    /// <param name="center">The center.</param>
    /// <param name="radius">The distance from the center to each corner.</param>
    /// <param name="sides">The number of sides, at least 3.</param>
    /// <param name="rotationDegrees">The clockwise turn.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sides"/> is below 3.</exception>
    public static Path RegularPolygon(Vector2 center, float radius, int sides, float rotationDegrees = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sides, 3);
        Vector2[] points = new Vector2[sides];
        for (int i = 0; i < sides; i++)
        {
            points[i] = OnCircle(center, radius, rotationDegrees + (360.0 * i / sides));
        }

        return Polygon(points);
    }

    /// <summary>
    /// Creates a star whose points lie on <paramref name="outerRadius"/> and whose inner corners lie on <paramref name="innerRadius"/>,
    /// the first point straight up, turned clockwise by <paramref name="rotationDegrees"/>.
    /// </summary>
    /// <param name="center">The center.</param>
    /// <param name="outerRadius">The distance to each point.</param>
    /// <param name="innerRadius">The distance to each inner corner.</param>
    /// <param name="points">The number of points, at least 2.</param>
    /// <param name="rotationDegrees">The clockwise turn.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="points"/> is below 2.</exception>
    public static Path Star(Vector2 center, float outerRadius, float innerRadius, int points, float rotationDegrees = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(points, 2);
        Vector2[] corners = new Vector2[2 * points];
        for (int i = 0; i < corners.Length; i++)
        {
            corners[i] = OnCircle(center, (i & 1) == 0 ? outerRadius : innerRadius, rotationDegrees + (180.0 * i / points));
        }

        return Polygon(corners);
    }

    private static Vector2 OnCircle(Vector2 center, float radius, double degreesFromTop)
    {
        (double sin, double cos) = Math.SinCos((degreesFromTop - 90) * Math.PI / 180);
        return center + new Vector2((float)(radius * cos), (float)(radius * sin));
    }
}
