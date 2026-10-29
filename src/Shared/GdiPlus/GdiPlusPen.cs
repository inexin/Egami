using System.Numerics;

using Egami.Drawing;

namespace Egami.GdiPlus;

/// <summary>
/// A GDI+ pen's geometry, as [MS-EMFPLUS] 2.2.2.32 serializes it and System.Drawing's Pen exposes it: width and unit, caps,
/// join, dashes, alignment, compound lines and custom caps. The brush it paints with is the caller's.
/// </summary>
internal sealed class GdiPlusPen
{
    /// <summary>LineCapFlat.</summary>
    public const int CapFlat = 0;

    /// <summary>LineCapSquare.</summary>
    public const int CapSquare = 1;

    /// <summary>LineCapRound.</summary>
    public const int CapRound = 2;

    /// <summary>LineCapTriangle.</summary>
    public const int CapTriangle = 3;

    /// <summary>LineCapSquareAnchor.</summary>
    public const int CapSquareAnchor = 0x11;

    /// <summary>LineCapRoundAnchor.</summary>
    public const int CapRoundAnchor = 0x12;

    /// <summary>LineCapDiamondAnchor.</summary>
    public const int CapDiamondAnchor = 0x13;

    /// <summary>LineCapArrowAnchor.</summary>
    public const int CapArrowAnchor = 0x14;

    /// <summary>LineCapCustom: the custom cap draws the end.</summary>
    public const int CapCustom = 0xFF;

    /// <summary>LineJoinMiter.</summary>
    public const int JoinMiter = 0;

    /// <summary>LineJoinBevel.</summary>
    public const int JoinBevel = 1;

    /// <summary>LineJoinRound.</summary>
    public const int JoinRound = 2;

    /// <summary>LineJoinMiterClipped.</summary>
    public const int JoinMiterClipped = 3;

    /// <summary>DashStyleSolid.</summary>
    public const int DashSolid = 0;

    /// <summary>DashStyleCustom: <see cref="Dashes"/> holds the pattern.</summary>
    public const int DashCustom = 5;

    /// <summary>PenAlignmentCenter.</summary>
    public const int AlignCenter = 0;

    /// <summary>PenAlignmentInset.</summary>
    public const int AlignInset = 1;

    public float Width { get; init; } = 1;

    /// <summary>Gets the unit of <see cref="Width"/>, a GDI+ unit (0 world, 1 display, 2 pixel, 3 point, 4 inch, 5 document, 6 millimeter).</summary>
    public int Unit { get; init; }

    public Matrix3x2 Transform { get; init; } = Matrix3x2.Identity;

    public int StartCap { get; init; }

    public int EndCap { get; init; }

    public int Join { get; init; }

    public float MiterLimit { get; init; } = 10;

    /// <summary>Gets the dash style: 0 solid, 1 dash, 2 dot, 3 dash-dot, 4 dash-dot-dot, 5 custom.</summary>
    public int DashStyle { get; init; }

    /// <summary>Gets the dash cap: 0 flat, 2 round, 3 triangle (drawn flat).</summary>
    public int DashCap { get; init; }

    /// <summary>Gets the dash offset, in multiples of the width.</summary>
    public float DashOffset { get; init; }

    /// <summary>Gets a custom dash pattern, in multiples of the width.</summary>
    public float[]? Dashes { get; init; }

    public int Alignment { get; init; }

    /// <summary>Gets the compound line positions across the width, 0 to 1 in pairs.</summary>
    public float[]? Compound { get; init; }

    public GdiPlusCustomCap? CustomStartCap { get; init; }

    public GdiPlusCustomCap? CustomEndCap { get; init; }
}

/// <summary>A custom line cap ([MS-EMFPLUS] 2.2.2.11): an adjustable arrow, or a path to fill or stroke.</summary>
internal sealed class GdiPlusCustomCap
{
    /// <summary>Gets a value indicating whether the cap is an adjustable arrow.</summary>
    public bool Arrow { get; init; }

    public float Width { get; init; }

    public float Height { get; init; }

    public float MiddleInset { get; init; }

    public bool Filled { get; init; }

    /// <summary>Gets a path cap's outline, in units of the pen width, pointing up the y axis.</summary>
    public Path? Shape { get; init; }

    public float BaseInset { get; init; }

