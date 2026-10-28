"""Writes src/Egami.SystemDrawing/Graphics.Overloads.cs: every Draw, Fill and DrawImage overload of Graphics, forwarding to a core.

Usage: python tools/sysdrawing-graphics-overloads.py src/Egami.SystemDrawing/Graphics.Overloads.cs
"""
import sys

out = []
w = out.append


def doc(summary, params, extra=""):
    w(f"    /// <summary>{summary}</summary>")
    for name, text in params:
        w(f'    /// <param name="{name}">{text}</param>')
    if extra:
        w(extra)


PEN = ("pen", "The pen.")
BRUSH = ("brush", "The brush.")
XYWH = [("x", "The left edge."), ("y", "The top edge."), ("width", "The width."), ("height", "The height.")]
RECT = [("rect", "The rectangle.")]
ANG = [("startAngle", "The start, in degrees clockwise from the x axis."), ("sweepAngle", "The sweep, in degrees clockwise.")]
PTS = [("points", "The points.")]

# ---- Arcs, pies, ellipses, rectangles ----
for name, verb, tool, core in [
    ("DrawArc", "Strokes an arc of the ellipse in a rectangle.", PEN, "Arc"),
    ("DrawPie", "Strokes a pie: an arc of the ellipse in a rectangle, closed through its center.", PEN, "Pie"),
    ("FillPie", "Fills a pie: an arc of the ellipse in a rectangle, closed through its center.", BRUSH, "Pie"),
]:
    t = "Pen pen" if tool is PEN else "Brush brush"
    v = tool[0]
    action = "Stroke" if tool is PEN else "Fill"
    shape = f"s.Arc(x, y, width, height, startAngle, sweepAngle, start: true)" if core == "Arc" else "s.Pie(x, y, width, height, startAngle, sweepAngle)"
    doc(verb, [tool, *XYWH, *ANG])
    w(f"    public void {name}({t}, float x, float y, float width, float height, float startAngle, float sweepAngle)")
    w("    {")
    w("        GdiShapes s = new();")
    w(f"        {shape};")
    w(f"        {action}({v}, s.ToPath());")
    w("    }")
    w("")
    doc(verb, [tool, *XYWH, *ANG])
    w(f"    public void {name}({t}, int x, int y, int width, int height, int startAngle, int sweepAngle) => {name}({v}, (float)x, y, width, height, startAngle, sweepAngle);")
    w("")
    for rt in ["RectangleF", "Rectangle"]:
        doc(verb, [tool, *RECT, *ANG])
        w(f"    public void {name}({t}, {rt} rect, float startAngle, float sweepAngle) => {name}({v}, (float)rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);")
        w("")

for name, verb, tool, shape in [
    ("DrawEllipse", "Strokes the ellipse in a rectangle.", PEN, "Ellipse"),
    ("FillEllipse", "Fills the ellipse in a rectangle.", BRUSH, "Ellipse"),
    ("DrawRectangle", "Strokes a rectangle.", PEN, "Rectangle"),
    ("FillRectangle", "Fills a rectangle.", BRUSH, "Rectangle"),
]:
    t = "Pen pen" if tool is PEN else "Brush brush"
    v = tool[0]
    action = "Stroke" if tool is PEN else "Fill"
    doc(verb, [tool, *XYWH])
    w(f"    public void {name}({t}, float x, float y, float width, float height)")
    w("    {")
    w("        GdiShapes s = new();")
    w(f"        s.{shape}(x, y, width, height);")
    w(f"        {action}({v}, s.ToPath());")
    w("    }")
    w("")
    doc(verb, [tool, *XYWH])
    w(f"    public void {name}({t}, int x, int y, int width, int height) => {name}({v}, (float)x, y, width, height);")
    w("")
    for rt in ["RectangleF", "Rectangle"]:
        doc(verb, [tool, *RECT])
        w(f"    public void {name}({t}, {rt} rect) => {name}({v}, (float)rect.X, rect.Y, rect.Width, rect.Height);")
        w("")

