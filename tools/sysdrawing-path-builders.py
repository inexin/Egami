"""Writes src/Egami.SystemDrawing/Drawing2D/GraphicsPath.Builders.cs: every Add overload of GraphicsPath, forwarding to a core.

Usage: python tools/sysdrawing-path-builders.py src/Egami.SystemDrawing/Drawing2D/GraphicsPath.Builders.cs
"""
import sys

out = []
w = out.append


def doc(summary, params):
    w(f"    /// <summary>{summary}</summary>")
    for name, text in params:
        w(f'    /// <param name="{name}">{text}</param>')


XYWH = [("x", "The left edge."), ("y", "The top edge."), ("width", "The width."), ("height", "The height.")]
ANG = [("startAngle", "The start, in degrees clockwise from the x axis."), ("sweepAngle", "The sweep, in degrees clockwise.")]
PTS = [("points", "The points.")]
TEN = [("tension", "How tightly the curve bends; 0.5 by default.")]

# Lines
doc("Adds a line, joined to the open figure.", [("x1", "The start's x."), ("y1", "The start's y."), ("x2", "The end's x."), ("y2", "The end's y.")])
w("    public void AddLine(float x1, float y1, float x2, float y2)")
w("    {")
w("        GdiShapes s = new();")
w("        s.Start(new Vector2(x1, y1));")
w("        s.Line(new Vector2(x2, y2));")
w("        Append(s, closed: false);")
w("    }")
w("")
doc("Adds a line, joined to the open figure.", [("x1", "The start's x."), ("y1", "The start's y."), ("x2", "The end's x."), ("y2", "The end's y.")])
w("    public void AddLine(int x1, int y1, int x2, int y2) => AddLine((float)x1, y1, x2, y2);")
w("")
for pt in ["PointF", "Point"]:
    doc("Adds a line, joined to the open figure.", [("pt1", "The start."), ("pt2", "The end.")])
    w(f"    public void AddLine({pt} pt1, {pt} pt2) => AddLine((float)pt1.X, pt1.Y, pt2.X, pt2.Y);")
    w("")


def pair(name, summary, body, arr_params=True, extra=(), sig="", call=""):
    for pt in ["PointF", "Point"]:
        pa = "params " if arr_params and not sig else ""
        doc(summary, [*PTS, *extra])
        w(f"    public void {name}({pa}{pt}[] points{sig})")
        w("    {")
        w("        ArgumentNullException.ThrowIfNull(points);")
        w(f"        {name}(points.AsSpan(){call});")
        w("    }")
        w("")
        ps = "params " if not sig else ""
        doc(summary, [*PTS, *extra])
        w(f"    public void {name}({ps}ReadOnlySpan<{pt}> points{sig})")
        w("    {")
        w("        Vector2[] p = V(points);")
        w("        GdiShapes s = new();")
        for line in body:
            w(f"        {line}" if line else "")
        w("    }")
        w("")


pair("AddLines", "Adds connected lines, joined to the open figure.", ["if (p.Length == 0)", "{", "    throw GdiErrors.InvalidParameter();", "}", "", "s.Lines(p, close: false);", "Append(s, closed: false);"])
pair("AddPolygon", "Adds a closed polygon as a figure of its own; a last point equal to the first is dropped.", [
    "if (p.Length > 1 && p[^1] == p[0])", "{", "    p = p[..^1];", "}", "", "if (p.Length < 3)", "{", "    throw GdiErrors.InvalidParameter();", "}", "",
    "s.Lines(p, close: true);", "Append(s, closed: true);"])
pair("AddBeziers", "Adds joined cubic Beziers, joined to the open figure: a start point, then three points each.", ["s.Beziers(p);", "Append(s, closed: false);"])

B = [("x1", "The start's x."), ("y1", "The start's y."), ("x2", "The first control point's x."), ("y2", "The first control point's y."),
     ("x3", "The second control point's x."), ("y3", "The second control point's y."), ("x4", "The end's x."), ("y4", "The end's y.")]
doc("Adds a cubic Bezier, joined to the open figure.", B)
w("    public void AddBezier(float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4)")
w("    {")
w("        GdiShapes s = new();")
w("        s.Start(new Vector2(x1, y1));")
w("        s.Bezier(new Vector2(x2, y2), new Vector2(x3, y3), new Vector2(x4, y4));")
w("        Append(s, closed: false);")
w("    }")
w("")
doc("Adds a cubic Bezier, joined to the open figure.", B)
w("    public void AddBezier(int x1, int y1, int x2, int y2, int x3, int y3, int x4, int y4) => AddBezier((float)x1, y1, x2, y2, x3, y3, x4, y4);")
w("")
for pt in ["PointF", "Point"]:
    doc("Adds a cubic Bezier, joined to the open figure.", [("pt1", "The start."), ("pt2", "The first control point."), ("pt3", "The second control point."), ("pt4", "The end.")])
    w(f"    public void AddBezier({pt} pt1, {pt} pt2, {pt} pt3, {pt} pt4) => AddBezier((float)pt1.X, pt1.Y, pt2.X, pt2.Y, pt3.X, pt3.Y, pt4.X, pt4.Y);")
    w("")

