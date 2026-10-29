// Written by tools/sysdrawing-path-builders.py; each builder forwards to GdiShapes and GraphicsPath.Append.
using System.Numerics;

using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing.Drawing2D;

using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

/// <summary>The Add builders.</summary>
public sealed partial class GraphicsPath
{
    /// <summary>Adds a line, joined to the open figure.</summary>
    /// <param name="x1">The start's x.</param>
    /// <param name="y1">The start's y.</param>
    /// <param name="x2">The end's x.</param>
    /// <param name="y2">The end's y.</param>
    public void AddLine(float x1, float y1, float x2, float y2)
    {
        GdiShapes s = new();
        s.Start(new Vector2(x1, y1));
        s.Line(new Vector2(x2, y2));
        Append(s, closed: false);
    }

    /// <summary>Adds a line, joined to the open figure.</summary>
    /// <param name="x1">The start's x.</param>
    /// <param name="y1">The start's y.</param>
    /// <param name="x2">The end's x.</param>
    /// <param name="y2">The end's y.</param>
    public void AddLine(int x1, int y1, int x2, int y2) => AddLine((float)x1, y1, x2, y2);

    /// <summary>Adds a line, joined to the open figure.</summary>
    /// <param name="pt1">The start.</param>
    /// <param name="pt2">The end.</param>
    public void AddLine(PointF pt1, PointF pt2) => AddLine((float)pt1.X, pt1.Y, pt2.X, pt2.Y);

    /// <summary>Adds a line, joined to the open figure.</summary>
    /// <param name="pt1">The start.</param>
    /// <param name="pt2">The end.</param>
    public void AddLine(Point pt1, Point pt2) => AddLine((float)pt1.X, pt1.Y, pt2.X, pt2.Y);