for name, verb, tool in [("DrawRectangles", "Strokes rectangles.", PEN), ("FillRectangles", "Fills rectangles.", BRUSH)]:
    t = "Pen pen" if tool is PEN else "Brush brush"
    v = tool[0]
    action = "Stroke" if tool is PEN else "Fill"
    single = "DrawRectangle" if tool is PEN else "FillRectangle"
    for rt in ["RectangleF", "Rectangle"]:
        doc(verb, [tool, ("rects", "The rectangles.")])
        w(f"    public void {name}({t}, params {rt}[] rects)")
        w("    {")
        w("        ArgumentNullException.ThrowIfNull(rects);")
        w(f"        {name}({v}, rects.AsSpan());")
        w("    }")
        w("")
        doc(verb, [tool, ("rects", "The rectangles.")])
        w(f"    public void {name}({t}, params ReadOnlySpan<{rt}> rects)")
        w("    {")
        w(f"        ArgumentNullException.ThrowIfNull({v});")
        w("        if (rects.IsEmpty)")
        w("        {")
        w("            throw GdiErrors.InvalidParameter();")
        w("        }")
        w("")
        w("        GdiShapes s = new();")
        w(f"        foreach ({rt} r in rects)")
        w("        {")
        w("            s.Rectangle(r.X, r.Y, r.Width, r.Height);")
        w("        }")
        w("")
        w(f"        {action}({v}, s.ToPath({'' if tool is PEN else 'FillRule.NonZero'}));")
        w("    }")
        w("")

for name, verb, tool in [("DrawRoundedRectangle", "Strokes a rectangle with rounded corners.", PEN), ("FillRoundedRectangle", "Fills a rectangle with rounded corners.", BRUSH)]:
    t = "Pen pen" if tool is PEN else "Brush brush"
    v = tool[0]
    action = "Stroke" if tool is PEN else "Fill"
    doc(verb, [tool, *RECT, ("radius", "The size of each corner's ellipse.")])
    w(f"    public void {name}({t}, RectangleF rect, SizeF radius)")
    w("    {")
    w("        GdiShapes s = new();")
    w("        s.RoundedRectangle(rect.X, rect.Y, rect.Width, rect.Height, radius.Width, radius.Height);")
    w(f"        {action}({v}, s.ToPath());")
    w("    }")
    w("")
    doc(verb, [tool, *RECT, ("radius", "The size of each corner's ellipse.")])
    w(f"    public void {name}({t}, Rectangle rect, Size radius) => {name}({v}, (RectangleF)rect, (SizeF)radius);")
    w("")

# ---- Lines and Beziers ----
doc("Strokes a line.", [PEN, ("x1", "The start's x."), ("y1", "The start's y."), ("x2", "The end's x."), ("y2", "The end's y.")])
w("    public void DrawLine(Pen pen, float x1, float y1, float x2, float y2)")
w("    {")
w("        GdiShapes s = new();")
w("        s.Start(new Vector2(x1, y1));")
w("        s.Line(new Vector2(x2, y2));")
w("        Stroke(pen, s.ToPath());")
w("    }")
w("")
doc("Strokes a line.", [PEN, ("x1", "The start's x."), ("y1", "The start's y."), ("x2", "The end's x."), ("y2", "The end's y.")])
w("    public void DrawLine(Pen pen, int x1, int y1, int x2, int y2) => DrawLine(pen, (float)x1, y1, x2, y2);")
w("")
for pt in ["PointF", "Point"]:
    doc("Strokes a line.", [PEN, ("pt1", "The start."), ("pt2", "The end.")])
    w(f"    public void DrawLine(Pen pen, {pt} pt1, {pt} pt2) => DrawLine(pen, (float)pt1.X, pt1.Y, pt2.X, pt2.Y);")
    w("")

B = [("x1", "The start's x."), ("y1", "The start's y."), ("x2", "The first control point's x."), ("y2", "The first control point's y."),
     ("x3", "The second control point's x."), ("y3", "The second control point's y."), ("x4", "The end's x."), ("y4", "The end's y.")]