# Curves
for pt in ["PointF", "Point"]:
    doc("Adds a cardinal spline through points, tension 0.5, joined to the open figure.", PTS)
    w(f"    public void AddCurve({'' if pt == 'Point' and False else 'params '}{pt}[] points) => AddCurve(points, 0.5f);")
    w("")
    doc("Adds a cardinal spline through points, tension 0.5, joined to the open figure.", PTS)
    w(f"    public void AddCurve({'params ' if pt == 'PointF' else ''}ReadOnlySpan<{pt}> points) => AddCurve(points, 0.5f);")
    w("")
    doc("Adds a cardinal spline through points, joined to the open figure.", [*PTS, *TEN])
    w(f"    public void AddCurve({pt}[] points, float tension)")
    w("    {")
    w("        ArgumentNullException.ThrowIfNull(points);")
    w("        AddCurve(points.AsSpan(), tension);")
    w("    }")
    w("")
    doc("Adds a cardinal spline through points, joined to the open figure.", [*PTS, *TEN])
    w(f"    public void AddCurve(ReadOnlySpan<{pt}> points, float tension) => AddCurve(V(points), 0, points.Length - 1, tension);")
    w("")
    doc("Adds part of a cardinal spline, joined to the open figure.", [*PTS, ("offset", "The point the part starts at."), ("numberOfSegments", "The number of segments."), *TEN])
    w(f"    public void AddCurve({pt}[] points, int offset, int numberOfSegments, float tension)")
    w("    {")
    w("        ArgumentNullException.ThrowIfNull(points);")
    w("        AddCurve(V(points), offset, numberOfSegments, tension);")
    w("    }")
    w("")
    doc("Adds a closed cardinal spline through points, tension 0.5, as a figure of its own.", PTS)
    w(f"    public void AddClosedCurve(params {pt}[] points) => AddClosedCurve(points, 0.5f);")
    w("")
    doc("Adds a closed cardinal spline through points, tension 0.5, as a figure of its own.", PTS)
    w(f"    public void AddClosedCurve(params ReadOnlySpan<{pt}> points) => AddClosedCurve(points, 0.5f);")
    w("")
    doc("Adds a closed cardinal spline through points as a figure of its own.", [*PTS, *TEN])
    w(f"    public void AddClosedCurve({pt}[] points, float tension)")
    w("    {")
    w("        ArgumentNullException.ThrowIfNull(points);")
    w("        AddClosedCurve(points.AsSpan(), tension);")
    w("    }")
    w("")
    doc("Adds a closed cardinal spline through points as a figure of its own.", [*PTS, *TEN])
    w(f"    public void AddClosedCurve(ReadOnlySpan<{pt}> points, float tension)")
    w("    {")
    w("        GdiShapes s = new();")
    w("        s.Curve(V(points), 0, 0, tension, closed: true);")
    w("        Append(s, closed: true);")
    w("    }")
    w("")

# Rectangles, ellipses, arcs, pies
doc("Adds a rectangle as a figure of its own; an empty one adds nothing.", [("rect", "The rectangle.")])
w("    public void AddRectangle(RectangleF rect)")
w("    {")
w("        if (rect.Width == 0 || rect.Height == 0)")
w("        {")
w("            return;")
w("        }")
w("")
w("        GdiShapes s = new();")
w("        s.Rectangle(rect.X, rect.Y, rect.Width, rect.Height);")
w("        Append(s, closed: true);")
w("    }")
w("")
doc("Adds a rectangle as a figure of its own; an empty one adds nothing.", [("rect", "The rectangle.")])
w("    public void AddRectangle(Rectangle rect) => AddRectangle((RectangleF)rect);")
w("")
for rt in ["RectangleF", "Rectangle"]:
    doc("Adds rectangles, each a figure of its own.", [("rects", "The rectangles.")])
    w(f"    public void AddRectangles(params {rt}[] rects)")
    w("    {")
    w("        ArgumentNullException.ThrowIfNull(rects);")
    w("        AddRectangles(rects.AsSpan());")
    w("    }")
    w("")
    doc("Adds rectangles, each a figure of its own.", [("rects", "The rectangles.")])
    w(f"    public void AddRectangles(params ReadOnlySpan<{rt}> rects)")
    w("    {")
    w(f"        foreach ({rt} r in rects)")
    w("        {")
    w("            AddRectangle(r);")
    w("        }")
    w("    }")
    w("")
