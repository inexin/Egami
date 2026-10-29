using System.Numerics;

using Egami.Drawing;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.GdiPlus;

/// <summary>
/// GDI+ brush semantics: hatch tiles, wrap modes, blend factors and preset colors as gradient stops, gamma-corrected blending,
/// and path gradients as Gouraud meshes.
/// </summary>
internal static class GdiPlusBrushes
{
    /// <summary>WrapModeTile.</summary>
    public const int WrapTile = 0;

    /// <summary>WrapModeTileFlipX.</summary>
    public const int WrapTileFlipX = 1;

    /// <summary>WrapModeTileFlipY.</summary>
    public const int WrapTileFlipY = 2;

    /// <summary>WrapModeTileFlipXY.</summary>
    public const int WrapTileFlipXY = 3;

    /// <summary>WrapModeClamp.</summary>
    public const int WrapClamp = 4;

    /// <summary>GDI+ wrap modes as Egami tile modes: 0 Tile, 1 TileFlipX, 2 TileFlipY, 3 TileFlipXY, 4 Clamp.</summary>
    public static (TileMode X, TileMode Y) Wrap(int mode) => mode switch
    {
        WrapTileFlipX => (TileMode.Mirror, TileMode.Repeat),
        WrapTileFlipY => (TileMode.Repeat, TileMode.Mirror),
        WrapTileFlipXY => (TileMode.Mirror, TileMode.Mirror),
        WrapClamp => (TileMode.Decal, TileMode.Decal),
        _ => (TileMode.Repeat, TileMode.Repeat),
    };

    /// <summary>
    /// A hatch style's 8 by 8 tile, in reference pixels: the back color everywhere and the fore color on the style's set bits,
    /// both copied rather than blended. Styles out of range clamp to the nearest. With <paramref name="smoothDiagonals"/>, the
    /// forward, backward and crossed diagonals (styles 2, 3 and 5) are drawn as GDI+ 1.1 draws them on screen and on bitmaps:
    /// 234/255 of the fore color on the diagonal and 64/255 beside it, left and right.
    /// </summary>
    public static Picture HatchTile(int style, Rgba8 fore, Rgba8 back, bool smoothDiagonals = false)
    {
        style = Math.Clamp(style, 0, GdiPlusHatches.Tiles.Length - 1);
        if (smoothDiagonals && style is 2 or 3 or 5)
        {
            return SmoothDiagonals(style, fore, back);
        }

        byte[] rows = GdiPlusHatches.Tiles[style];
        PictureRecorder tile = new(new RectF(0, 0, 8, 8));
        tile.FillRect(new RectF(0, 0, 8, 8), new SolidPaint(back) { Antialias = false, BlendMode = BlendMode.Source });
        SolidPaint ink = new(fore) { Antialias = false, BlendMode = BlendMode.Source };
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

        return tile.Finish();
    }