doc("Strokes a cubic Bezier.", [PEN, *B])
w("    public void DrawBezier(Pen pen, float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4)")
w("    {")
w("        GdiShapes s = new();")
w("        s.Start(new Vector2(x1, y1));")
w("        s.Bezier(new Vector2(x2, y2), new Vector2(x3, y3), new Vector2(x4, y4));")
w("        Stroke(pen, s.ToPath());")
w("    }")
w("")
for pt in ["PointF", "Point"]:
    doc("Strokes a cubic Bezier.", [PEN, ("pt1", "The start."), ("pt2", "The first control point."), ("pt3", "The second control point."), ("pt4", "The end.")])
    w(f"    public void DrawBezier(Pen pen, {pt} pt1, {pt} pt2, {pt} pt3, {pt} pt4) => DrawBezier(pen, (float)pt1.X, pt1.Y, pt2.X, pt2.Y, pt3.X, pt3.Y, pt4.X, pt4.Y);")
    w("")


def points_pair(name, summary, tool, params_extra, sig_extra, body, array_params=True, span_params=True, arr_types=("PointF", "Point"), call_extra=""):
    """An array overload forwarding to a span overload, and the span overload running body (which sees Vector2[] p)."""
    t = "Pen pen" if tool is PEN else "Brush brush"
    v = tool[0]
    for pt in arr_types:
        pa = "params " if array_params and not sig_extra else ""
        doc(summary, [tool, *PTS, *params_extra])
        w(f"    public void {name}({t}, {pa}{pt}[] points{sig_extra})")
        w("    {")
        w("        ArgumentNullException.ThrowIfNull(points);")
        w(f"        {name}({v}, points.AsSpan(){call_extra});")
        w("    }")
        w("")
        ps = "params " if span_params and not sig_extra else ""
        doc(summary, [tool, *PTS, *params_extra])
        w(f"    public void {name}({t}, {ps}ReadOnlySpan<{pt}> points{sig_extra})")
        w("    {")
        w(f"        ArgumentNullException.ThrowIfNull({v});")
        w("        Vector2[] p = Points(points);")
        w("        GdiShapes s = new();")
        for line in body:
            w(f"        {line}")
        w("    }")
        w("")


points_pair("DrawLines", "Strokes connected lines.", PEN, [], "", ["if (p.Length < 2)", "{", "    throw GdiErrors.InvalidParameter();", "}", "", "s.Lines(p, close: false);", "Stroke(pen, s.ToPath());"])
points_pair("DrawPolygon", "Strokes a polygon.", PEN, [], "", ["if (p.Length < 2)", "{", "    throw GdiErrors.InvalidParameter();", "}", "", "s.Lines(p, close: true);", "Stroke(pen, s.ToPath());"])
points_pair("DrawBeziers", "Strokes joined cubic Beziers: a start point, then three points each.", PEN, [], "", ["s.Beziers(p);", "Stroke(pen, s.ToPath());"])

# FillPolygon: Point[] is not params in the reference.
FM = [("fillMode", "Alternate (even-odd) or Winding (nonzero).")]
for pt in ["PointF", "Point"]:
    pa = "params " if pt == "PointF" else ""
    doc("Fills a polygon, even-odd.", [BRUSH, *PTS])
    w(f"    public void FillPolygon(Brush brush, {pa}{pt}[] points) => FillPolygon(brush, points, FillMode.Alternate);")
    w("")
    doc("Fills a polygon, even-odd.", [BRUSH, *PTS])
    w(f"    public void FillPolygon(Brush brush, params ReadOnlySpan<{pt}> points) => FillPolygon(brush, points, FillMode.Alternate);")
    w("")
    doc("Fills a polygon.", [BRUSH, *PTS, *FM])
    w(f"    public void FillPolygon(Brush brush, {pt}[] points, FillMode fillMode)")
    w("    {")
    w("        ArgumentNullException.ThrowIfNull(points);")
    w("        FillPolygon(brush, points.AsSpan(), fillMode);")
    w("    }")
    w("")
    doc("Fills a polygon.", [BRUSH, *PTS, *FM])
    w(f"    public void FillPolygon(Brush brush, ReadOnlySpan<{pt}> points, FillMode fillMode)")
    w("    {")
    w("        ArgumentNullException.ThrowIfNull(brush);")
    w("        Vector2[] p = Points(points);")
    w("        if (p.Length < 3)")
    w("        {")
    w("            throw GdiErrors.InvalidParameter();")
    w("        }")
    w("")
    w("        GdiShapes s = new();")
    w("        s.Lines(p, close: true);")
    w("        Fill(brush, s.ToPath(Rule(fillMode)));")
    w("    }")
    w("")