    /// <summary>
    /// Gets a value indicating whether <see cref="Shape"/> is drawn at the line's end, its x axis across the line and its y axis
    /// along it, outward, in pen widths times <see cref="WidthScale"/>. Off for metafiles, which do not draw path caps.
    /// </summary>
    public bool DrawShape { get; init; }

    /// <summary>Gets how much the pen width scales the cap.</summary>
    public float WidthScale { get; init; } = 1;
}

/// <summary>What a pen draws along a path: the outline to fill, the clip an inset pen needs, and the caps at the open ends.</summary>
/// <param name="Outline">The stroke's outline, filled nonzero.</param>
/// <param name="Clip">The figure an inset pen is clipped to, or null.</param>
/// <param name="Caps">The anchor and custom caps, filled after the outline under the same clip.</param>
internal readonly record struct GdiPlusPenOutline(Path Outline, Path? Clip, IReadOnlyList<Path> Caps);

/// <summary>GDI+ pen semantics in world space: dash patterns scaled by the width, dash caps, compound lines, inset alignment and caps.</summary>
internal static class GdiPlusPens
{
    /// <summary>A width in world units that is at least one device pixel, as GDI+ never draws a pen thinner than a pixel.</summary>
    /// <param name="width">The pen's width in world units.</param>
    /// <param name="scale">Device pixels per world unit.</param>
    public static float DeviceWidth(float width, float scale) => width * scale < 1 && scale > 0 ? 1 / scale : width;

    /// <summary>The dash pattern in multiples of the width, an odd custom pattern repeated to an even length; null when solid.</summary>
    public static float[]? DashArray(GdiPlusPen pen)
    {
        float[]? dashes = pen.Dashes is { Length: > 0 } custom ? custom : pen.DashStyle switch
        {
            1 => [3, 1],
            2 => [1, 1],
            3 => [3, 1, 1, 1],
            4 => [3, 1, 1, 1, 1, 1],
            _ => null,
        };
        if (dashes is not null && dashes.Length % 2 != 0)
        {
            dashes = [.. dashes, .. dashes];
        }

        return dashes;
    }

    /// <summary>The Egami stroke for a pen at a width in world units.</summary>
    public static Stroke ToStroke(GdiPlusPen pen, float width)
    {
        float[]? dashes = DashArray(pen);
        LineCap cap = (dashes is not null ? pen.DashCap : pen.StartCap) switch
        {
            GdiPlusPen.CapSquare or GdiPlusPen.CapSquareAnchor => LineCap.Square,
            GdiPlusPen.CapRound or GdiPlusPen.CapRoundAnchor => LineCap.Round,
            _ => LineCap.Butt,
        };
        return new Stroke(width)
        {
            Cap = cap,
            Join = pen.Join switch { GdiPlusPen.JoinBevel => LineJoin.Bevel, GdiPlusPen.JoinRound => LineJoin.Round, _ => LineJoin.Miter },
            MiterLimit = Math.Max(1, pen.MiterLimit),
            Dash = dashes is null ? null : new DashPattern([.. dashes.Select((v, i) => DashLength(v * width, i, cap, width))], pen.DashOffset * width),
        };
    }

    /// <summary>GDI+ keeps a round or square dash cap inside the dash, so the dash shortens and the gap after it grows by the width.</summary>
    private static float DashLength(float length, int index, LineCap cap, float width)
    {
        float extra = cap == LineCap.Butt ? 0 : width;
        return Math.Max(index % 2 == 0 ? length - extra : length + extra, 1e-3f);
    }

    /// <summary>
    /// What a pen draws along a world-space path at a width in world units: compound lines as strips, an inset pen on a closed
    /// figure as twice the width clipped to the figure, else the plain stroke; and the anchor and custom caps at the open ends.
    /// </summary>
    public static GdiPlusPenOutline Outline(Path world, GdiPlusPen pen, float width)
    {
        Stroke stroke = ToStroke(pen, width);
        bool closed = world.Verbs.Contains(PathVerb.Close);
        Path outline;
        Path? clip = null;
        if (pen.Compound is { Length: >= 2 } compound)
        {
            outline = Compound(world, stroke, compound);
        }
        else if (pen.Alignment == GdiPlusPen.AlignInset && closed)
        {
            // Inset: the pen lies inside the figure, so twice the width clipped to the figure.
            clip = world;
            outline = (stroke with { Width = width * 2 }).ToPath(world);
        }
        else
        {
            outline = stroke.ToPath(world);
        }

        return new GdiPlusPenOutline(outline, clip, Caps(world, pen, width));
    }

