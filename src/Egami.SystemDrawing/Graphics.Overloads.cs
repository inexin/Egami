// Written by tools/sysdrawing-graphics-overloads.py; each overload forwards to a core in Graphics.Drawing.cs.
using System.Numerics;

using Egami.Drawing;
using Egami.SystemDrawing.Drawing2D;
using Egami.SystemDrawing.Imaging;
using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing;

using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

/// <summary>The Draw, Fill and DrawImage overloads.</summary>
public sealed partial class Graphics
{
    /// <summary>Strokes an arc of the ellipse in a rectangle.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="startAngle">The start, in degrees clockwise from the x axis.</param>
    /// <param name="sweepAngle">The sweep, in degrees clockwise.</param>
    public void DrawArc(Pen pen, float x, float y, float width, float height, float startAngle, float sweepAngle)
    {
        GdiShapes s = new();
        s.Arc(x, y, width, height, startAngle, sweepAngle, start: true);
        Stroke(pen, s.ToPath());
    }

    /// <summary>Strokes an arc of the ellipse in a rectangle.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="startAngle">The start, in degrees clockwise from the x axis.</param>
    /// <param name="sweepAngle">The sweep, in degrees clockwise.</param>
    public void DrawArc(Pen pen, int x, int y, int width, int height, int startAngle, int sweepAngle) => DrawArc(pen, (float)x, y, width, height, startAngle, sweepAngle);

    /// <summary>Strokes an arc of the ellipse in a rectangle.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="rect">The rectangle.</param>
    /// <param name="startAngle">The start, in degrees clockwise from the x axis.</param>
    /// <param name="sweepAngle">The sweep, in degrees clockwise.</param>
    public void DrawArc(Pen pen, RectangleF rect, float startAngle, float sweepAngle) => DrawArc(pen, (float)rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);

    /// <summary>Strokes an arc of the ellipse in a rectangle.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="rect">The rectangle.</param>
    /// <param name="startAngle">The start, in degrees clockwise from the x axis.</param>
    /// <param name="sweepAngle">The sweep, in degrees clockwise.</param>
    public void DrawArc(Pen pen, Rectangle rect, float startAngle, float sweepAngle) => DrawArc(pen, (float)rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);

    /// <summary>Strokes a pie: an arc of the ellipse in a rectangle, closed through its center.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="startAngle">The start, in degrees clockwise from the x axis.</param>
    /// <param name="sweepAngle">The sweep, in degrees clockwise.</param>
    public void DrawPie(Pen pen, float x, float y, float width, float height, float startAngle, float sweepAngle)
    {
        GdiShapes s = new();
        s.Pie(x, y, width, height, startAngle, sweepAngle);
        Stroke(pen, s.ToPath());
    }

    /// <summary>Strokes a pie: an arc of the ellipse in a rectangle, closed through its center.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="startAngle">The start, in degrees clockwise from the x axis.</param>
    /// <param name="sweepAngle">The sweep, in degrees clockwise.</param>
    public void DrawPie(Pen pen, int x, int y, int width, int height, int startAngle, int sweepAngle) => DrawPie(pen, (float)x, y, width, height, startAngle, sweepAngle);

    /// <summary>Strokes a pie: an arc of the ellipse in a rectangle, closed through its center.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="rect">The rectangle.</param>
    /// <param name="startAngle">The start, in degrees clockwise from the x axis.</param>
    /// <param name="sweepAngle">The sweep, in degrees clockwise.</param>
    public void DrawPie(Pen pen, RectangleF rect, float startAngle, float sweepAngle) => DrawPie(pen, (float)rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);

    /// <summary>Strokes a pie: an arc of the ellipse in a rectangle, closed through its center.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="rect">The rectangle.</param>
    /// <param name="startAngle">The start, in degrees clockwise from the x axis.</param>
    /// <param name="sweepAngle">The sweep, in degrees clockwise.</param>
    public void DrawPie(Pen pen, Rectangle rect, float startAngle, float sweepAngle) => DrawPie(pen, (float)rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);

    /// <summary>Fills a pie: an arc of the ellipse in a rectangle, closed through its center.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="startAngle">The start, in degrees clockwise from the x axis.</param>
    /// <param name="sweepAngle">The sweep, in degrees clockwise.</param>
    public void FillPie(Brush brush, float x, float y, float width, float height, float startAngle, float sweepAngle)
    {
        GdiShapes s = new();
        s.Pie(x, y, width, height, startAngle, sweepAngle);
        Fill(brush, s.ToPath());
    }

    /// <summary>Fills a pie: an arc of the ellipse in a rectangle, closed through its center.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="startAngle">The start, in degrees clockwise from the x axis.</param>
    /// <param name="sweepAngle">The sweep, in degrees clockwise.</param>
    public void FillPie(Brush brush, int x, int y, int width, int height, int startAngle, int sweepAngle) => FillPie(brush, (float)x, y, width, height, startAngle, sweepAngle);

    /// <summary>Fills a pie: an arc of the ellipse in a rectangle, closed through its center.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="rect">The rectangle.</param>
    /// <param name="startAngle">The start, in degrees clockwise from the x axis.</param>
    /// <param name="sweepAngle">The sweep, in degrees clockwise.</param>
    public void FillPie(Brush brush, RectangleF rect, float startAngle, float sweepAngle) => FillPie(brush, (float)rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);

    /// <summary>Fills a pie: an arc of the ellipse in a rectangle, closed through its center.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="rect">The rectangle.</param>
    /// <param name="startAngle">The start, in degrees clockwise from the x axis.</param>
    /// <param name="sweepAngle">The sweep, in degrees clockwise.</param>
    public void FillPie(Brush brush, Rectangle rect, float startAngle, float sweepAngle) => FillPie(brush, (float)rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);

    /// <summary>Strokes the ellipse in a rectangle.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public void DrawEllipse(Pen pen, float x, float y, float width, float height)
    {
        GdiShapes s = new();
        s.Ellipse(x, y, width, height);
        Stroke(pen, s.ToPath());
    }

    /// <summary>Strokes the ellipse in a rectangle.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public void DrawEllipse(Pen pen, int x, int y, int width, int height) => DrawEllipse(pen, (float)x, y, width, height);