# ---- Cardinal splines ----
TEN = [("tension", "How tightly the curve bends; 0.5 by default, 0 gives straight lines.")]
for pt in ["PointF", "Point"]:
    doc("Strokes a cardinal spline through points, tension 0.5.", [PEN, *PTS])
    w(f"    public void DrawCurve(Pen pen, params {pt}[] points) => DrawCurve(pen, points, 0.5f);")
    w("")
    doc("Strokes a cardinal spline through points, tension 0.5.", [PEN, *PTS])
    w(f"    public void DrawCurve(Pen pen, params ReadOnlySpan<{pt}> points) => DrawCurve(pen, points, 0.5f);")
    w("")
    doc("Strokes a cardinal spline through points.", [PEN, *PTS, *TEN])
    w(f"    public void DrawCurve(Pen pen, {pt}[] points, float tension)")
    w("    {")
    w("        ArgumentNullException.ThrowIfNull(points);")
    w("        DrawCurve(pen, points.AsSpan(), tension);")
    w("    }")
    w("")
    doc("Strokes a cardinal spline through points.", [PEN, *PTS, *TEN])
    w(f"    public void DrawCurve(Pen pen, ReadOnlySpan<{pt}> points, float tension) => DrawCurve(pen, points, 0, points.Length - 1, tension);")
    w("")
    if pt == "PointF":
        doc("Strokes part of a cardinal spline, tension 0.5.", [PEN, *PTS, ("offset", "The point the drawn part starts at."), ("numberOfSegments", "The number of segments drawn.")])
        w(f"    public void DrawCurve(Pen pen, {pt}[] points, int offset, int numberOfSegments) => DrawCurve(pen, points, offset, numberOfSegments, 0.5f);")
        w("")
        doc("Strokes part of a cardinal spline, tension 0.5.", [PEN, *PTS, ("offset", "The point the drawn part starts at."), ("numberOfSegments", "The number of segments drawn.")])
        w(f"    public void DrawCurve(Pen pen, ReadOnlySpan<{pt}> points, int offset, int numberOfSegments) => DrawCurve(pen, points, offset, numberOfSegments, 0.5f);")
        w("")
    doc("Strokes part of a cardinal spline.", [PEN, *PTS, ("offset", "The point the drawn part starts at."), ("numberOfSegments", "The number of segments drawn."), *TEN])
    w(f"    public void DrawCurve(Pen pen, {pt}[] points, int offset, int numberOfSegments, float tension)")
    w("    {")
    w("        ArgumentNullException.ThrowIfNull(points);")
    w("        DrawCurve(pen, points.AsSpan(), offset, numberOfSegments, tension);")
    w("    }")
    w("")
    doc("Strokes part of a cardinal spline.", [PEN, *PTS, ("offset", "The point the drawn part starts at."), ("numberOfSegments", "The number of segments drawn."), *TEN])
    w(f"    public void DrawCurve(Pen pen, ReadOnlySpan<{pt}> points, int offset, int numberOfSegments, float tension)")
    w("    {")
    w("        ArgumentNullException.ThrowIfNull(pen);")
    w("        Vector2[] p = Points(points);")
    w("        if (p.Length < 2 || offset < 0 || numberOfSegments < 1 || offset + numberOfSegments > p.Length - 1)")
    w("        {")
    w("            throw GdiErrors.InvalidParameter();")
    w("        }")
    w("")
    w("        GdiShapes s = new();")
    w("        s.Curve(p, offset, numberOfSegments, tension, closed: false);")
    w("        Stroke(pen, s.ToPath());")
    w("    }")
    w("")

    doc("Strokes a closed cardinal spline through points, tension 0.5.", [PEN, *PTS])
    w(f"    public void DrawClosedCurve(Pen pen, params {pt}[] points) => DrawClosedCurve(pen, points, 0.5f, FillMode.Alternate);")
    w("")
    doc("Strokes a closed cardinal spline through points, tension 0.5.", [PEN, *PTS])
    w(f"    public void DrawClosedCurve(Pen pen, params ReadOnlySpan<{pt}> points) => DrawClosedCurve(pen, points, 0.5f, FillMode.Alternate);")
    w("")
    doc("Strokes a closed cardinal spline through points.", [PEN, *PTS, *TEN, ("fillmode", "Not used by a stroke.")])
    w(f"    public void DrawClosedCurve(Pen pen, {pt}[] points, float tension, FillMode fillmode)")
    w("    {")
    w("        ArgumentNullException.ThrowIfNull(points);")
    w("        DrawClosedCurve(pen, points.AsSpan(), tension, fillmode);")
    w("    }")
    w("")
    doc("Strokes a closed cardinal spline through points.", [PEN, *PTS, *TEN, ("fillmode", "Not used by a stroke.")])
    w(f"    public void DrawClosedCurve(Pen pen, ReadOnlySpan<{pt}> points, float tension, FillMode fillmode)")
    w("    {")
    w("        ArgumentNullException.ThrowIfNull(pen);")
    w("        GdiShapes s = new();")
    w("        s.Curve(Points(points), 0, 0, tension, closed: true);")
    w("        Stroke(pen, s.ToPath());")
    w("    }")
    w("")

    doc("Fills a closed cardinal spline through points, even-odd, tension 0.5.", [BRUSH, *PTS])
    w(f"    public void FillClosedCurve(Brush brush, params {pt}[] points) => FillClosedCurve(brush, points, FillMode.Alternate, 0.5f);")
    w("")
    doc("Fills a closed cardinal spline through points, even-odd, tension 0.5.", [BRUSH, *PTS])
    w(f"    public void FillClosedCurve(Brush brush, params ReadOnlySpan<{pt}> points) => FillClosedCurve(brush, points, FillMode.Alternate, 0.5f);")
    w("")
    fm = [("fillmode", "Alternate (even-odd) or Winding (nonzero).")]
    doc("Fills a closed cardinal spline through points, tension 0.5.", [BRUSH, *PTS, *fm])
    w(f"    public void FillClosedCurve(Brush brush, {pt}[] points, FillMode fillmode) => FillClosedCurve(brush, points, fillmode, 0.5f);")
    w("")
    doc("Fills a closed cardinal spline through points, tension 0.5.", [BRUSH, *PTS, *fm])
    w(f"    public void FillClosedCurve(Brush brush, ReadOnlySpan<{pt}> points, FillMode fillmode) => FillClosedCurve(brush, points, fillmode, 0.5f);")
    w("")
    doc("Fills a closed cardinal spline through points.", [BRUSH, *PTS, *fm, *TEN])
    w(f"    public void FillClosedCurve(Brush brush, {pt}[] points, FillMode fillmode, float tension)")
    w("    {")
    w("        ArgumentNullException.ThrowIfNull(points);")
    w("        FillClosedCurve(brush, points.AsSpan(), fillmode, tension);")
    w("    }")
    w("")
    doc("Fills a closed cardinal spline through points.", [BRUSH, *PTS, *fm, *TEN])
    w(f"    public void FillClosedCurve(Brush brush, ReadOnlySpan<{pt}> points, FillMode fillmode, float tension)")
    w("    {")
    w("        ArgumentNullException.ThrowIfNull(brush);")
    w("        GdiShapes s = new();")
    w("        s.Curve(Points(points), 0, 0, tension, closed: true);")
    w("        Fill(brush, s.ToPath(Rule(fillmode)));")
    w("    }")
    w("")

