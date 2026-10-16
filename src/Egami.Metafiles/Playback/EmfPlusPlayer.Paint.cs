using System.Numerics;

using Egami.Drawing;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Metafiles.Playback;

/// <summary>What a fill draws: a paint, or for a path gradient a Gouraud mesh clipped to the shape.</summary>
internal readonly record struct PlusFill(Paint? Paint, Mesh? Mesh);

/// <summary>EMF+ brushes and pens as Egami paints and strokes.</summary>
internal sealed partial class EmfPlusPlayer
{
    /// <summary>The fill for a record's brush field: an ARGB color when the S flag (0x8000) is set, else a brush object's index.</summary>
    private PlusFill BrushPaint(ushort flags, uint value) =>
        (flags & ColorBrush) != 0 ? new PlusFill(Solid(PlusObjectReader.Argb(value)), null)
            : _objects[value & 0xFF] is PlusBrush brush ? BrushPaint(brush) : default;

    private SolidPaint Solid(Rgba8 color) => new(color) { Antialias = Smooth, BlendMode = Blend };

    /// <summary>CompositingModeSourceCopy (1) replaces what is below; SourceOver (0) blends.</summary>
    private BlendMode Blend => _state.Compositing == 1 ? BlendMode.Source : BlendMode.SourceOver;

    private PlusFill BrushPaint(PlusBrush brush)
    {
        Matrix3x2 local = brush.Transform * Full;
        switch (brush.Type)
        {
            case PlusBrush.SolidType:
                return new PlusFill(Solid(brush.Color), null);

            case PlusBrush.HatchType:
                {
                    // The tile is 8 reference pixels, as for GDI hatches (question 11), from the device origin; the scale is rounded to
                    // whole output pixels so the tile's pixels stay sharp, as GDI+ draws them.
                    byte[] rows = PlusHatches.Tiles[Math.Clamp(brush.HatchStyle, 0, PlusHatches.Tiles.Length - 1)];
                    PictureRecorder tile = new(new RectF(0, 0, 8, 8));
                    tile.FillRect(new RectF(0, 0, 8, 8), new SolidPaint(brush.BackColor) { Antialias = false, BlendMode = BlendMode.Source });
                    SolidPaint ink = new(brush.Color) { Antialias = false, BlendMode = BlendMode.Source };
                    for (int y = 0; y < 8; y++)
                    {
                        for (int x = 0; x < 8; x++)
                        {
                            if ((rows[y] & (0x80 >> x)) != 0)
                            {
                                tile.FillRect(new RectF(x, y, 1, 1), ink);
                            }
                        }
                    }

                    Matrix3x2 device = _gdi.Device;
                    float scale = MathF.Max(1, MathF.Round(MathF.Sqrt(MathF.Abs(device.GetDeterminant()))));
                    Matrix3x2 pixels = Matrix3x2.CreateScale(scale);
                    return new PlusFill(new PicturePaint(tile.Finish(), TileMode.Repeat, TileMode.Repeat, new RectF(0, 0, 8, 8)) { LocalMatrix = pixels, Antialias = Smooth, BlendMode = Blend }, null);
                }

            case PlusBrush.TextureType when brush.Texture?.Bitmap is Raster<Rgba8> image:
                {
                    (TileMode tx, TileMode ty) = Wrap(brush.WrapMode);
                    return new PlusFill(new ImagePaint(image, tx, ty, Sampling) { LocalMatrix = local, Antialias = Smooth, BlendMode = Blend }, null);
                }

            case PlusBrush.LinearGradientType:
                {
                    RectF r = brush.Rect;
                    GradientStop[] stops = brush.Stops ?? [new GradientStop(0, brush.Color), new GradientStop(1, brush.EndColor)];
                    if (brush.GammaCorrected)
                    {
                        stops = GammaStops(stops);
                    }

                    (TileMode tile, _) = Wrap(brush.WrapMode);
                    return new PlusFill(
                        new LinearGradient(new Vector2(r.X, r.Y), new Vector2(r.Right, r.Y), stops) { LocalMatrix = local, Tile = tile, Antialias = Smooth, BlendMode = Blend },
                        null);
                }

            case PlusBrush.PathGradientType:
                return new PlusFill(null, PathGradient(brush, local));

            default:
                return new PlusFill(Solid(brush.Color), null);
        }
    }