    private static Picture SmoothDiagonals(int style, Rgba8 fore, Rgba8 back)
    {
        PictureRecorder tile = new(new RectF(0, 0, 8, 8));
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int coverage = 0;
                foreach (int center in (int[])(style switch { 2 => [y], 3 => [7 - y], _ => [y, 7 - y] }))
                {
                    int d = ((x - center) % 8 + 8) % 8;
                    coverage = Math.Max(coverage, d == 0 ? 234 : d is 1 or 7 ? 64 : 0);
                }

                tile.FillRect(new RectF(x, y, 1, 1), new SolidPaint(Lerp(back, fore, coverage / 255f)) { Antialias = false, BlendMode = BlendMode.Source });
            }
        }

        return tile.Finish();
    }

    /// <summary>
    /// The scale of a hatch tile under a device transform: the tile is 8 reference pixels from the device origin, rounded to
    /// whole output pixels so its pixels stay sharp, as GDI+ draws them.
    /// </summary>
    public static float HatchScale(Matrix3x2 device) => MathF.Max(1, MathF.Round(MathF.Sqrt(MathF.Abs(device.GetDeterminant()))));

    /// <summary>A preset color stop (EmfPlusBlendColors, InterpolationColors): its position clamped to 0 to 1.</summary>
    public static GradientStop PresetStop(float position, Rgba8 color) => new(Math.Clamp(position, 0, 1), color);

    /// <summary>
    /// A blend factor stop (EmfPlusBlendFactors, Blend): its position and its factor from the start color (0) to the end color
    /// (1), each clamped to 0 to 1; a gamma-corrected brush mixes the two colors in linear light.
    /// </summary>
    public static GradientStop BlendStop(float position, float factor, Rgba8 start, Rgba8 end, bool gamma)
    {
        float t = Math.Clamp(factor, 0, 1);
        return new GradientStop(Math.Clamp(position, 0, 1), gamma ? LinearLerp(start, end, t) : Lerp(start, end, t));
    }

    private static Rgba8 LinearLerp(Rgba8 a, Rgba8 b, float t)
    {
        static byte Mix(byte x, byte y, float t) =>
            (byte)((MathF.Pow((MathF.Pow(x / 255f, 2.2f) * (1 - t)) + (MathF.Pow(y / 255f, 2.2f) * t), 1 / 2.2f) * 255) + 0.5f);
        return new(Mix(a.R, b.R, t), Mix(a.G, b.G, t), Mix(a.B, b.B, t), (byte)(a.A + ((b.A - a.A) * t) + 0.5f));
    }

    /// <summary>Mixes two 8-bit colors channel by channel, rounding to nearest.</summary>
    public static Rgba8 Lerp(Rgba8 a, Rgba8 b, float t) =>
        new((byte)(a.R + ((b.R - a.R) * t) + 0.5f), (byte)(a.G + ((b.G - a.G) * t) + 0.5f), (byte)(a.B + ((b.B - a.B) * t) + 0.5f), (byte)(a.A + ((b.A - a.A) * t) + 0.5f));

    /// <summary>
    /// A gamma-corrected gradient (GDI+ blends in linear light, gamma 2.2): each segment gets stops in between whose colors are
    /// mixed in linear light, so the encoded-space gradient between them follows the same curve closely.
    /// </summary>
    public static GradientStop[] GammaStops(GradientStop[] stops)
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

    /// <summary>
    /// A path gradient as a Gouraud mesh in device space: rings from the boundary (position 0) to the center (position 1) at the
    /// blend's positions, each boundary point with its surrounding color, the center with its own. Null when the boundary has
    /// fewer than two points.
    /// </summary>
    /// <param name="boundary">The boundary points in world units.</param>
    /// <param name="center">The center point in world units.</param>
    /// <param name="centerColor">The center color.</param>
    /// <param name="surround">The surrounding colors, one per boundary point; the last repeats, and none means the center color.</param>
    /// <param name="stops">The stops from the boundary (0) to the center (1), from blend factors or preset colors, or null.</param>
    /// <param name="local">The brush transform times the world-to-device transform.</param>
    /// <param name="presetColors">Whether the stops' own colors are the rings' colors (preset colors), rather than mixes from the surround to the center.</param>
    public static Mesh? PathGradient(Vector2[] boundary, Vector2 center, Rgba8 centerColor, Rgba8[] surround, GradientStop[]? stops, Matrix3x2 local, bool presetColors = false)
    {
        if (presetColors && stops is { Length: > 1 })
        {
            Rgba8[] ringColors = [.. stops.OrderBy(s => s.Offset).Select(s => s.Color.ToRgba8())];
            float[] ringPositions = [.. stops.OrderBy(s => s.Offset).Select(s => s.Offset)];
            return Rings(boundary, center, local, ringPositions, (ring, _) => ringColors[ring], ringColors[^1]);
        }

        if (boundary.Length < 2)
        {
            return null;
        }

        Rgba8 Surround(int i) => surround.Length == 0 ? centerColor : surround[Math.Min(i, surround.Length - 1)];

        // The rings' positions and how far each is from the surround color (0) to the center color (1).
        (float Position, float Mix)[] rings = stops is { Length: > 1 }
            ? [.. stops.Select(s => (s.Offset, Mix(s.Color.ToRgba8(), Surround(0), centerColor))).OrderBy(r => r.Offset)]
            : [(0f, 0f), (1f, 1f)];
        if (rings[0].Position > 0)
        {
            rings = [(0f, rings[0].Mix), .. rings];
        }

        List<Vector2> positions = [];
        List<RgbaF32> colors = [];
        List<int> indices = [];
        int n = boundary.Length;
        Vector2 c = center;
        for (int ring = 0; ring < rings.Length; ring++)
        {
            (float p, float mix) = rings[ring];
            for (int i = 0; i < n; i++)
            {
                Vector2 point = boundary[i] + ((c - boundary[i]) * p);
                positions.Add(Vector2.Transform(point, local));
                colors.Add(RgbaF32.FromRgba8(Lerp(Surround(i), centerColor, mix)));
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
        colors.Add(RgbaF32.FromRgba8(Lerp(Surround(0), centerColor, rings[^1].Mix)));
        int last = (rings.Length - 1) * n;
        for (int i = 0; i < n; i++)
        {
            indices.AddRange([last + i, last + ((i + 1) % n), centerIndex]);
        }

        return new Mesh(MeshMode.Triangles, positions.ToArray(), colors.ToArray(), indices: indices.ToArray());
    }

    /// <summary>Rings from the boundary to the center at positions, each point colored by its ring and boundary index, and the center.</summary>
    private static Mesh Rings(Vector2[] boundary, Vector2 center, Matrix3x2 local, float[] rings, Func<int, int, Rgba8> color, Rgba8 centerColor)
    {
        if (rings[0] > 0)
        {
            rings = [0, .. rings];
            Func<int, int, Rgba8> inner = color;
            color = (ring, i) => inner(Math.Max(0, ring - 1), i);
        }

        List<Vector2> positions = [];
        List<RgbaF32> colors = [];
        List<int> indices = [];
        int n = boundary.Length;
        for (int ring = 0; ring < rings.Length; ring++)
        {
            for (int i = 0; i < n; i++)
            {
                positions.Add(Vector2.Transform(boundary[i] + ((center - boundary[i]) * rings[ring]), local));
                colors.Add(RgbaF32.FromRgba8(color(ring, i)));
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

        int centerIndex = positions.Count;
        positions.Add(Vector2.Transform(center, local));
        colors.Add(RgbaF32.FromRgba8(centerColor));
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

    /// <summary>A path's outline as points (quadratic curves in 8 steps, cubic curves in 12), for a path gradient's boundary.</summary>
    public static Vector2[] Flatten(Path? path)
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
}