    /// <summary>Adds connected lines, joined to the open figure.</summary>
    /// <param name="points">The points.</param>
    public void AddLines(params PointF[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        AddLines(points.AsSpan());
    }

    /// <summary>Adds connected lines, joined to the open figure.</summary>
    /// <param name="points">The points.</param>
    public void AddLines(params ReadOnlySpan<PointF> points)
    {
        Vector2[] p = V(points);
        GdiShapes s = new();
        if (p.Length == 0)
        {
            throw GdiErrors.InvalidParameter();
        }

        s.Lines(p, close: false);
        Append(s, closed: false);
    }

    /// <summary>Adds connected lines, joined to the open figure.</summary>
    /// <param name="points">The points.</param>
    public void AddLines(params Point[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        AddLines(points.AsSpan());
    }

    /// <summary>Adds connected lines, joined to the open figure.</summary>
    /// <param name="points">The points.</param>
    public void AddLines(params ReadOnlySpan<Point> points)
    {
        Vector2[] p = V(points);
        GdiShapes s = new();
        if (p.Length == 0)
        {
            throw GdiErrors.InvalidParameter();
        }

        s.Lines(p, close: false);
        Append(s, closed: false);
    }

    /// <summary>Adds a closed polygon as a figure of its own; a last point equal to the first is dropped.</summary>
    /// <param name="points">The points.</param>
    public void AddPolygon(params PointF[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        AddPolygon(points.AsSpan());
    }

    /// <summary>Adds a closed polygon as a figure of its own; a last point equal to the first is dropped.</summary>
    /// <param name="points">The points.</param>
    public void AddPolygon(params ReadOnlySpan<PointF> points)
    {
        Vector2[] p = V(points);
        GdiShapes s = new();
        if (p.Length > 1 && p[^1] == p[0])
        {
            p = p[..^1];
        }

        if (p.Length < 3)
        {
            throw GdiErrors.InvalidParameter();
        }

        s.Lines(p, close: true);
        Append(s, closed: true);
    }

    /// <summary>Adds a closed polygon as a figure of its own; a last point equal to the first is dropped.</summary>
    /// <param name="points">The points.</param>
    public void AddPolygon(params Point[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        AddPolygon(points.AsSpan());
    }

    /// <summary>Adds a closed polygon as a figure of its own; a last point equal to the first is dropped.</summary>
    /// <param name="points">The points.</param>
    public void AddPolygon(params ReadOnlySpan<Point> points)
    {
        Vector2[] p = V(points);
        GdiShapes s = new();
        if (p.Length > 1 && p[^1] == p[0])
        {
            p = p[..^1];
        }

        if (p.Length < 3)
        {
            throw GdiErrors.InvalidParameter();
        }

        s.Lines(p, close: true);
        Append(s, closed: true);
    }

    /// <summary>Adds joined cubic Beziers, joined to the open figure: a start point, then three points each.</summary>
    /// <param name="points">The points.</param>
    public void AddBeziers(params PointF[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        AddBeziers(points.AsSpan());
    }

    /// <summary>Adds joined cubic Beziers, joined to the open figure: a start point, then three points each.</summary>
    /// <param name="points">The points.</param>
    public void AddBeziers(params ReadOnlySpan<PointF> points)
    {
        Vector2[] p = V(points);
        GdiShapes s = new();
        s.Beziers(p);
        Append(s, closed: false);
    }

    /// <summary>Adds joined cubic Beziers, joined to the open figure: a start point, then three points each.</summary>
    /// <param name="points">The points.</param>
    public void AddBeziers(params Point[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        AddBeziers(points.AsSpan());
    }

    /// <summary>Adds joined cubic Beziers, joined to the open figure: a start point, then three points each.</summary>
    /// <param name="points">The points.</param>
    public void AddBeziers(params ReadOnlySpan<Point> points)
    {
        Vector2[] p = V(points);
        GdiShapes s = new();
        s.Beziers(p);
        Append(s, closed: false);
    }

    /// <summary>Adds a cubic Bezier, joined to the open figure.</summary>
    /// <param name="x1">The start's x.</param>
    /// <param name="y1">The start's y.</param>
    /// <param name="x2">The first control point's x.</param>
    /// <param name="y2">The first control point's y.</param>
    /// <param name="x3">The second control point's x.</param>
    /// <param name="y3">The second control point's y.</param>
    /// <param name="x4">The end's x.</param>
    /// <param name="y4">The end's y.</param>
    public void AddBezier(float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4)
    {
        GdiShapes s = new();
        s.Start(new Vector2(x1, y1));
        s.Bezier(new Vector2(x2, y2), new Vector2(x3, y3), new Vector2(x4, y4));
        Append(s, closed: false);
    }

    /// <summary>Adds a cubic Bezier, joined to the open figure.</summary>
    /// <param name="x1">The start's x.</param>
    /// <param name="y1">The start's y.</param>
    /// <param name="x2">The first control point's x.</param>
    /// <param name="y2">The first control point's y.</param>
    /// <param name="x3">The second control point's x.</param>
    /// <param name="y3">The second control point's y.</param>
    /// <param name="x4">The end's x.</param>
    /// <param name="y4">The end's y.</param>
    public void AddBezier(int x1, int y1, int x2, int y2, int x3, int y3, int x4, int y4) => AddBezier((float)x1, y1, x2, y2, x3, y3, x4, y4);

    /// <summary>Adds a cubic Bezier, joined to the open figure.</summary>
    /// <param name="pt1">The start.</param>
    /// <param name="pt2">The first control point.</param>
    /// <param name="pt3">The second control point.</param>
    /// <param name="pt4">The end.</param>
    public void AddBezier(PointF pt1, PointF pt2, PointF pt3, PointF pt4) => AddBezier((float)pt1.X, pt1.Y, pt2.X, pt2.Y, pt3.X, pt3.Y, pt4.X, pt4.Y);

    /// <summary>Adds a cubic Bezier, joined to the open figure.</summary>
    /// <param name="pt1">The start.</param>
    /// <param name="pt2">The first control point.</param>
    /// <param name="pt3">The second control point.</param>
    /// <param name="pt4">The end.</param>
    public void AddBezier(Point pt1, Point pt2, Point pt3, Point pt4) => AddBezier((float)pt1.X, pt1.Y, pt2.X, pt2.Y, pt3.X, pt3.Y, pt4.X, pt4.Y);

    /// <summary>Adds a cardinal spline through points, tension 0.5, joined to the open figure.</summary>
    /// <param name="points">The points.</param>
    public void AddCurve(params PointF[] points) => AddCurve(points, 0.5f);

    /// <summary>Adds a cardinal spline through points, tension 0.5, joined to the open figure.</summary>
    /// <param name="points">The points.</param>
    public void AddCurve(params ReadOnlySpan<PointF> points) => AddCurve(points, 0.5f);

    /// <summary>Adds a cardinal spline through points, joined to the open figure.</summary>
    /// <param name="points">The points.</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default.</param>
    public void AddCurve(PointF[] points, float tension)
    {
        ArgumentNullException.ThrowIfNull(points);
        AddCurve(points.AsSpan(), tension);
    }

    /// <summary>Adds a cardinal spline through points, joined to the open figure.</summary>
    /// <param name="points">The points.</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default.</param>
    public void AddCurve(ReadOnlySpan<PointF> points, float tension) => AddCurve(V(points), 0, points.Length - 1, tension);

    /// <summary>Adds part of a cardinal spline, joined to the open figure.</summary>
    /// <param name="points">The points.</param>
    /// <param name="offset">The point the part starts at.</param>
    /// <param name="numberOfSegments">The number of segments.</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default.</param>
    public void AddCurve(PointF[] points, int offset, int numberOfSegments, float tension)
    {
        ArgumentNullException.ThrowIfNull(points);
        AddCurve(V(points), offset, numberOfSegments, tension);
    }

    /// <summary>Adds a closed cardinal spline through points, tension 0.5, as a figure of its own.</summary>
    /// <param name="points">The points.</param>
    public void AddClosedCurve(params PointF[] points) => AddClosedCurve(points, 0.5f);

    /// <summary>Adds a closed cardinal spline through points, tension 0.5, as a figure of its own.</summary>
    /// <param name="points">The points.</param>
    public void AddClosedCurve(params ReadOnlySpan<PointF> points) => AddClosedCurve(points, 0.5f);

    /// <summary>Adds a closed cardinal spline through points as a figure of its own.</summary>
    /// <param name="points">The points.</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default.</param>
    public void AddClosedCurve(PointF[] points, float tension)
    {
        ArgumentNullException.ThrowIfNull(points);
        AddClosedCurve(points.AsSpan(), tension);
    }

    /// <summary>Adds a closed cardinal spline through points as a figure of its own.</summary>
    /// <param name="points">The points.</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default.</param>
    public void AddClosedCurve(ReadOnlySpan<PointF> points, float tension)
    {
        GdiShapes s = new();
        s.Curve(V(points), 0, 0, tension, closed: true);
        Append(s, closed: true);
    }

    /// <summary>Adds a cardinal spline through points, tension 0.5, joined to the open figure.</summary>
    /// <param name="points">The points.</param>
    public void AddCurve(params Point[] points) => AddCurve(points, 0.5f);

    /// <summary>Adds a cardinal spline through points, tension 0.5, joined to the open figure.</summary>
    /// <param name="points">The points.</param>
    public void AddCurve(ReadOnlySpan<Point> points) => AddCurve(points, 0.5f);

    /// <summary>Adds a cardinal spline through points, joined to the open figure.</summary>
    /// <param name="points">The points.</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default.</param>
    public void AddCurve(Point[] points, float tension)
    {
        ArgumentNullException.ThrowIfNull(points);
        AddCurve(points.AsSpan(), tension);
    }

    /// <summary>Adds a cardinal spline through points, joined to the open figure.</summary>
    /// <param name="points">The points.</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default.</param>
    public void AddCurve(ReadOnlySpan<Point> points, float tension) => AddCurve(V(points), 0, points.Length - 1, tension);

    /// <summary>Adds part of a cardinal spline, joined to the open figure.</summary>
    /// <param name="points">The points.</param>
    /// <param name="offset">The point the part starts at.</param>
    /// <param name="numberOfSegments">The number of segments.</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default.</param>
    public void AddCurve(Point[] points, int offset, int numberOfSegments, float tension)
    {
        ArgumentNullException.ThrowIfNull(points);
        AddCurve(V(points), offset, numberOfSegments, tension);
    }

    /// <summary>Adds a closed cardinal spline through points, tension 0.5, as a figure of its own.</summary>
    /// <param name="points">The points.</param>
    public void AddClosedCurve(params Point[] points) => AddClosedCurve(points, 0.5f);

    /// <summary>Adds a closed cardinal spline through points, tension 0.5, as a figure of its own.</summary>
    /// <param name="points">The points.</param>
    public void AddClosedCurve(params ReadOnlySpan<Point> points) => AddClosedCurve(points, 0.5f);

    /// <summary>Adds a closed cardinal spline through points as a figure of its own.</summary>
    /// <param name="points">The points.</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default.</param>
    public void AddClosedCurve(Point[] points, float tension)
    {
        ArgumentNullException.ThrowIfNull(points);
        AddClosedCurve(points.AsSpan(), tension);
    }

    /// <summary>Adds a closed cardinal spline through points as a figure of its own.</summary>
    /// <param name="points">The points.</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default.</param>
    public void AddClosedCurve(ReadOnlySpan<Point> points, float tension)
    {
        GdiShapes s = new();
        s.Curve(V(points), 0, 0, tension, closed: true);
        Append(s, closed: true);
    }

    /// <summary>Adds a rectangle as a figure of its own; an empty one adds nothing.</summary>
    /// <param name="rect">The rectangle.</param>
    public void AddRectangle(RectangleF rect)
    {
        if (rect.Width == 0 || rect.Height == 0)
        {
            return;
        }

        GdiShapes s = new();
        s.Rectangle(rect.X, rect.Y, rect.Width, rect.Height);
        Append(s, closed: true);
    }

    /// <summary>Adds a rectangle as a figure of its own; an empty one adds nothing.</summary>
    /// <param name="rect">The rectangle.</param>
    public void AddRectangle(Rectangle rect) => AddRectangle((RectangleF)rect);

    /// <summary>Adds rectangles, each a figure of its own.</summary>
    /// <param name="rects">The rectangles.</param>
    public void AddRectangles(params RectangleF[] rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        AddRectangles(rects.AsSpan());
    }

    /// <summary>Adds rectangles, each a figure of its own.</summary>
    /// <param name="rects">The rectangles.</param>
    public void AddRectangles(params ReadOnlySpan<RectangleF> rects)
    {
        foreach (RectangleF r in rects)
        {
            AddRectangle(r);
        }
    }

    /// <summary>Adds rectangles, each a figure of its own.</summary>
    /// <param name="rects">The rectangles.</param>
    public void AddRectangles(params Rectangle[] rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        AddRectangles(rects.AsSpan());
    }

    /// <summary>Adds rectangles, each a figure of its own.</summary>
    /// <param name="rects">The rectangles.</param>
    public void AddRectangles(params ReadOnlySpan<Rectangle> rects)
    {
        foreach (Rectangle r in rects)
        {
            AddRectangle(r);
        }
    }

    /// <summary>Adds the ellipse in a rectangle as a figure of its own: four quarter Beziers from its right-hand point.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public void AddEllipse(float x, float y, float width, float height)
    {
        GdiShapes s = new();
        s.Ellipse(x, y, width, height);
        Append(s, closed: true);
    }

    /// <summary>Adds the ellipse in a rectangle as a figure of its own.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public void AddEllipse(int x, int y, int width, int height) => AddEllipse((float)x, y, width, height);

    /// <summary>Adds the ellipse in a rectangle as a figure of its own.</summary>
    /// <param name="rect">The rectangle.</param>
    public void AddEllipse(RectangleF rect) => AddEllipse((float)rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>Adds the ellipse in a rectangle as a figure of its own.</summary>
    /// <param name="rect">The rectangle.</param>
    public void AddEllipse(Rectangle rect) => AddEllipse((float)rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>Adds an arc of the ellipse in a rectangle, joined to the open figure.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="startAngle">The start, in degrees clockwise from the x axis.</param>
    /// <param name="sweepAngle">The sweep, in degrees clockwise.</param>
    public void AddArc(float x, float y, float width, float height, float startAngle, float sweepAngle)
    {
        GdiShapes s = new();
        s.Arc(x, y, width, height, startAngle, sweepAngle, start: true);
        Append(s, closed: false);
    }

    /// <summary>Adds an arc of the ellipse in a rectangle, joined to the open figure.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="startAngle">The start, in degrees clockwise from the x axis.</param>
    /// <param name="sweepAngle">The sweep, in degrees clockwise.</param>
    public void AddArc(int x, int y, int width, int height, float startAngle, float sweepAngle) => AddArc((float)x, y, width, height, startAngle, sweepAngle);

    /// <summary>Adds an arc of the ellipse in a rectangle, joined to the open figure.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="startAngle">The start, in degrees clockwise from the x axis.</param>
    /// <param name="sweepAngle">The sweep, in degrees clockwise.</param>
    public void AddArc(RectangleF rect, float startAngle, float sweepAngle) => AddArc((float)rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);

    /// <summary>Adds an arc of the ellipse in a rectangle, joined to the open figure.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="startAngle">The start, in degrees clockwise from the x axis.</param>
    /// <param name="sweepAngle">The sweep, in degrees clockwise.</param>
    public void AddArc(Rectangle rect, float startAngle, float sweepAngle) => AddArc((float)rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);

    /// <summary>Adds a pie as a figure of its own: the center, the arc, and back.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="startAngle">The start, in degrees clockwise from the x axis.</param>
    /// <param name="sweepAngle">The sweep, in degrees clockwise.</param>
    public void AddPie(float x, float y, float width, float height, float startAngle, float sweepAngle)
    {
        GdiShapes s = new();
        s.Pie(x, y, width, height, startAngle, sweepAngle);
        Append(s, closed: true);
    }

    /// <summary>Adds a pie as a figure of its own.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="startAngle">The start, in degrees clockwise from the x axis.</param>
    /// <param name="sweepAngle">The sweep, in degrees clockwise.</param>
    public void AddPie(int x, int y, int width, int height, float startAngle, float sweepAngle) => AddPie((float)x, y, width, height, startAngle, sweepAngle);

    /// <summary>Adds a pie as a figure of its own.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="startAngle">The start, in degrees clockwise from the x axis.</param>
    /// <param name="sweepAngle">The sweep, in degrees clockwise.</param>
    public void AddPie(Rectangle rect, float startAngle, float sweepAngle) => AddPie((float)rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);

    /// <summary>Adds a rectangle with rounded corners as a figure of its own.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="radius">The size of each corner's ellipse; both must be more than zero.</param>
    public void AddRoundedRectangle(RectangleF rect, SizeF radius)
    {
        if (radius.Width <= 0 || radius.Height <= 0)
        {
            throw GdiErrors.InvalidParameter();
        }

        GdiShapes s = new();
        s.RoundedRectangle(rect.X, rect.Y, rect.Width, rect.Height, radius.Width, radius.Height);
        Append(s, closed: true);
    }

    /// <summary>Adds a rectangle with rounded corners as a figure of its own.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="radius">The size of each corner's ellipse; both must be more than zero.</param>
    public void AddRoundedRectangle(Rectangle rect, Size radius) => AddRoundedRectangle((RectangleF)rect, (SizeF)radius);

    private void AddCurve(Vector2[] p, int offset, int numberOfSegments, float tension)
    {
        if (p.Length < 2 || offset < 0 || numberOfSegments < 1 || offset + numberOfSegments > p.Length - 1)
        {
            throw GdiErrors.InvalidParameter();
        }

        GdiShapes s = new();
        s.Curve(p, offset, numberOfSegments, tension, closed: false);
        Append(s, closed: false);
    }
}