    /// <summary>
    /// A gamma-corrected gradient (GDI+ blends in linear light, gamma 2.2): each segment gets stops in between whose colors are
    /// mixed in linear light, so the encoded-space gradient between them follows the same curve closely.
    /// </summary>
    private static GradientStop[] GammaStops(GradientStop[] stops)
    {
        List<GradientStop> result = [];
        for (int i = 0; i < stops.Length; i++)
        {
            result.Add(stops[i]);
            if (i + 1 < stops.Length)
            {
                GradientStop a = stops[i], b = stops[i + 1];
                for (int k = 1; k < 8; k++)
                {
                    float t = k / 8f;
                    result.Add(new GradientStop(a.Offset + ((b.Offset - a.Offset) * t), LinearMix(a.Color, b.Color, t)));
                }
            }
        }

        return [.. result];
    }

    private static RgbaF32 LinearMix(RgbaF32 a, RgbaF32 b, float t)
    {
        static float L(float c) => MathF.Pow(Math.Clamp(c, 0, 1), 2.2f);
        static float E(float c) => MathF.Pow(Math.Clamp(c, 0, 1), 1 / 2.2f);
        return new RgbaF32(E(L(a.R) + ((L(b.R) - L(a.R)) * t)), E(L(a.G) + ((L(b.G) - L(a.G)) * t)), E(L(a.B) + ((L(b.B) - L(a.B)) * t)), a.A + ((b.A - a.A) * t));
    }

    /// <summary>GDI+ wrap modes: 0 Tile, 1 TileFlipX, 2 TileFlipY, 3 TileFlipXY, 4 Clamp.</summary>
    private static (TileMode X, TileMode Y) Wrap(int mode) => mode switch
    {
        1 => (TileMode.Mirror, TileMode.Repeat),
        2 => (TileMode.Repeat, TileMode.Mirror),
        3 => (TileMode.Mirror, TileMode.Mirror),
        4 => (TileMode.Decal, TileMode.Decal),
        _ => (TileMode.Repeat, TileMode.Repeat),
    };

    /// <summary>Nearest neighbour for GDI+'s NearestNeighbor interpolation (5), smooth otherwise.</summary>
    private SamplingOptions Sampling => _state.Interpolation == 5 ? new SamplingOptions { Filter = ResampleFilter.Nearest } : new SamplingOptions { Filter = ResampleFilter.Bilinear };

    /// <summary>
    /// A path gradient as a Gouraud mesh in device space (design, stage C): rings from the boundary (position 0) to the center
    /// (position 1) at the blend's positions, each boundary point with its surrounding color, the center with its own.
    /// </summary>
    private static Mesh? PathGradient(PlusBrush brush, Matrix3x2 local)
    {
        Vector2[] boundary = brush.BoundaryPoints ?? Flatten(brush.Boundary);
        if (boundary.Length < 2)
        {
            return null;
        }

        Rgba8 center = brush.Color;
        Rgba8 Surround(int i) => brush.Surround.Length == 0 ? center : brush.Surround[Math.Min(i, brush.Surround.Length - 1)];

        // The rings' positions and how far each is from the surround color (0) to the center color (1).
        (float Position, float Mix)[] rings = brush.Stops is { Length: > 1 } stops
            ? [.. stops.Select(s => (s.Offset, Mix(s.Color.ToRgba8(), Surround(0), center))).OrderBy(r => r.Offset)]
            : [(0f, 0f), (1f, 1f)];
        if (rings[0].Position > 0)
        {
            rings = [(0f, rings[0].Mix), .. rings];
        }

        List<Vector2> positions = [];
        List<RgbaF32> colors = [];
        List<int> indices = [];
        int n = boundary.Length;
        Vector2 c = brush.Center;
        for (int ring = 0; ring < rings.Length; ring++)
        {
            (float p, float mix) = rings[ring];
            for (int i = 0; i < n; i++)
            {
                Vector2 point = boundary[i] + ((c - boundary[i]) * p);
                positions.Add(Vector2.Transform(point, local));
                colors.Add(RgbaF32.FromRgba8(PlusObjectReader.Lerp(Surround(i), center, mix)));
            }

            if (ring > 0)
            {
                int a = (ring - 1) * n, b = ring * n;
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    indices.AddRange([a + i, a + j, b + i, a + j, b + j, b + i]);
                }
            }
        }