    /// <summary>A compound pen: the strips between pairs of positions across the width, as outlines of concentric strokes.</summary>
    private static Path Compound(Path world, Stroke stroke, float[] positions)
    {
        List<Path> strips = [];
        for (int i = 0; i + 1 < positions.Length; i += 2)
        {
            float a = (positions[i] - 0.5f) * stroke.Width, b = (positions[i + 1] - 0.5f) * stroke.Width;
            float outer = Math.Max(Math.Abs(a), Math.Abs(b)) * 2, inner = a < 0 && b > 0 ? 0 : Math.Min(Math.Abs(a), Math.Abs(b)) * 2;
            Path strip = (stroke with { Width = Math.Max(outer, 1e-3f), Dash = null }).ToPath(world);
            if (inner > 1e-3f)
            {
                strip = strip.Difference((stroke with { Width = inner, Dash = null }).ToPath(world));
            }

            strips.Add(strip);
        }

        return strips.Count == 1 ? strips[0] : Path.Combine(strips, PathOp.Union);
    }

    /// <summary>The anchor caps (square, round, diamond, arrow) and custom caps at the open ends of the path, start first.</summary>
    private static List<Path> Caps(Path world, GdiPlusPen pen, float width)
    {
        List<Path> caps = [];
        ReadOnlySpan<Vector2> p = world.Points;
        if (p.Length < 2 || world.Verbs.Contains(PathVerb.Close))
        {
            return caps;
        }

        if (Cap(pen.StartCap, pen.CustomStartCap, p[0], p[1], width) is Path start)
        {
            caps.Add(start);
        }

        if (Cap(pen.EndCap, pen.CustomEndCap, p[^1], p[^2], width) is Path end)
        {
            caps.Add(end);
        }

        return caps;
    }

    private static Path? Cap(int kind, GdiPlusCustomCap? custom, Vector2 end, Vector2 toward, float width)
    {
        Vector2 dir = end - toward;
        if (dir.LengthSquared() == 0)
        {
            return null;
        }

        dir = Vector2.Normalize(dir);
        Vector2 normal = new(-dir.Y, dir.X);
        float size = width * 2;
        Path? shape = kind switch
        {
            GdiPlusPen.CapSquareAnchor => Path.Polygon([end + ((normal + dir) * size / 2), end + ((normal - dir) * size / 2), end - ((normal + dir) * size / 2), end - ((normal - dir) * size / 2)]),
            GdiPlusPen.CapRoundAnchor => Path.Circle(end, size / 2),
            GdiPlusPen.CapDiamondAnchor => Path.Polygon([end + (dir * size / 2), end + (normal * size / 2), end - (dir * size / 2), end - (normal * size / 2)]),
            GdiPlusPen.CapArrowAnchor => Path.Polygon([end, end - (dir * size) + (normal * size / 2), end - (dir * size) - (normal * size / 2)]),
            _ => null,
        };
        if (kind == GdiPlusPen.CapCustom && custom is { Arrow: true } arrow)
        {
            // An adjustable arrow: its width and height in pen widths, the tip at the end of the line.
            float w = arrow.Width * width / 2, h = arrow.Height * width;
            Vector2 tip = end, baseCenter = end - (dir * h), inset = baseCenter + (dir * arrow.MiddleInset * width);
            Path head = Path.Polygon([tip, baseCenter + (normal * w), inset, baseCenter - (normal * w)]);
            shape = arrow.Filled ? head : new Stroke(width).ToPath(Path.Polygon([baseCenter + (normal * w), tip, baseCenter - (normal * w)], close: false));
        }

        if (kind == GdiPlusPen.CapCustom && custom is { Arrow: false, DrawShape: true, Shape: Path cap })
        {
            float scale = width * custom.WidthScale;
            Matrix3x2 place = new(normal.X * scale, normal.Y * scale, dir.X * scale, dir.Y * scale, end.X, end.Y);
            shape = cap.Transform(place).WithFillRule(FillRule.NonZero);
        }

        return shape;
    }
}