doc("Adds the ellipse in a rectangle as a figure of its own: four quarter Beziers from its right-hand point.", XYWH)
w("    public void AddEllipse(float x, float y, float width, float height)")
w("    {")
w("        GdiShapes s = new();")
w("        s.Ellipse(x, y, width, height);")
w("        Append(s, closed: true);")
w("    }")
w("")
doc("Adds the ellipse in a rectangle as a figure of its own.", XYWH)
w("    public void AddEllipse(int x, int y, int width, int height) => AddEllipse((float)x, y, width, height);")
w("")
for rt in ["RectangleF", "Rectangle"]:
    doc("Adds the ellipse in a rectangle as a figure of its own.", [("rect", "The rectangle.")])
    w(f"    public void AddEllipse({rt} rect) => AddEllipse((float)rect.X, rect.Y, rect.Width, rect.Height);")
    w("")
doc("Adds an arc of the ellipse in a rectangle, joined to the open figure.", [*XYWH, *ANG])
w("    public void AddArc(float x, float y, float width, float height, float startAngle, float sweepAngle)")
w("    {")
w("        GdiShapes s = new();")
w("        s.Arc(x, y, width, height, startAngle, sweepAngle, start: true);")
w("        Append(s, closed: false);")
w("    }")
w("")
doc("Adds an arc of the ellipse in a rectangle, joined to the open figure.", [*XYWH, *ANG])
w("    public void AddArc(int x, int y, int width, int height, float startAngle, float sweepAngle) => AddArc((float)x, y, width, height, startAngle, sweepAngle);")
w("")
for rt in ["RectangleF", "Rectangle"]:
    doc("Adds an arc of the ellipse in a rectangle, joined to the open figure.", [("rect", "The rectangle."), *ANG])
    w(f"    public void AddArc({rt} rect, float startAngle, float sweepAngle) => AddArc((float)rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);")
    w("")
doc("Adds a pie as a figure of its own: the center, the arc, and back.", [*XYWH, *ANG])
w("    public void AddPie(float x, float y, float width, float height, float startAngle, float sweepAngle)")
w("    {")
w("        GdiShapes s = new();")
w("        s.Pie(x, y, width, height, startAngle, sweepAngle);")
w("        Append(s, closed: true);")
w("    }")
w("")
doc("Adds a pie as a figure of its own.", [*XYWH, *ANG])
w("    public void AddPie(int x, int y, int width, int height, float startAngle, float sweepAngle) => AddPie((float)x, y, width, height, startAngle, sweepAngle);")
w("")
doc("Adds a pie as a figure of its own.", [("rect", "The rectangle."), *ANG])
w("    public void AddPie(Rectangle rect, float startAngle, float sweepAngle) => AddPie((float)rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);")
w("")
doc("Adds a rectangle with rounded corners as a figure of its own.", [("rect", "The rectangle."), ("radius", "The size of each corner's ellipse; both must be more than zero.")])
w("    public void AddRoundedRectangle(RectangleF rect, SizeF radius)")
w("    {")
w("        if (radius.Width <= 0 || radius.Height <= 0)")
w("        {")
w("            throw GdiErrors.InvalidParameter();")
w("        }")
w("")
w("        GdiShapes s = new();")
w("        s.RoundedRectangle(rect.X, rect.Y, rect.Width, rect.Height, radius.Width, radius.Height);")
w("        Append(s, closed: true);")
w("    }")
w("")
doc("Adds a rectangle with rounded corners as a figure of its own.", [("rect", "The rectangle."), ("radius", "The size of each corner's ellipse; both must be more than zero.")])
w("    public void AddRoundedRectangle(Rectangle rect, Size radius) => AddRoundedRectangle((RectangleF)rect, (SizeF)radius);")
w("")
w("    private void AddCurve(Vector2[] p, int offset, int numberOfSegments, float tension)")
w("    {")
w("        if (p.Length < 2 || offset < 0 || numberOfSegments < 1 || offset + numberOfSegments > p.Length - 1)")
w("        {")
w("            throw GdiErrors.InvalidParameter();")
w("        }")
w("")
w("        GdiShapes s = new();")
w("        s.Curve(p, offset, numberOfSegments, tension, closed: false);")
w("        Append(s, closed: false);")
w("    }")

header = """// Written by tools/sysdrawing-path-builders.py; each builder forwards to GdiShapes and GraphicsPath.Append.
using System.Numerics;

using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing.Drawing2D;

using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

/// <summary>The Add builders.</summary>
public sealed partial class GraphicsPath
{
"""
open(sys.argv[1], "w", encoding="utf-8", newline="\n").write(header + "\n".join(line.rstrip() for line in out) + "\n}\n")