doc("Fills a region.", [BRUSH, ("region", "The region, in world coordinates.")])
w("    public void FillRegion(Brush brush, Region region)")
w("    {")
w("        ArgumentNullException.ThrowIfNull(region);")
w("        // An infinite region fills everything the clip lets through, as in GDI+: the bitmap, mapped back to world coordinates.")
w("        Fill(brush, region.Shape ?? DeviceInWorld);")
w("    }")
w("")

# ---- Visibility ----
doc("Gets whether a point is inside the bitmap and the clip.", [("x", "The x."), ("y", "The y.")], "    /// <returns><see langword=\"true\"/> when visible.</returns>")
w("    public bool IsVisible(float x, float y) => IsVisible(new PointF(x, y));")
w("")
doc("Gets whether a point is inside the bitmap and the clip.", [("x", "The x."), ("y", "The y.")], "    /// <returns><see langword=\"true\"/> when visible.</returns>")
w("    public bool IsVisible(int x, int y) => IsVisible(new PointF(x, y));")
w("")
doc("Gets whether a point is inside the bitmap and the clip.", [("point", "The point.")], "    /// <returns><see langword=\"true\"/> when visible.</returns>")
w("    public bool IsVisible(Point point) => IsVisible((PointF)point);")
w("")
doc("Gets whether any of a rectangle is inside the bitmap and the clip.", XYWH, "    /// <returns><see langword=\"true\"/> when visible.</returns>")
w("    public bool IsVisible(float x, float y, float width, float height) => IsVisible(new RectangleF(x, y, width, height));")
w("")
doc("Gets whether any of a rectangle is inside the bitmap and the clip.", XYWH, "    /// <returns><see langword=\"true\"/> when visible.</returns>")
w("    public bool IsVisible(int x, int y, int width, int height) => IsVisible(new RectangleF(x, y, width, height));")
w("")
doc("Gets whether any of a rectangle is inside the bitmap and the clip.", RECT, "    /// <returns><see langword=\"true\"/> when visible.</returns>")
w("    public bool IsVisible(Rectangle rect) => IsVisible((RectangleF)rect);")
w("")