    /// <summary>Strokes the ellipse in a rectangle.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="rect">The rectangle.</param>
    public void DrawEllipse(Pen pen, RectangleF rect) => DrawEllipse(pen, (float)rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>Strokes the ellipse in a rectangle.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="rect">The rectangle.</param>
    public void DrawEllipse(Pen pen, Rectangle rect) => DrawEllipse(pen, (float)rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>Fills the ellipse in a rectangle.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public void FillEllipse(Brush brush, float x, float y, float width, float height)
    {
        GdiShapes s = new();
        s.Ellipse(x, y, width, height);
        Fill(brush, s.ToPath());
    }

    /// <summary>Fills the ellipse in a rectangle.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public void FillEllipse(Brush brush, int x, int y, int width, int height) => FillEllipse(brush, (float)x, y, width, height);

    /// <summary>Fills the ellipse in a rectangle.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="rect">The rectangle.</param>
    public void FillEllipse(Brush brush, RectangleF rect) => FillEllipse(brush, (float)rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>Fills the ellipse in a rectangle.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="rect">The rectangle.</param>
    public void FillEllipse(Brush brush, Rectangle rect) => FillEllipse(brush, (float)rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>Strokes a rectangle.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public void DrawRectangle(Pen pen, float x, float y, float width, float height)
    {
        GdiShapes s = new();
        s.Rectangle(x, y, width, height);
        Stroke(pen, s.ToPath());
    }

    /// <summary>Strokes a rectangle.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public void DrawRectangle(Pen pen, int x, int y, int width, int height) => DrawRectangle(pen, (float)x, y, width, height);

    /// <summary>Strokes a rectangle.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="rect">The rectangle.</param>
    public void DrawRectangle(Pen pen, RectangleF rect) => DrawRectangle(pen, (float)rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>Strokes a rectangle.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="rect">The rectangle.</param>
    public void DrawRectangle(Pen pen, Rectangle rect) => DrawRectangle(pen, (float)rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>Fills a rectangle.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public void FillRectangle(Brush brush, float x, float y, float width, float height)
    {
        GdiShapes s = new();
        s.Rectangle(x, y, width, height);
        Fill(brush, s.ToPath());
    }

    /// <summary>Fills a rectangle.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public void FillRectangle(Brush brush, int x, int y, int width, int height) => FillRectangle(brush, (float)x, y, width, height);

    /// <summary>Fills a rectangle.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="rect">The rectangle.</param>
    public void FillRectangle(Brush brush, RectangleF rect) => FillRectangle(brush, (float)rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>Fills a rectangle.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="rect">The rectangle.</param>
    public void FillRectangle(Brush brush, Rectangle rect) => FillRectangle(brush, (float)rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>Strokes rectangles.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="rects">The rectangles.</param>
    public void DrawRectangles(Pen pen, params RectangleF[] rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        DrawRectangles(pen, rects.AsSpan());
    }

    /// <summary>Strokes rectangles.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="rects">The rectangles.</param>
    public void DrawRectangles(Pen pen, params ReadOnlySpan<RectangleF> rects)
    {
        ArgumentNullException.ThrowIfNull(pen);
        if (rects.IsEmpty)
        {
            throw GdiErrors.InvalidParameter();
        }

        GdiShapes s = new();
        foreach (RectangleF r in rects)
        {
            s.Rectangle(r.X, r.Y, r.Width, r.Height);
        }

        Stroke(pen, s.ToPath());
    }

    /// <summary>Strokes rectangles.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="rects">The rectangles.</param>
    public void DrawRectangles(Pen pen, params Rectangle[] rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        DrawRectangles(pen, rects.AsSpan());
    }

    /// <summary>Strokes rectangles.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="rects">The rectangles.</param>
    public void DrawRectangles(Pen pen, params ReadOnlySpan<Rectangle> rects)
    {
        ArgumentNullException.ThrowIfNull(pen);
        if (rects.IsEmpty)
        {
            throw GdiErrors.InvalidParameter();
        }

        GdiShapes s = new();
        foreach (Rectangle r in rects)
        {
            s.Rectangle(r.X, r.Y, r.Width, r.Height);
        }

        Stroke(pen, s.ToPath());
    }

    /// <summary>Fills rectangles.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="rects">The rectangles.</param>
    public void FillRectangles(Brush brush, params RectangleF[] rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        FillRectangles(brush, rects.AsSpan());
    }

    /// <summary>Fills rectangles.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="rects">The rectangles.</param>
    public void FillRectangles(Brush brush, params ReadOnlySpan<RectangleF> rects)
    {
        ArgumentNullException.ThrowIfNull(brush);
        if (rects.IsEmpty)
        {
            throw GdiErrors.InvalidParameter();
        }

        GdiShapes s = new();
        foreach (RectangleF r in rects)
        {
            s.Rectangle(r.X, r.Y, r.Width, r.Height);
        }

        Fill(brush, s.ToPath(FillRule.NonZero));
    }

    /// <summary>Fills rectangles.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="rects">The rectangles.</param>
    public void FillRectangles(Brush brush, params Rectangle[] rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        FillRectangles(brush, rects.AsSpan());
    }

    /// <summary>Fills rectangles.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="rects">The rectangles.</param>
    public void FillRectangles(Brush brush, params ReadOnlySpan<Rectangle> rects)
    {
        ArgumentNullException.ThrowIfNull(brush);
        if (rects.IsEmpty)
        {
            throw GdiErrors.InvalidParameter();
        }

        GdiShapes s = new();
        foreach (Rectangle r in rects)
        {
            s.Rectangle(r.X, r.Y, r.Width, r.Height);
        }

        Fill(brush, s.ToPath(FillRule.NonZero));
    }

    /// <summary>Strokes a rectangle with rounded corners.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="rect">The rectangle.</param>
    /// <param name="radius">The size of each corner's ellipse.</param>
    public void DrawRoundedRectangle(Pen pen, RectangleF rect, SizeF radius)
    {
        GdiShapes s = new();
        s.RoundedRectangle(rect.X, rect.Y, rect.Width, rect.Height, radius.Width, radius.Height);
        Stroke(pen, s.ToPath());
    }

    /// <summary>Strokes a rectangle with rounded corners.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="rect">The rectangle.</param>
    /// <param name="radius">The size of each corner's ellipse.</param>
    public void DrawRoundedRectangle(Pen pen, Rectangle rect, Size radius) => DrawRoundedRectangle(pen, (RectangleF)rect, (SizeF)radius);

    /// <summary>Fills a rectangle with rounded corners.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="rect">The rectangle.</param>
    /// <param name="radius">The size of each corner's ellipse.</param>
    public void FillRoundedRectangle(Brush brush, RectangleF rect, SizeF radius)
    {
        GdiShapes s = new();
        s.RoundedRectangle(rect.X, rect.Y, rect.Width, rect.Height, radius.Width, radius.Height);
        Fill(brush, s.ToPath());
    }

    /// <summary>Fills a rectangle with rounded corners.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="rect">The rectangle.</param>
    /// <param name="radius">The size of each corner's ellipse.</param>
    public void FillRoundedRectangle(Brush brush, Rectangle rect, Size radius) => FillRoundedRectangle(brush, (RectangleF)rect, (SizeF)radius);

    /// <summary>Strokes a line.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="x1">The start's x.</param>
    /// <param name="y1">The start's y.</param>
    /// <param name="x2">The end's x.</param>
    /// <param name="y2">The end's y.</param>
    public void DrawLine(Pen pen, float x1, float y1, float x2, float y2)
    {
        GdiShapes s = new();
        s.Start(new Vector2(x1, y1));
        s.Line(new Vector2(x2, y2));
        Stroke(pen, s.ToPath());
    }

    /// <summary>Strokes a line.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="x1">The start's x.</param>
    /// <param name="y1">The start's y.</param>
    /// <param name="x2">The end's x.</param>
    /// <param name="y2">The end's y.</param>
    public void DrawLine(Pen pen, int x1, int y1, int x2, int y2) => DrawLine(pen, (float)x1, y1, x2, y2);

    /// <summary>Strokes a line.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="pt1">The start.</param>
    /// <param name="pt2">The end.</param>
    public void DrawLine(Pen pen, PointF pt1, PointF pt2) => DrawLine(pen, (float)pt1.X, pt1.Y, pt2.X, pt2.Y);

    /// <summary>Strokes a line.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="pt1">The start.</param>
    /// <param name="pt2">The end.</param>
    public void DrawLine(Pen pen, Point pt1, Point pt2) => DrawLine(pen, (float)pt1.X, pt1.Y, pt2.X, pt2.Y);

    /// <summary>Strokes a cubic Bezier.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="x1">The start's x.</param>
    /// <param name="y1">The start's y.</param>
    /// <param name="x2">The first control point's x.</param>
    /// <param name="y2">The first control point's y.</param>
    /// <param name="x3">The second control point's x.</param>
    /// <param name="y3">The second control point's y.</param>
    /// <param name="x4">The end's x.</param>
    /// <param name="y4">The end's y.</param>
    public void DrawBezier(Pen pen, float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4)
    {
        GdiShapes s = new();
        s.Start(new Vector2(x1, y1));
        s.Bezier(new Vector2(x2, y2), new Vector2(x3, y3), new Vector2(x4, y4));
        Stroke(pen, s.ToPath());
    }

    /// <summary>Strokes a cubic Bezier.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="pt1">The start.</param>
    /// <param name="pt2">The first control point.</param>
    /// <param name="pt3">The second control point.</param>
    /// <param name="pt4">The end.</param>
    public void DrawBezier(Pen pen, PointF pt1, PointF pt2, PointF pt3, PointF pt4) => DrawBezier(pen, (float)pt1.X, pt1.Y, pt2.X, pt2.Y, pt3.X, pt3.Y, pt4.X, pt4.Y);

    /// <summary>Strokes a cubic Bezier.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="pt1">The start.</param>
    /// <param name="pt2">The first control point.</param>
    /// <param name="pt3">The second control point.</param>
    /// <param name="pt4">The end.</param>
    public void DrawBezier(Pen pen, Point pt1, Point pt2, Point pt3, Point pt4) => DrawBezier(pen, (float)pt1.X, pt1.Y, pt2.X, pt2.Y, pt3.X, pt3.Y, pt4.X, pt4.Y);

    /// <summary>Strokes connected lines.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    public void DrawLines(Pen pen, params PointF[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        DrawLines(pen, points.AsSpan());
    }

    /// <summary>Strokes connected lines.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    public void DrawLines(Pen pen, params ReadOnlySpan<PointF> points)
    {
        ArgumentNullException.ThrowIfNull(pen);
        Vector2[] p = Points(points);
        GdiShapes s = new();
        if (p.Length < 2)
        {
            throw GdiErrors.InvalidParameter();
        }

        s.Lines(p, close: false);
        Stroke(pen, s.ToPath());
    }

    /// <summary>Strokes connected lines.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    public void DrawLines(Pen pen, params Point[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        DrawLines(pen, points.AsSpan());
    }

    /// <summary>Strokes connected lines.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    public void DrawLines(Pen pen, params ReadOnlySpan<Point> points)
    {
        ArgumentNullException.ThrowIfNull(pen);
        Vector2[] p = Points(points);
        GdiShapes s = new();
        if (p.Length < 2)
        {
            throw GdiErrors.InvalidParameter();
        }

        s.Lines(p, close: false);
        Stroke(pen, s.ToPath());
    }

    /// <summary>Strokes a polygon.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    public void DrawPolygon(Pen pen, params PointF[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        DrawPolygon(pen, points.AsSpan());
    }

    /// <summary>Strokes a polygon.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    public void DrawPolygon(Pen pen, params ReadOnlySpan<PointF> points)
    {
        ArgumentNullException.ThrowIfNull(pen);
        Vector2[] p = Points(points);
        GdiShapes s = new();
        if (p.Length < 2)
        {
            throw GdiErrors.InvalidParameter();
        }

        s.Lines(p, close: true);
        Stroke(pen, s.ToPath());
    }

    /// <summary>Strokes a polygon.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    public void DrawPolygon(Pen pen, params Point[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        DrawPolygon(pen, points.AsSpan());
    }

    /// <summary>Strokes a polygon.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    public void DrawPolygon(Pen pen, params ReadOnlySpan<Point> points)
    {
        ArgumentNullException.ThrowIfNull(pen);
        Vector2[] p = Points(points);
        GdiShapes s = new();
        if (p.Length < 2)
        {
            throw GdiErrors.InvalidParameter();
        }

        s.Lines(p, close: true);
        Stroke(pen, s.ToPath());
    }

    /// <summary>Strokes joined cubic Beziers: a start point, then three points each.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    public void DrawBeziers(Pen pen, params PointF[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        DrawBeziers(pen, points.AsSpan());
    }

    /// <summary>Strokes joined cubic Beziers: a start point, then three points each.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    public void DrawBeziers(Pen pen, params ReadOnlySpan<PointF> points)
    {
        ArgumentNullException.ThrowIfNull(pen);
        Vector2[] p = Points(points);
        GdiShapes s = new();
        s.Beziers(p);
        Stroke(pen, s.ToPath());
    }

    /// <summary>Strokes joined cubic Beziers: a start point, then three points each.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    public void DrawBeziers(Pen pen, params Point[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        DrawBeziers(pen, points.AsSpan());
    }

    /// <summary>Strokes joined cubic Beziers: a start point, then three points each.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    public void DrawBeziers(Pen pen, params ReadOnlySpan<Point> points)
    {
        ArgumentNullException.ThrowIfNull(pen);
        Vector2[] p = Points(points);
        GdiShapes s = new();
        s.Beziers(p);
        Stroke(pen, s.ToPath());
    }

    /// <summary>Fills a polygon, even-odd.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="points">The points.</param>
    public void FillPolygon(Brush brush, params PointF[] points) => FillPolygon(brush, points, FillMode.Alternate);

    /// <summary>Fills a polygon, even-odd.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="points">The points.</param>
    public void FillPolygon(Brush brush, params ReadOnlySpan<PointF> points) => FillPolygon(brush, points, FillMode.Alternate);

    /// <summary>Fills a polygon.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="points">The points.</param>
    /// <param name="fillMode">Alternate (even-odd) or Winding (nonzero).</param>
    public void FillPolygon(Brush brush, PointF[] points, FillMode fillMode)
    {
        ArgumentNullException.ThrowIfNull(points);
        FillPolygon(brush, points.AsSpan(), fillMode);
    }

    /// <summary>Fills a polygon.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="points">The points.</param>
    /// <param name="fillMode">Alternate (even-odd) or Winding (nonzero).</param>
    public void FillPolygon(Brush brush, ReadOnlySpan<PointF> points, FillMode fillMode)
    {
        ArgumentNullException.ThrowIfNull(brush);
        Vector2[] p = Points(points);
        if (p.Length < 3)
        {
            throw GdiErrors.InvalidParameter();
        }

        GdiShapes s = new();
        s.Lines(p, close: true);
        Fill(brush, s.ToPath(Rule(fillMode)));
    }

    /// <summary>Fills a polygon, even-odd.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="points">The points.</param>
    public void FillPolygon(Brush brush, Point[] points) => FillPolygon(brush, points, FillMode.Alternate);

    /// <summary>Fills a polygon, even-odd.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="points">The points.</param>
    public void FillPolygon(Brush brush, params ReadOnlySpan<Point> points) => FillPolygon(brush, points, FillMode.Alternate);

    /// <summary>Fills a polygon.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="points">The points.</param>
    /// <param name="fillMode">Alternate (even-odd) or Winding (nonzero).</param>
    public void FillPolygon(Brush brush, Point[] points, FillMode fillMode)
    {
        ArgumentNullException.ThrowIfNull(points);
        FillPolygon(brush, points.AsSpan(), fillMode);
    }

    /// <summary>Fills a polygon.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="points">The points.</param>
    /// <param name="fillMode">Alternate (even-odd) or Winding (nonzero).</param>
    public void FillPolygon(Brush brush, ReadOnlySpan<Point> points, FillMode fillMode)
    {
        ArgumentNullException.ThrowIfNull(brush);
        Vector2[] p = Points(points);
        if (p.Length < 3)
        {
            throw GdiErrors.InvalidParameter();
        }

        GdiShapes s = new();
        s.Lines(p, close: true);
        Fill(brush, s.ToPath(Rule(fillMode)));
    }

    /// <summary>Strokes a cardinal spline through points, tension 0.5.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    public void DrawCurve(Pen pen, params PointF[] points) => DrawCurve(pen, points, 0.5f);

    /// <summary>Strokes a cardinal spline through points, tension 0.5.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    public void DrawCurve(Pen pen, params ReadOnlySpan<PointF> points) => DrawCurve(pen, points, 0.5f);

    /// <summary>Strokes a cardinal spline through points.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default, 0 gives straight lines.</param>
    public void DrawCurve(Pen pen, PointF[] points, float tension)
    {
        ArgumentNullException.ThrowIfNull(points);
        DrawCurve(pen, points.AsSpan(), tension);
    }

    /// <summary>Strokes a cardinal spline through points.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default, 0 gives straight lines.</param>
    public void DrawCurve(Pen pen, ReadOnlySpan<PointF> points, float tension) => DrawCurve(pen, points, 0, points.Length - 1, tension);

    /// <summary>Strokes part of a cardinal spline, tension 0.5.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    /// <param name="offset">The point the drawn part starts at.</param>
    /// <param name="numberOfSegments">The number of segments drawn.</param>
    public void DrawCurve(Pen pen, PointF[] points, int offset, int numberOfSegments) => DrawCurve(pen, points, offset, numberOfSegments, 0.5f);

    /// <summary>Strokes part of a cardinal spline, tension 0.5.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    /// <param name="offset">The point the drawn part starts at.</param>
    /// <param name="numberOfSegments">The number of segments drawn.</param>
    public void DrawCurve(Pen pen, ReadOnlySpan<PointF> points, int offset, int numberOfSegments) => DrawCurve(pen, points, offset, numberOfSegments, 0.5f);

    /// <summary>Strokes part of a cardinal spline.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    /// <param name="offset">The point the drawn part starts at.</param>
    /// <param name="numberOfSegments">The number of segments drawn.</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default, 0 gives straight lines.</param>
    public void DrawCurve(Pen pen, PointF[] points, int offset, int numberOfSegments, float tension)
    {
        ArgumentNullException.ThrowIfNull(points);
        DrawCurve(pen, points.AsSpan(), offset, numberOfSegments, tension);
    }

    /// <summary>Strokes part of a cardinal spline.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    /// <param name="offset">The point the drawn part starts at.</param>
    /// <param name="numberOfSegments">The number of segments drawn.</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default, 0 gives straight lines.</param>
    public void DrawCurve(Pen pen, ReadOnlySpan<PointF> points, int offset, int numberOfSegments, float tension)
    {
        ArgumentNullException.ThrowIfNull(pen);
        Vector2[] p = Points(points);
        if (p.Length < 2 || offset < 0 || numberOfSegments < 1 || offset + numberOfSegments > p.Length - 1)
        {
            throw GdiErrors.InvalidParameter();
        }

        GdiShapes s = new();
        s.Curve(p, offset, numberOfSegments, tension, closed: false);
        Stroke(pen, s.ToPath());
    }

    /// <summary>Strokes a closed cardinal spline through points, tension 0.5.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    public void DrawClosedCurve(Pen pen, params PointF[] points) => DrawClosedCurve(pen, points, 0.5f, FillMode.Alternate);

    /// <summary>Strokes a closed cardinal spline through points, tension 0.5.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    public void DrawClosedCurve(Pen pen, params ReadOnlySpan<PointF> points) => DrawClosedCurve(pen, points, 0.5f, FillMode.Alternate);

    /// <summary>Strokes a closed cardinal spline through points.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default, 0 gives straight lines.</param>
    /// <param name="fillmode">Not used by a stroke.</param>
    public void DrawClosedCurve(Pen pen, PointF[] points, float tension, FillMode fillmode)
    {
        ArgumentNullException.ThrowIfNull(points);
        DrawClosedCurve(pen, points.AsSpan(), tension, fillmode);
    }

    /// <summary>Strokes a closed cardinal spline through points.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default, 0 gives straight lines.</param>
    /// <param name="fillmode">Not used by a stroke.</param>
    public void DrawClosedCurve(Pen pen, ReadOnlySpan<PointF> points, float tension, FillMode fillmode)
    {
        ArgumentNullException.ThrowIfNull(pen);
        GdiShapes s = new();
        s.Curve(Points(points), 0, 0, tension, closed: true);
        Stroke(pen, s.ToPath());
    }

    /// <summary>Fills a closed cardinal spline through points, even-odd, tension 0.5.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="points">The points.</param>
    public void FillClosedCurve(Brush brush, params PointF[] points) => FillClosedCurve(brush, points, FillMode.Alternate, 0.5f);

    /// <summary>Fills a closed cardinal spline through points, even-odd, tension 0.5.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="points">The points.</param>
    public void FillClosedCurve(Brush brush, params ReadOnlySpan<PointF> points) => FillClosedCurve(brush, points, FillMode.Alternate, 0.5f);

    /// <summary>Fills a closed cardinal spline through points, tension 0.5.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="points">The points.</param>
    /// <param name="fillmode">Alternate (even-odd) or Winding (nonzero).</param>
    public void FillClosedCurve(Brush brush, PointF[] points, FillMode fillmode) => FillClosedCurve(brush, points, fillmode, 0.5f);

    /// <summary>Fills a closed cardinal spline through points, tension 0.5.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="points">The points.</param>
    /// <param name="fillmode">Alternate (even-odd) or Winding (nonzero).</param>
    public void FillClosedCurve(Brush brush, ReadOnlySpan<PointF> points, FillMode fillmode) => FillClosedCurve(brush, points, fillmode, 0.5f);

    /// <summary>Fills a closed cardinal spline through points.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="points">The points.</param>
    /// <param name="fillmode">Alternate (even-odd) or Winding (nonzero).</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default, 0 gives straight lines.</param>
    public void FillClosedCurve(Brush brush, PointF[] points, FillMode fillmode, float tension)
    {
        ArgumentNullException.ThrowIfNull(points);
        FillClosedCurve(brush, points.AsSpan(), fillmode, tension);
    }

    /// <summary>Fills a closed cardinal spline through points.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="points">The points.</param>
    /// <param name="fillmode">Alternate (even-odd) or Winding (nonzero).</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default, 0 gives straight lines.</param>
    public void FillClosedCurve(Brush brush, ReadOnlySpan<PointF> points, FillMode fillmode, float tension)
    {
        ArgumentNullException.ThrowIfNull(brush);
        GdiShapes s = new();
        s.Curve(Points(points), 0, 0, tension, closed: true);
        Fill(brush, s.ToPath(Rule(fillmode)));
    }

    /// <summary>Strokes a cardinal spline through points, tension 0.5.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    public void DrawCurve(Pen pen, params Point[] points) => DrawCurve(pen, points, 0.5f);

    /// <summary>Strokes a cardinal spline through points, tension 0.5.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    public void DrawCurve(Pen pen, params ReadOnlySpan<Point> points) => DrawCurve(pen, points, 0.5f);

    /// <summary>Strokes a cardinal spline through points.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default, 0 gives straight lines.</param>
    public void DrawCurve(Pen pen, Point[] points, float tension)
    {
        ArgumentNullException.ThrowIfNull(points);
        DrawCurve(pen, points.AsSpan(), tension);
    }

    /// <summary>Strokes a cardinal spline through points.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default, 0 gives straight lines.</param>
    public void DrawCurve(Pen pen, ReadOnlySpan<Point> points, float tension) => DrawCurve(pen, points, 0, points.Length - 1, tension);

    /// <summary>Strokes part of a cardinal spline.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    /// <param name="offset">The point the drawn part starts at.</param>
    /// <param name="numberOfSegments">The number of segments drawn.</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default, 0 gives straight lines.</param>
    public void DrawCurve(Pen pen, Point[] points, int offset, int numberOfSegments, float tension)
    {
        ArgumentNullException.ThrowIfNull(points);
        DrawCurve(pen, points.AsSpan(), offset, numberOfSegments, tension);
    }

    /// <summary>Strokes part of a cardinal spline.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    /// <param name="offset">The point the drawn part starts at.</param>
    /// <param name="numberOfSegments">The number of segments drawn.</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default, 0 gives straight lines.</param>
    public void DrawCurve(Pen pen, ReadOnlySpan<Point> points, int offset, int numberOfSegments, float tension)
    {
        ArgumentNullException.ThrowIfNull(pen);
        Vector2[] p = Points(points);
        if (p.Length < 2 || offset < 0 || numberOfSegments < 1 || offset + numberOfSegments > p.Length - 1)
        {
            throw GdiErrors.InvalidParameter();
        }

        GdiShapes s = new();
        s.Curve(p, offset, numberOfSegments, tension, closed: false);
        Stroke(pen, s.ToPath());
    }

    /// <summary>Strokes a closed cardinal spline through points, tension 0.5.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    public void DrawClosedCurve(Pen pen, params Point[] points) => DrawClosedCurve(pen, points, 0.5f, FillMode.Alternate);

    /// <summary>Strokes a closed cardinal spline through points, tension 0.5.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    public void DrawClosedCurve(Pen pen, params ReadOnlySpan<Point> points) => DrawClosedCurve(pen, points, 0.5f, FillMode.Alternate);

    /// <summary>Strokes a closed cardinal spline through points.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default, 0 gives straight lines.</param>
    /// <param name="fillmode">Not used by a stroke.</param>
    public void DrawClosedCurve(Pen pen, Point[] points, float tension, FillMode fillmode)
    {
        ArgumentNullException.ThrowIfNull(points);
        DrawClosedCurve(pen, points.AsSpan(), tension, fillmode);
    }

    /// <summary>Strokes a closed cardinal spline through points.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="points">The points.</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default, 0 gives straight lines.</param>
    /// <param name="fillmode">Not used by a stroke.</param>
    public void DrawClosedCurve(Pen pen, ReadOnlySpan<Point> points, float tension, FillMode fillmode)
    {
        ArgumentNullException.ThrowIfNull(pen);
        GdiShapes s = new();
        s.Curve(Points(points), 0, 0, tension, closed: true);
        Stroke(pen, s.ToPath());
    }

    /// <summary>Fills a closed cardinal spline through points, even-odd, tension 0.5.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="points">The points.</param>
    public void FillClosedCurve(Brush brush, params Point[] points) => FillClosedCurve(brush, points, FillMode.Alternate, 0.5f);

    /// <summary>Fills a closed cardinal spline through points, even-odd, tension 0.5.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="points">The points.</param>
    public void FillClosedCurve(Brush brush, params ReadOnlySpan<Point> points) => FillClosedCurve(brush, points, FillMode.Alternate, 0.5f);

    /// <summary>Fills a closed cardinal spline through points, tension 0.5.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="points">The points.</param>
    /// <param name="fillmode">Alternate (even-odd) or Winding (nonzero).</param>
    public void FillClosedCurve(Brush brush, Point[] points, FillMode fillmode) => FillClosedCurve(brush, points, fillmode, 0.5f);

    /// <summary>Fills a closed cardinal spline through points, tension 0.5.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="points">The points.</param>
    /// <param name="fillmode">Alternate (even-odd) or Winding (nonzero).</param>
    public void FillClosedCurve(Brush brush, ReadOnlySpan<Point> points, FillMode fillmode) => FillClosedCurve(brush, points, fillmode, 0.5f);

    /// <summary>Fills a closed cardinal spline through points.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="points">The points.</param>
    /// <param name="fillmode">Alternate (even-odd) or Winding (nonzero).</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default, 0 gives straight lines.</param>
    public void FillClosedCurve(Brush brush, Point[] points, FillMode fillmode, float tension)
    {
        ArgumentNullException.ThrowIfNull(points);
        FillClosedCurve(brush, points.AsSpan(), fillmode, tension);
    }

    /// <summary>Fills a closed cardinal spline through points.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="points">The points.</param>
    /// <param name="fillmode">Alternate (even-odd) or Winding (nonzero).</param>
    /// <param name="tension">How tightly the curve bends; 0.5 by default, 0 gives straight lines.</param>
    public void FillClosedCurve(Brush brush, ReadOnlySpan<Point> points, FillMode fillmode, float tension)
    {
        ArgumentNullException.ThrowIfNull(brush);
        GdiShapes s = new();
        s.Curve(Points(points), 0, 0, tension, closed: true);
        Fill(brush, s.ToPath(Rule(fillmode)));
    }

    /// <summary>Fills a region.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="region">The region, in world coordinates.</param>
    public void FillRegion(Brush brush, Region region)
    {
        ArgumentNullException.ThrowIfNull(region);
        Fill(brush, region.Shape ?? Path.Rect(new RectF(-4194304, -4194304, 8388608, 8388608)));
    }

    /// <summary>Gets whether a point is inside the bitmap and the clip.</summary>
    /// <param name="x">The x.</param>
    /// <param name="y">The y.</param>
    /// <returns><see langword="true"/> when visible.</returns>
    public bool IsVisible(float x, float y) => IsVisible(new PointF(x, y));

    /// <summary>Gets whether a point is inside the bitmap and the clip.</summary>
    /// <param name="x">The x.</param>
    /// <param name="y">The y.</param>
    /// <returns><see langword="true"/> when visible.</returns>
    public bool IsVisible(int x, int y) => IsVisible(new PointF(x, y));

    /// <summary>Gets whether a point is inside the bitmap and the clip.</summary>
    /// <param name="point">The point.</param>
    /// <returns><see langword="true"/> when visible.</returns>
    public bool IsVisible(Point point) => IsVisible((PointF)point);

    /// <summary>Gets whether any of a rectangle is inside the bitmap and the clip.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <returns><see langword="true"/> when visible.</returns>
    public bool IsVisible(float x, float y, float width, float height) => IsVisible(new RectangleF(x, y, width, height));

    /// <summary>Gets whether any of a rectangle is inside the bitmap and the clip.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <returns><see langword="true"/> when visible.</returns>
    public bool IsVisible(int x, int y, int width, int height) => IsVisible(new RectangleF(x, y, width, height));

    /// <summary>Gets whether any of a rectangle is inside the bitmap and the clip.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <returns><see langword="true"/> when visible.</returns>
    public bool IsVisible(Rectangle rect) => IsVisible((RectangleF)rect);

    /// <summary>Draws an image at its physical size: its resolution against the graphics' resolution.</summary>
    /// <param name="image">The image.</param>
    /// <param name="point">The top-left corner.</param>
    public void DrawImage(Image image, PointF point) => DrawImageAt(image, point.X, point.Y);

    /// <summary>Draws an image at its physical size: its resolution against the graphics' resolution.</summary>
    /// <param name="image">The image.</param>
    /// <param name="point">The top-left corner.</param>
    public void DrawImage(Image image, Point point) => DrawImageAt(image, point.X, point.Y);

    /// <summary>Draws an image at its physical size: its resolution against the graphics' resolution.</summary>
    /// <param name="image">The image.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    public void DrawImage(Image image, float x, float y) => DrawImageAt(image, x, y);

    /// <summary>Draws an image at its physical size: its resolution against the graphics' resolution.</summary>
    /// <param name="image">The image.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    public void DrawImage(Image image, int x, int y) => DrawImageAt(image, x, y);

    /// <summary>Draws an image stretched into a rectangle.</summary>
    /// <param name="image">The image.</param>
    /// <param name="rect">The rectangle.</param>
    public void DrawImage(Image image, RectangleF rect) => DrawImageRect(image, rect);

    /// <summary>Draws an image stretched into a rectangle.</summary>
    /// <param name="image">The image.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public void DrawImage(Image image, float x, float y, float width, float height) => DrawImageRect(image, new RectangleF(x, y, width, height));

    /// <summary>Draws an image stretched into a rectangle.</summary>
    /// <param name="image">The image.</param>
    /// <param name="rect">The rectangle.</param>
    public void DrawImage(Image image, Rectangle rect) => DrawImageRect(image, rect);

    /// <summary>Draws an image stretched into a rectangle.</summary>
    /// <param name="image">The image.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public void DrawImage(Image image, int x, int y, int width, int height) => DrawImageRect(image, new RectangleF(x, y, width, height));

    /// <summary>Draws an image onto a parallelogram.</summary>
    /// <param name="image">The image.</param>
    /// <param name="destPoints">Where the top-left, top-right and bottom-left corners go.</param>
    public void DrawImage(Image image, PointF[] destPoints)
    {
        ArgumentNullException.ThrowIfNull(destPoints);
        DrawImagePoints(image, Corners(destPoints));
    }

    /// <summary>Draws an image onto a parallelogram.</summary>
    /// <param name="image">The image.</param>
    /// <param name="destPoints">Where the top-left, top-right and bottom-left corners go.</param>
    public void DrawImage(Image image, Point[] destPoints)
    {
        ArgumentNullException.ThrowIfNull(destPoints);
        DrawImagePoints(image, Corners(destPoints));
    }

    /// <summary>Draws part of an image at its physical size.</summary>
    /// <param name="image">The image.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="srcRect">The part of the image.</param>
    /// <param name="srcUnit">The unit of the source rectangle.</param>
    public void DrawImage(Image image, float x, float y, RectangleF srcRect, GraphicsUnit srcUnit) => DrawImageAt(image, x, y, srcRect, srcUnit);

    /// <summary>Draws part of an image at its physical size.</summary>
    /// <param name="image">The image.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="srcRect">The part of the image.</param>
    /// <param name="srcUnit">The unit of the source rectangle.</param>
    public void DrawImage(Image image, int x, int y, Rectangle srcRect, GraphicsUnit srcUnit) => DrawImageAt(image, x, y, srcRect, srcUnit);

    /// <summary>Draws part of an image stretched into a rectangle.</summary>
    /// <param name="image">The image.</param>
    /// <param name="destRect">The rectangle.</param>
    /// <param name="srcRect">The part of the image.</param>
    /// <param name="srcUnit">The unit of the source rectangle.</param>
    public void DrawImage(Image image, RectangleF destRect, RectangleF srcRect, GraphicsUnit srcUnit) => DrawImageRect(image, destRect, srcRect, srcUnit);

    /// <summary>Draws part of an image stretched into a rectangle.</summary>
    /// <param name="image">The image.</param>
    /// <param name="destRect">The rectangle.</param>
    /// <param name="srcRect">The part of the image.</param>
    /// <param name="srcUnit">The unit of the source rectangle.</param>
    public void DrawImage(Image image, Rectangle destRect, Rectangle srcRect, GraphicsUnit srcUnit) => DrawImageRect(image, destRect, srcRect, srcUnit);

    /// <summary>Draws part of an image onto a parallelogram.</summary>
    /// <param name="image">The image.</param>
    /// <param name="destPoints">Where the top-left, top-right and bottom-left corners go.</param>
    /// <param name="srcRect">The part of the image.</param>
    /// <param name="srcUnit">The unit of the source rectangle.</param>
    public void DrawImage(Image image, PointF[] destPoints, RectangleF srcRect, GraphicsUnit srcUnit) => DrawImage(image, destPoints, srcRect, srcUnit, null, null, 0);

    /// <summary>Draws part of an image onto a parallelogram.</summary>
    /// <param name="image">The image.</param>
    /// <param name="destPoints">Where the top-left, top-right and bottom-left corners go.</param>
    /// <param name="srcRect">The part of the image.</param>
    /// <param name="srcUnit">The unit of the source rectangle.</param>
    /// <param name="imageAttr">Color adjustments and the wrap mode, or null.</param>
    public void DrawImage(Image image, PointF[] destPoints, RectangleF srcRect, GraphicsUnit srcUnit, ImageAttributes? imageAttr) => DrawImage(image, destPoints, srcRect, srcUnit, imageAttr, null, 0);

    /// <summary>Draws part of an image onto a parallelogram.</summary>
    /// <param name="image">The image.</param>
    /// <param name="destPoints">Where the top-left, top-right and bottom-left corners go.</param>
    /// <param name="srcRect">The part of the image.</param>
    /// <param name="srcUnit">The unit of the source rectangle.</param>
    /// <param name="imageAttr">Color adjustments and the wrap mode, or null.</param>
    /// <param name="callback">Called while drawing; returning true stops it.</param>
    public void DrawImage(Image image, PointF[] destPoints, RectangleF srcRect, GraphicsUnit srcUnit, ImageAttributes? imageAttr, DrawImageAbort? callback) => DrawImage(image, destPoints, srcRect, srcUnit, imageAttr, callback, 0);

    /// <summary>Draws part of an image onto a parallelogram.</summary>
    /// <param name="image">The image.</param>
    /// <param name="destPoints">Where the top-left, top-right and bottom-left corners go.</param>
    /// <param name="srcRect">The part of the image.</param>
    /// <param name="srcUnit">The unit of the source rectangle.</param>
    /// <param name="imageAttr">Color adjustments and the wrap mode, or null.</param>
    /// <param name="callback">Called while drawing; returning true stops it.</param>
    /// <param name="callbackData">Data for the callback.</param>
    public void DrawImage(Image image, PointF[] destPoints, RectangleF srcRect, GraphicsUnit srcUnit, ImageAttributes? imageAttr, DrawImageAbort? callback, int callbackData)
    {
        ArgumentNullException.ThrowIfNull(destPoints);
        DrawImagePoints(image, Corners(destPoints), srcRect, srcUnit, imageAttr, callback, callbackData);
    }

    /// <summary>Draws part of an image onto a parallelogram.</summary>
    /// <param name="image">The image.</param>
    /// <param name="destPoints">Where the top-left, top-right and bottom-left corners go.</param>
    /// <param name="srcRect">The part of the image.</param>
    /// <param name="srcUnit">The unit of the source rectangle.</param>
    public void DrawImage(Image image, Point[] destPoints, Rectangle srcRect, GraphicsUnit srcUnit) => DrawImage(image, destPoints, srcRect, srcUnit, null, null, 0);

    /// <summary>Draws part of an image onto a parallelogram.</summary>
    /// <param name="image">The image.</param>
    /// <param name="destPoints">Where the top-left, top-right and bottom-left corners go.</param>
    /// <param name="srcRect">The part of the image.</param>
    /// <param name="srcUnit">The unit of the source rectangle.</param>
    /// <param name="imageAttr">Color adjustments and the wrap mode, or null.</param>
    public void DrawImage(Image image, Point[] destPoints, Rectangle srcRect, GraphicsUnit srcUnit, ImageAttributes? imageAttr) => DrawImage(image, destPoints, srcRect, srcUnit, imageAttr, null, 0);

    /// <summary>Draws part of an image onto a parallelogram.</summary>
    /// <param name="image">The image.</param>
    /// <param name="destPoints">Where the top-left, top-right and bottom-left corners go.</param>
    /// <param name="srcRect">The part of the image.</param>
    /// <param name="srcUnit">The unit of the source rectangle.</param>
    /// <param name="imageAttr">Color adjustments and the wrap mode, or null.</param>
    /// <param name="callback">Called while drawing; returning true stops it.</param>
    public void DrawImage(Image image, Point[] destPoints, Rectangle srcRect, GraphicsUnit srcUnit, ImageAttributes? imageAttr, DrawImageAbort? callback) => DrawImage(image, destPoints, srcRect, srcUnit, imageAttr, callback, 0);

    /// <summary>Draws part of an image onto a parallelogram.</summary>
    /// <param name="image">The image.</param>
    /// <param name="destPoints">Where the top-left, top-right and bottom-left corners go.</param>
    /// <param name="srcRect">The part of the image.</param>
    /// <param name="srcUnit">The unit of the source rectangle.</param>
    /// <param name="imageAttr">Color adjustments and the wrap mode, or null.</param>
    /// <param name="callback">Called while drawing; returning true stops it.</param>
    /// <param name="callbackData">Data for the callback.</param>
    public void DrawImage(Image image, Point[] destPoints, Rectangle srcRect, GraphicsUnit srcUnit, ImageAttributes? imageAttr, DrawImageAbort? callback, int callbackData)
    {
        ArgumentNullException.ThrowIfNull(destPoints);
        DrawImagePoints(image, Corners(destPoints), srcRect, srcUnit, imageAttr, callback, callbackData);
    }

    /// <summary>Draws part of an image stretched into a rectangle.</summary>
    /// <param name="image">The image.</param>
    /// <param name="destRect">The rectangle.</param>
    /// <param name="srcX">The source's left edge.</param>
    /// <param name="srcY">The source's top edge.</param>
    /// <param name="srcWidth">The source's width.</param>
    /// <param name="srcHeight">The source's height.</param>
    /// <param name="srcUnit">The unit of the source rectangle.</param>
    public void DrawImage(Image image, Rectangle destRect, float srcX, float srcY, float srcWidth, float srcHeight, GraphicsUnit srcUnit) => DrawImage(image, destRect, srcX, srcY, srcWidth, srcHeight, srcUnit, null, null, IntPtr.Zero);

    /// <summary>Draws part of an image stretched into a rectangle.</summary>
    /// <param name="image">The image.</param>
    /// <param name="destRect">The rectangle.</param>
    /// <param name="srcX">The source's left edge.</param>
    /// <param name="srcY">The source's top edge.</param>
    /// <param name="srcWidth">The source's width.</param>
    /// <param name="srcHeight">The source's height.</param>
    /// <param name="srcUnit">The unit of the source rectangle.</param>
    /// <param name="imageAttrs">Color adjustments and the wrap mode, or null.</param>
    public void DrawImage(Image image, Rectangle destRect, float srcX, float srcY, float srcWidth, float srcHeight, GraphicsUnit srcUnit, ImageAttributes? imageAttrs) => DrawImage(image, destRect, srcX, srcY, srcWidth, srcHeight, srcUnit, imageAttrs, null, IntPtr.Zero);

    /// <summary>Draws part of an image stretched into a rectangle.</summary>
    /// <param name="image">The image.</param>
    /// <param name="destRect">The rectangle.</param>
    /// <param name="srcX">The source's left edge.</param>
    /// <param name="srcY">The source's top edge.</param>
    /// <param name="srcWidth">The source's width.</param>
    /// <param name="srcHeight">The source's height.</param>
    /// <param name="srcUnit">The unit of the source rectangle.</param>
    /// <param name="imageAttrs">Color adjustments and the wrap mode, or null.</param>
    /// <param name="callback">Called while drawing; returning true stops it.</param>
    public void DrawImage(Image image, Rectangle destRect, float srcX, float srcY, float srcWidth, float srcHeight, GraphicsUnit srcUnit, ImageAttributes? imageAttrs, DrawImageAbort? callback) => DrawImage(image, destRect, srcX, srcY, srcWidth, srcHeight, srcUnit, imageAttrs, callback, IntPtr.Zero);

    /// <summary>Draws part of an image stretched into a rectangle.</summary>
    /// <param name="image">The image.</param>
    /// <param name="destRect">The rectangle.</param>
    /// <param name="srcX">The source's left edge.</param>
    /// <param name="srcY">The source's top edge.</param>
    /// <param name="srcWidth">The source's width.</param>
    /// <param name="srcHeight">The source's height.</param>
    /// <param name="srcUnit">The unit of the source rectangle.</param>
    /// <param name="imageAttrs">Color adjustments and the wrap mode, or null.</param>
    /// <param name="callback">Called while drawing; returning true stops it.</param>
    /// <param name="callbackData">Data for the callback.</param>
    public void DrawImage(Image image, Rectangle destRect, float srcX, float srcY, float srcWidth, float srcHeight, GraphicsUnit srcUnit, ImageAttributes? imageAttrs, DrawImageAbort? callback, IntPtr callbackData) =>
        DrawImageRect(image, destRect, new RectangleF(srcX, srcY, srcWidth, srcHeight), srcUnit, imageAttrs, callback, callbackData);

    /// <summary>Draws part of an image stretched into a rectangle.</summary>
    /// <param name="image">The image.</param>
    /// <param name="destRect">The rectangle.</param>
    /// <param name="srcX">The source's left edge.</param>
    /// <param name="srcY">The source's top edge.</param>
    /// <param name="srcWidth">The source's width.</param>
    /// <param name="srcHeight">The source's height.</param>
    /// <param name="srcUnit">The unit of the source rectangle.</param>
    public void DrawImage(Image image, Rectangle destRect, int srcX, int srcY, int srcWidth, int srcHeight, GraphicsUnit srcUnit) => DrawImage(image, destRect, srcX, srcY, srcWidth, srcHeight, srcUnit, null, null, IntPtr.Zero);

    /// <summary>Draws part of an image stretched into a rectangle.</summary>
    /// <param name="image">The image.</param>
    /// <param name="destRect">The rectangle.</param>
    /// <param name="srcX">The source's left edge.</param>
    /// <param name="srcY">The source's top edge.</param>
    /// <param name="srcWidth">The source's width.</param>
    /// <param name="srcHeight">The source's height.</param>
    /// <param name="srcUnit">The unit of the source rectangle.</param>
    /// <param name="imageAttr">Color adjustments and the wrap mode, or null.</param>
    public void DrawImage(Image image, Rectangle destRect, int srcX, int srcY, int srcWidth, int srcHeight, GraphicsUnit srcUnit, ImageAttributes? imageAttr) => DrawImage(image, destRect, srcX, srcY, srcWidth, srcHeight, srcUnit, imageAttr, null, IntPtr.Zero);

    /// <summary>Draws part of an image stretched into a rectangle.</summary>
    /// <param name="image">The image.</param>
    /// <param name="destRect">The rectangle.</param>
    /// <param name="srcX">The source's left edge.</param>
    /// <param name="srcY">The source's top edge.</param>
    /// <param name="srcWidth">The source's width.</param>
    /// <param name="srcHeight">The source's height.</param>
    /// <param name="srcUnit">The unit of the source rectangle.</param>
    /// <param name="imageAttr">Color adjustments and the wrap mode, or null.</param>
    /// <param name="callback">Called while drawing; returning true stops it.</param>
    public void DrawImage(Image image, Rectangle destRect, int srcX, int srcY, int srcWidth, int srcHeight, GraphicsUnit srcUnit, ImageAttributes? imageAttr, DrawImageAbort? callback) => DrawImage(image, destRect, srcX, srcY, srcWidth, srcHeight, srcUnit, imageAttr, callback, IntPtr.Zero);

    /// <summary>Draws part of an image stretched into a rectangle.</summary>
    /// <param name="image">The image.</param>
    /// <param name="destRect">The rectangle.</param>
    /// <param name="srcX">The source's left edge.</param>
    /// <param name="srcY">The source's top edge.</param>
    /// <param name="srcWidth">The source's width.</param>
    /// <param name="srcHeight">The source's height.</param>
    /// <param name="srcUnit">The unit of the source rectangle.</param>
    /// <param name="imageAttrs">Color adjustments and the wrap mode, or null.</param>
    /// <param name="callback">Called while drawing; returning true stops it.</param>
    /// <param name="callbackData">Data for the callback.</param>
    public void DrawImage(Image image, Rectangle destRect, int srcX, int srcY, int srcWidth, int srcHeight, GraphicsUnit srcUnit, ImageAttributes? imageAttrs, DrawImageAbort? callback, IntPtr callbackData) =>
        DrawImageRect(image, destRect, new RectangleF(srcX, srcY, srcWidth, srcHeight), srcUnit, imageAttrs, callback, callbackData);

    /// <summary>Draws an image at its physical size.</summary>
    /// <param name="image">The image.</param>
    /// <param name="point">The top-left corner.</param>
    public void DrawImageUnscaled(Image image, Point point) => DrawImage(image, point.X, point.Y);

    /// <summary>Draws an image at its physical size.</summary>
    /// <param name="image">The image.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    public void DrawImageUnscaled(Image image, int x, int y) => DrawImage(image, x, y);

    /// <summary>Draws an image at its physical size at a rectangle's corner; the size is not used.</summary>
    /// <param name="image">The image.</param>
    /// <param name="rect">The rectangle.</param>
    public void DrawImageUnscaled(Image image, Rectangle rect) => DrawImage(image, rect.X, rect.Y);

    /// <summary>Draws an image at its physical size; the size is not used.</summary>
    /// <param name="image">The image.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public void DrawImageUnscaled(Image image, int x, int y, int width, int height) => DrawImage(image, x, y);

    /// <summary>Draws an image pixel for pixel, cut to a rectangle.</summary>
    /// <param name="image">The image.</param>
    /// <param name="rect">The rectangle.</param>
    public void DrawImageUnscaledAndClipped(Image image, Rectangle rect)
    {
        ArgumentNullException.ThrowIfNull(image);
        int width = Math.Min(rect.Width, image.Width), height = Math.Min(rect.Height, image.Height);
        DrawImage(image, new Rectangle(rect.X, rect.Y, width, height), 0, 0, width, height, GraphicsUnit.Pixel);
    }

    /// <summary>Draws an icon at its size.</summary>
    /// <param name="icon">The icon.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    public void DrawIcon(Icon icon, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(icon);
        DrawIcon(icon, new Rectangle(x, y, icon.Width, icon.Height));
    }

    /// <summary>Draws an icon stretched into a rectangle.</summary>
    /// <param name="icon">The icon.</param>
    /// <param name="targetRect">The rectangle.</param>
    public void DrawIcon(Icon icon, Rectangle targetRect)
    {
        ArgumentNullException.ThrowIfNull(icon);
        using Bitmap bitmap = icon.ToBitmap();
        DrawImage(bitmap, targetRect);
    }

    /// <summary>Draws an icon at its own size at a rectangle's corner; as in GDI+, the rectangle's size is not used.</summary>
    /// <param name="icon">The icon.</param>
    /// <param name="targetRect">The rectangle.</param>
    public void DrawIconUnstretched(Icon icon, Rectangle targetRect)
    {
        ArgumentNullException.ThrowIfNull(icon);
        using Bitmap bitmap = icon.ToBitmap();
        DrawImage(bitmap, new Rectangle(targetRect.X, targetRect.Y, icon.Width, icon.Height));
    }

    private static FillRule Rule(FillMode mode) => mode == FillMode.Winding ? FillRule.NonZero : FillRule.EvenOdd;

    private static Vector2[] Points(ReadOnlySpan<PointF> points)
    {
        Vector2[] p = new Vector2[points.Length];
        for (int i = 0; i < p.Length; i++)
        {
            p[i] = new Vector2(points[i].X, points[i].Y);
        }

        return p;
    }

    private static PointF[] Corners(PointF[] points) => points.Length == 3 ? [.. points.Select(p => new PointF(p.X, p.Y))] : throw GdiErrors.InvalidParameter();

    private static Vector2[] Points(ReadOnlySpan<Point> points)
    {
        Vector2[] p = new Vector2[points.Length];
        for (int i = 0; i < p.Length; i++)
        {
            p[i] = new Vector2(points[i].X, points[i].Y);
        }

        return p;
    }

    private static PointF[] Corners(Point[] points) => points.Length == 3 ? [.. points.Select(p => new PointF(p.X, p.Y))] : throw GdiErrors.InvalidParameter();
}
