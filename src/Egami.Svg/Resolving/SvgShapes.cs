using System.Numerics;

using Egami.Drawing;
using Egami.Svg.Reading;
using Egami.Svg.Values;

namespace Egami.Svg.Resolving;

/// <summary>
/// The basic shapes as 2.6 paths, built the way SVG 2 section 10 defines their equivalent paths (start point and direction
/// matter for dashes and markers): rect from (x + rx, y) clockwise, circle and ellipse from (cx + r, cy) clockwise. A shape
/// with a zero or negative size, or a negative radius, draws nothing; it is reported as an error unless SVG says it is just
/// empty.
/// </summary>
internal static class SvgShapes
{
    /// <summary>Builds the path of a shape element, or <see langword="null"/> when it draws nothing.</summary>
    public static Path? Build(SvgElement element, SvgGeometry geometry, in SvgLengthContext context, long maxCommands, SvgReport report)
    {
        switch (element.Name)
        {
            case "rect":
                return Rect(element, geometry, context, report);
            case "circle":
            {
                float r = geometry.Length(element, "r", context, SvgAxis.Other, 0);
                if (r < 0)
                {
                    report.Warn("Svg.BadAttribute", $"The circle's r is negative ({r}); it is not drawn.", element);
                }

                return r > 0 ? Ellipse(geometry.Length(element, "cx", context, SvgAxis.X, 0), geometry.Length(element, "cy", context, SvgAxis.Y, 0), r, r) : null;
            }

            case "ellipse":
            {
                // A negative radius is an error that falls back to auto (SVG 2), as resvg reads it.
                float? rx = geometry.Auto(element, "rx", context, SvgAxis.X);
                float? ry = geometry.Auto(element, "ry", context, SvgAxis.Y);
                rx = rx < 0 ? null : rx;
                ry = ry < 0 ? null : ry;
                float x = rx ?? ry ?? 0, y = ry ?? rx ?? 0;

                return x > 0 && y > 0 ? Ellipse(geometry.Length(element, "cx", context, SvgAxis.X, 0), geometry.Length(element, "cy", context, SvgAxis.Y, 0), x, y) : null;
            }

            case "line":
                return new PathBuilder()
                    .MoveTo(geometry.Length(element, "x1", context, SvgAxis.X, 0), geometry.Length(element, "y1", context, SvgAxis.Y, 0))
                    .LineTo(geometry.Length(element, "x2", context, SvgAxis.X, 0), geometry.Length(element, "y2", context, SvgAxis.Y, 0))
                    .ToPath();
            case "polyline":
            case "polygon":
                return Poly(element, element.Name == "polygon", maxCommands, report);
            case "path":
                return PathData(element, geometry, maxCommands, report);
            default:
                return null;
        }
    }

    private static Path? Rect(SvgElement element, SvgGeometry geometry, in SvgLengthContext context, SvgReport report)
    {
        float x = geometry.Length(element, "x", context, SvgAxis.X, 0);
        float y = geometry.Length(element, "y", context, SvgAxis.Y, 0);
        float w = geometry.Length(element, "width", context, SvgAxis.X, 0);
        float h = geometry.Length(element, "height", context, SvgAxis.Y, 0);
        if (w < 0 || h < 0)
        {
            report.Warn("Svg.BadAttribute", "The rect has a negative width or height; it is not drawn.", element);
            return null;
        }

        if (w == 0 || h == 0)
        {
            return null;
        }

        float? rxAuto = geometry.Auto(element, "rx", context, SvgAxis.X);
        float? ryAuto = geometry.Auto(element, "ry", context, SvgAxis.Y);
        if (rxAuto < 0)
        {
            rxAuto = null;
        }

        if (ryAuto < 0)
        {
            ryAuto = null;
        }

        float rx = Math.Min(rxAuto ?? ryAuto ?? 0, w / 2);
        float ry = Math.Min(ryAuto ?? rxAuto ?? 0, h / 2);
        PathBuilder b = new();
        if (rx <= 0 || ry <= 0)
        {
            return b.MoveTo(x, y).LineTo(x + w, y).LineTo(x + w, y + h).LineTo(x, y + h).Close().ToPath();
        }

        Vector2 r = new(rx, ry);
        return b.MoveTo(x + rx, y)
            .LineTo(x + w - rx, y).ArcTo(r, 0, false, true, new Vector2(x + w, y + ry))
            .LineTo(x + w, y + h - ry).ArcTo(r, 0, false, true, new Vector2(x + w - rx, y + h))
            .LineTo(x + rx, y + h).ArcTo(r, 0, false, true, new Vector2(x, y + h - ry))
            .LineTo(x, y + ry).ArcTo(r, 0, false, true, new Vector2(x + rx, y))
            .Close().ToPath();
    }

    private static Path Ellipse(float cx, float cy, float rx, float ry)
    {
        Vector2 r = new(rx, ry);
        return new PathBuilder().MoveTo(cx + rx, cy)
            .ArcTo(r, 0, false, true, new Vector2(cx, cy + ry))
            .ArcTo(r, 0, false, true, new Vector2(cx - rx, cy))
            .ArcTo(r, 0, false, true, new Vector2(cx, cy - ry))
            .ArcTo(r, 0, false, true, new Vector2(cx + rx, cy))
            .Close().ToPath();
    }

    private static Path? Poly(SvgElement element, bool close, long maxCommands, SvgReport report)
    {
        string? text = element.Attributes["points"];
        if (text is null)
        {
            return null;
        }

        List<double> numbers = [];
        if (!SvgNumbers.TryParseList(text, numbers))
        {
            report.Warn("Svg.BadAttribute", $"The {element.Name}'s points have an error after {numbers.Count / 2} points; it is drawn up to there.", element);
        }

        int count = numbers.Count / 2;
        if (count < 2)
        {
            return null;
        }

        if (count > maxCommands)
        {
            throw new SvgException(element.Line, element.Column, $"The {element.Name} has more than {maxCommands:N0} points.");
        }

        PathBuilder b = new();
        b.MoveTo((float)numbers[0], (float)numbers[1]);
        for (int i = 1; i < count; i++)
        {
            b.LineTo((float)numbers[2 * i], (float)numbers[(2 * i) + 1]);
        }

        if (close)
        {
            b.Close();
        }

        return b.ToPath();
    }

    private static Path? PathData(SvgElement element, SvgGeometry geometry, long maxCommands, SvgReport report)
    {
        string? d = geometry.PathData(element);
        if (string.IsNullOrWhiteSpace(d) || d.Trim() == "none")
        {
            return null;
        }

        if (d.Length > maxCommands * 4)
        {
            throw new SvgException(element.Line, element.Column, $"The path data is longer than {maxCommands * 4:N0} characters.");
        }

        if (!Path.TryParseSvg(d, out Path? strict))
        {
            report.Warn("Svg.BadPathData", "The path data has an error; the path is drawn up to it.", element);
            strict = Path.ParseSvg(d, lenient: true);
        }

        return strict is null || strict.IsEmpty ? null : strict;
    }
}