# ---- Images ----
IMG = ("image", "The image.")
UNIT = ("srcUnit", "The unit of the source rectangle.")
for pt in ["PointF", "Point"]:
    doc("Draws an image at its physical size: its resolution against the graphics' resolution.", [IMG, ("point", "The top-left corner.")])
    w(f"    public void DrawImage(Image image, {pt} point) => DrawImageAt(image, point.X, point.Y);")
    w("")
for num in ["float", "int"]:
    doc("Draws an image at its physical size: its resolution against the graphics' resolution.", [IMG, ("x", "The left edge."), ("y", "The top edge.")])
    w(f"    public void DrawImage(Image image, {num} x, {num} y) => DrawImageAt(image, x, y);")
    w("")
for rt, num in [("RectangleF", "float"), ("Rectangle", "int")]:
    doc("Draws an image stretched into a rectangle.", [IMG, ("rect", "The rectangle.")])
    w(f"    public void DrawImage(Image image, {rt} rect) => DrawImageRect(image, rect);")
    w("")
    doc("Draws an image stretched into a rectangle.", [IMG, *XYWH])
    w(f"    public void DrawImage(Image image, {num} x, {num} y, {num} width, {num} height) => DrawImageRect(image, new RectangleF(x, y, width, height));")
    w("")
for pt in ["PointF", "Point"]:
    doc("Draws an image onto a parallelogram.", [IMG, ("destPoints", "Where the top-left, top-right and bottom-left corners go.")])
    w(f"    public void DrawImage(Image image, {pt}[] destPoints)")
    w("    {")
    w("        ArgumentNullException.ThrowIfNull(destPoints);")
    w("        DrawImagePoints(image, Corners(destPoints));")
    w("    }")
    w("")
for num, rt in [("float", "RectangleF"), ("int", "Rectangle")]:
    doc("Draws part of an image at its physical size.", [IMG, ("x", "The left edge."), ("y", "The top edge."), ("srcRect", "The part of the image."), UNIT])
    w(f"    public void DrawImage(Image image, {num} x, {num} y, {rt} srcRect, GraphicsUnit srcUnit) => DrawImageAt(image, x, y, srcRect, srcUnit);")
    w("")