        // The last ring meets the center.
        int centerIndex = positions.Count;
        positions.Add(Vector2.Transform(c, local));
        colors.Add(RgbaF32.FromRgba8(PlusObjectReader.Lerp(Surround(0), center, rings[^1].Mix)));
        int last = (rings.Length - 1) * n;
        for (int i = 0; i < n; i++)
        {
            indices.AddRange([last + i, last + ((i + 1) % n), centerIndex]);
        }

        return new Mesh(MeshMode.Triangles, positions.ToArray(), colors.ToArray(), indices: indices.ToArray());
    }

    /// <summary>How far a color lies from one color to another, by its largest channel difference.</summary>
    private static float Mix(Rgba8 c, Rgba8 from, Rgba8 to)
    {
        float best = 0, span = 0;
        foreach ((int a, int b, int x) in new[] { (from.R, to.R, c.R), (from.G, to.G, c.G), (from.B, to.B, c.B), (from.A, to.A, c.A) })
        {
            if (Math.Abs(b - a) > span)
            {
                span = Math.Abs(b - a);
                best = (x - a) / (float)(b - a);
            }
        }

        return span == 0 ? 1 : Math.Clamp(best, 0, 1);
    }

    /// <summary>A path's outline as points (curves flattened by their control polygon's subdivision).</summary>
    private static Vector2[] Flatten(Path? path)
    {
        if (path is null)
        {
            return [];
        }

        List<Vector2> points = [];
        ReadOnlySpan<Vector2> p = path.Points;
        int at = 0;
        foreach (PathVerb verb in path.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                case PathVerb.Line:
                    points.Add(p[at++]);
                    break;
                case PathVerb.Quad:
                    {
                        Vector2 a = points.Count > 0 ? points[^1] : p[at];
                        for (int i = 1; i <= 8; i++)
                        {
                            float t = i / 8f;
                            points.Add(((1 - t) * (1 - t) * a) + (2 * (1 - t) * t * p[at]) + (t * t * p[at + 1]));
                        }

                        at += 2;
                        break;
                    }

                case PathVerb.Cubic:
                    {
                        Vector2 a = points.Count > 0 ? points[^1] : p[at];
                        for (int i = 1; i <= 12; i++)
                        {
                            float t = i / 12f, u = 1 - t;
                            points.Add((u * u * u * a) + (3 * u * u * t * p[at]) + (3 * u * t * t * p[at + 1]) + (t * t * t * p[at + 2]));
                        }

                        at += 3;
                        break;
                    }
            }
        }

        return [.. points];
    }

    // ---- Pens ----

    /// <summary>
    /// Strokes a world-space path with a pen: its width in world units (at least one device pixel), caps, join, miter limit,
    /// dashes in multiples of the width, inset alignment for closed figures, compound lines as strips, and anchor and custom caps
    /// drawn at the ends. The stroke is computed in world space and filled in device space, so it follows the world transform.
    /// </summary>
    private void Stroke(Path world, PlusPen? pen)
    {
        if (pen is null || world.IsEmpty)
        {
            return;
        }

        if (!GdiPlayer.Sane(world.Transform(Full)))
        {
            WarnOnce("Metafile.BadRecord", "A shape lies more than a million pixels away; it is not drawn.", "insane");
            return;
        }

        Matrix3x2 full = Full;
        float scale = MathF.Sqrt(MathF.Abs((full.M11 * full.M22) - (full.M12 * full.M21)));
        float unit = pen.Unit is 0 or 2 or 1 ? 1 : UnitScale(pen.Unit) / UnitScale(_state.PageUnit) / _state.PageScale;
        float width = pen.Width * unit * MathF.Sqrt(MathF.Abs((pen.Transform.M11 * pen.Transform.M22) - (pen.Transform.M12 * pen.Transform.M21)));
        if (width * scale < 1 && scale > 0)
        {
            width = 1 / scale; // GDI+ never draws a pen thinner than a pixel
        }

        float[]? dashes = pen.Dashes is { Length: > 0 } custom ? custom : pen.LineStyle switch
        {
            1 => [3, 1],
            2 => [1, 1],
            3 => [3, 1, 1, 1],
            4 => [3, 1, 1, 1, 1, 1],
            5 when pen.Dashes is { Length: > 0 } d => d,
            _ => null,
        };
        if (dashes is not null && dashes.Length % 2 != 0)
        {
            dashes = [.. dashes, .. dashes];
        }

        LineCap cap = (pen.LineStyle != 0 && dashes is not null ? pen.DashCap : pen.StartCap) switch
        {
            1 or 0x11 => LineCap.Square,
            2 or 0x12 => LineCap.Round,
            _ => LineCap.Butt,
        };
        Stroke stroke = new(width)
        {
            Cap = cap,
            Join = pen.Join switch { 1 => LineJoin.Bevel, 2 => LineJoin.Round, _ => LineJoin.Miter },
            MiterLimit = Math.Max(1, pen.MiterLimit),
            Dash = dashes is null ? null : new DashPattern([.. dashes.Select(v => Math.Max(v * width, 1e-3f))], pen.DashOffset * width),
        };

        PlusFill fill = BrushPaint(pen.Brush);
        Paint paint = fill.Paint ?? Solid(pen.Brush.Color);
        bool closed = world.Verbs.Contains(PathVerb.Close);
        int count = _canvas.Save();
        try
        {
            Path outline;
            if (pen.Compound is { Length: >= 2 } compound)
            {
                outline = Compound(world, stroke, compound);
            }
            else if (pen.Alignment == 1 && closed)
            {
                // Inset: the pen lies inside the figure, so twice the width clipped to the figure.
                _canvas.ClipPath(world.Transform(full), ClipOp.Intersect, antialias: Smooth);
                outline = (stroke with { Width = width * 2 }).ToPath(world);
            }
            else
            {
                outline = stroke.ToPath(world);
            }

            _canvas.FillPath(outline.WithFillRule(FillRule.NonZero).Transform(full), paint);
            Anchors(world, pen, width, paint, full);
        }
        finally
        {
            _canvas.RestoreToCount(count);
        }
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

    /// <summary>The anchor caps (square, round, diamond, arrow) and custom caps at the open ends of the path.</summary>
    private void Anchors(Path world, PlusPen pen, float width, Paint paint, Matrix3x2 full)
    {
        ReadOnlySpan<Vector2> p = world.Points;
        if (p.Length < 2 || world.Verbs.Contains(PathVerb.Close))
        {
            return;
        }

        Cap(pen.StartCap, pen.CustomStartCap, p[0], p[1], width, paint, full);
        Cap(pen.EndCap, pen.CustomEndCap, p[^1], p[^2], width, paint, full);
    }

    private void Cap(int kind, PlusCap? custom, Vector2 end, Vector2 toward, float width, Paint paint, Matrix3x2 full)
    {
        Vector2 dir = end - toward;
        if (dir.LengthSquared() == 0)
        {
            return;
        }

        dir = Vector2.Normalize(dir);
        Vector2 normal = new(-dir.Y, dir.X);
        float size = width * 2;
        Path? shape = kind switch
        {
            0x11 => Path.Polygon([end + ((normal + dir) * size / 2), end + ((normal - dir) * size / 2), end - ((normal + dir) * size / 2), end - ((normal - dir) * size / 2)]),
            0x12 => Path.Circle(end, size / 2),
            0x13 => Path.Polygon([end + (dir * size / 2), end + (normal * size / 2), end - (dir * size / 2), end - (normal * size / 2)]),
            0x14 => Path.Polygon([end + (dir * size), end + (normal * size / 2), end - (normal * size / 2)]),
            _ => null,
        };
        if (kind == 0xFF && custom is { Arrow: true } arrow)
        {
            // An adjustable arrow: its width and height in pen widths, the tip at the end of the line.
            float w = arrow.Width * width / 2, h = arrow.Height * width;
            Vector2 tip = end, baseCenter = end - (dir * h), inset = baseCenter + (dir * arrow.MiddleInset * width);
            Path head = Path.Polygon([tip, baseCenter + (normal * w), inset, baseCenter - (normal * w)]);
            shape = arrow.Filled ? head : new Stroke(width).ToPath(Path.Polygon([baseCenter + (normal * w), tip, baseCenter - (normal * w)], close: false));
        }

        if (shape is not null)
        {
            _canvas.FillPath(shape.Transform(full), paint);
        }
    }
}