for rt in ["RectangleF", "Rectangle"]:
    doc("Draws part of an image stretched into a rectangle.", [IMG, ("destRect", "The rectangle."), ("srcRect", "The part of the image."), UNIT])
    w(f"    public void DrawImage(Image image, {rt} destRect, {rt} srcRect, GraphicsUnit srcUnit) => DrawImageRect(image, destRect, srcRect, srcUnit);")
    w("")
ATTR = ("imageAttr", "Color adjustments and the wrap mode, or null.")
CB = ("callback", "Called while drawing; returning true stops it.")
CBD = ("callbackData", "Data for the callback.")
DP = ("destPoints", "Where the top-left, top-right and bottom-left corners go.")
for pt, rt in [("PointF", "RectangleF"), ("Point", "Rectangle")]:
    base = [IMG, DP, ("srcRect", "The part of the image."), UNIT]
    doc("Draws part of an image onto a parallelogram.", base)
    w(f"    public void DrawImage(Image image, {pt}[] destPoints, {rt} srcRect, GraphicsUnit srcUnit) => DrawImage(image, destPoints, srcRect, srcUnit, null, null, 0);")
    w("")
    doc("Draws part of an image onto a parallelogram.", [*base, ATTR])
    w(f"    public void DrawImage(Image image, {pt}[] destPoints, {rt} srcRect, GraphicsUnit srcUnit, ImageAttributes? imageAttr) => DrawImage(image, destPoints, srcRect, srcUnit, imageAttr, null, 0);")
    w("")
    doc("Draws part of an image onto a parallelogram.", [*base, ATTR, CB])
    w(f"    public void DrawImage(Image image, {pt}[] destPoints, {rt} srcRect, GraphicsUnit srcUnit, ImageAttributes? imageAttr, DrawImageAbort? callback) => DrawImage(image, destPoints, srcRect, srcUnit, imageAttr, callback, 0);")
    w("")
    doc("Draws part of an image onto a parallelogram.", [*base, ATTR, CB, CBD])
    w(f"    public void DrawImage(Image image, {pt}[] destPoints, {rt} srcRect, GraphicsUnit srcUnit, ImageAttributes? imageAttr, DrawImageAbort? callback, int callbackData)")
    w("    {")
    w("        ArgumentNullException.ThrowIfNull(destPoints);")
    w("        DrawImagePoints(image, Corners(destPoints), srcRect, srcUnit, imageAttr, callback, callbackData);")
    w("    }")
    w("")
for num in ["float", "int"]:
    srcp = [("srcX", "The source's left edge."), ("srcY", "The source's top edge."), ("srcWidth", "The source's width."), ("srcHeight", "The source's height.")]
    base = [IMG, ("destRect", "The rectangle."), *srcp, UNIT]
    an = "imageAttrs" if num == "float" else "imageAttr"
    attr = (an, ATTR[1])
    sig = f"Image image, Rectangle destRect, {num} srcX, {num} srcY, {num} srcWidth, {num} srcHeight, GraphicsUnit srcUnit"
    args = "image, destRect, srcX, srcY, srcWidth, srcHeight, srcUnit"
    doc("Draws part of an image stretched into a rectangle.", base)
    w(f"    public void DrawImage({sig}) => DrawImage({args}, null, null, IntPtr.Zero);")
    w("")
    doc("Draws part of an image stretched into a rectangle.", [*base, attr])
    w(f"    public void DrawImage({sig}, ImageAttributes? {an}) => DrawImage({args}, {an}, null, IntPtr.Zero);")
    w("")
    doc("Draws part of an image stretched into a rectangle.", [*base, attr, CB])
    w(f"    public void DrawImage({sig}, ImageAttributes? {an}, DrawImageAbort? callback) => DrawImage({args}, {an}, callback, IntPtr.Zero);")
    w("")
    doc("Draws part of an image stretched into a rectangle.", [*base, ("imageAttrs", ATTR[1]), CB, CBD])
    w(f"    public void DrawImage({sig}, ImageAttributes? imageAttrs, DrawImageAbort? callback, IntPtr callbackData) =>")
    w("        DrawImageRect(image, destRect, new RectangleF(srcX, srcY, srcWidth, srcHeight), srcUnit, imageAttrs, callback, callbackData);")
    w("")

doc("Draws an image at its physical size.", [IMG, ("point", "The top-left corner.")])
w("    public void DrawImageUnscaled(Image image, Point point) => DrawImage(image, point.X, point.Y);")
w("")
doc("Draws an image at its physical size.", [IMG, ("x", "The left edge."), ("y", "The top edge.")])
w("    public void DrawImageUnscaled(Image image, int x, int y) => DrawImage(image, x, y);")
w("")
doc("Draws an image at its physical size at a rectangle's corner; the size is not used.", [IMG, ("rect", "The rectangle.")])
w("    public void DrawImageUnscaled(Image image, Rectangle rect) => DrawImage(image, rect.X, rect.Y);")
w("")
doc("Draws an image at its physical size; the size is not used.", [IMG, *XYWH])
w("    public void DrawImageUnscaled(Image image, int x, int y, int width, int height) => DrawImage(image, x, y);")
w("")
doc("Draws an image pixel for pixel, cut to a rectangle.", [IMG, ("rect", "The rectangle.")])
w("    public void DrawImageUnscaledAndClipped(Image image, Rectangle rect)")
w("    {")
w("        ArgumentNullException.ThrowIfNull(image);")
w("        int width = Math.Min(rect.Width, image.Width), height = Math.Min(rect.Height, image.Height);")
w("        DrawImage(image, new Rectangle(rect.X, rect.Y, width, height), 0, 0, width, height, GraphicsUnit.Pixel);")
w("    }")
w("")
doc("Draws an icon at its size.", [("icon", "The icon."), ("x", "The left edge."), ("y", "The top edge.")])
w("    public void DrawIcon(Icon icon, int x, int y)")
w("    {")
w("        ArgumentNullException.ThrowIfNull(icon);")
w("        DrawIcon(icon, new Rectangle(x, y, icon.Width, icon.Height));")
w("    }")
w("")
doc("Draws an icon stretched into a rectangle.", [("icon", "The icon."), ("targetRect", "The rectangle.")])
w("    public void DrawIcon(Icon icon, Rectangle targetRect)")
w("    {")
w("        ArgumentNullException.ThrowIfNull(icon);")
w("        using Bitmap bitmap = icon.ToBitmap();")
w("        DrawImage(bitmap, targetRect);")
w("    }")
w("")
doc("Draws an icon at its own size at a rectangle's corner; as in GDI+, the rectangle's size is not used.", [("icon", "The icon."), ("targetRect", "The rectangle.")])
w("    public void DrawIconUnstretched(Icon icon, Rectangle targetRect)")
w("    {")
w("        ArgumentNullException.ThrowIfNull(icon);")
w("        using Bitmap bitmap = icon.ToBitmap();")
w("        DrawImage(bitmap, new Rectangle(targetRect.X, targetRect.Y, icon.Width, icon.Height));")
w("    }")
w("")

# ---- Helpers ----
w("    private static FillRule Rule(FillMode mode) => mode == FillMode.Winding ? FillRule.NonZero : FillRule.EvenOdd;")
w("")
for pt in ["PointF", "Point"]:
    w(f"    private static Vector2[] Points(ReadOnlySpan<{pt}> points)")
    w("    {")
    w("        Vector2[] p = new Vector2[points.Length];")
    w("        for (int i = 0; i < p.Length; i++)")
    w("        {")
    w("            p[i] = new Vector2(points[i].X, points[i].Y);")
    w("        }")
    w("")
    w("        return p;")
    w("    }")
    w("")
    w(f"    private static PointF[] Corners({pt}[] points) => points.Length == 3 ? [.. points.Select(p => new PointF(p.X, p.Y))] : throw GdiErrors.InvalidParameter();")
    w("")

while out[-1] == "":
    out.pop()

header = """// Written by tools/sysdrawing-graphics-overloads.py; each overload forwards to a core in Graphics.Drawing.cs.
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
"""
open(sys.argv[1], "w", encoding="utf-8", newline="\n").write(header + "\n".join(line.rstrip() for line in out) + "\n}\n")
