using System.Numerics;

using Egami.Colors;
using Egami.Pixels;

namespace Egami.Drawing;

/// <summary>Which surface a <see cref="Patch"/> is: its boundary only, or a full tensor-product net.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="patch"/>
/// </example>
public enum PatchKind
{
    /// <summary>A Coons patch: four cubic boundary curves, 12 points, the inside blended from them (PDF shading type 6, Skia's drawPatch).</summary>
    Coons = 0,

    /// <summary>A tensor-product patch: a 4×4 net of cubic control points (PDF shading type 7).</summary>
    Tensor = 1,
}

/// <summary>
/// A curved four-sided surface with a color, value or texture coordinate at each corner, for <see cref="Canvas.DrawPatch"/>: PDF's
/// Coons and tensor-product patch shadings (types 6 and 7) and Skia's drawPatch. The surface's parameters u (left to right) and v
/// (top to bottom) run from 0 to 1; corner data is interpolated bilinearly in them. Immutable.
/// <para>
/// Corners are in the order top-left (u 0, v 0), top-right, bottom-right, bottom-left. A Coons patch's 12 points go clockwise
/// from the top-left corner, as Skia's: the top curve left to right (points 0 to 3), the right curve down (3 to 6), the bottom
/// curve right to left (6 to 9) and the left curve up (9, 10, 11, 0). A tensor patch's 16 points are its net row by row, top row
/// first, each row left to right.
/// </para>
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="patch"/>
/// </example>
public sealed class Patch
{
    /// <summary>The largest number of rows or columns a patch is cut into: 256, so at most 131,072 triangles.</summary>
    internal const int MaxDivisions = 256;

    /// <summary>How far, in device pixels, the triangles may stray from the surface.</summary>
    private const float Flatness = 0.25f;

    private readonly Vector2[] _points;
    private readonly RgbaF32[]? _colors;
    private readonly float[]? _values;
    private readonly Vector2[]? _texCoords;
    private InkColor[]? _inks;

    private Patch(PatchKind kind, ReadOnlySpan<Vector2> points, ReadOnlySpan<RgbaF32> colors, ReadOnlySpan<float> values, ReadOnlySpan<Vector2> texCoords)
    {
        Kind = kind;
        foreach (Vector2 p in points)
        {
            CheckPoint(p, nameof(points));
        }

        CheckCorners(colors.Length, nameof(colors));
        CheckCorners(values.Length, nameof(values));
        CheckCorners(texCoords.Length, nameof(texCoords));
        if (!colors.IsEmpty && !values.IsEmpty)
        {
            throw new ArgumentException("A patch has corner colors or corner values, not both.", nameof(values));
        }

        foreach (RgbaF32 c in colors)
        {
            if (!(float.IsFinite(c.R) && float.IsFinite(c.G) && float.IsFinite(c.B) && float.IsFinite(c.A)))
            {
                throw new ArgumentException("A corner color must be finite.", nameof(colors));
            }
        }

        foreach (float v in values)
        {
            if (!float.IsFinite(v))
            {
                throw new ArgumentException("A corner value must be finite.", nameof(values));
            }
        }

        foreach (Vector2 t in texCoords)
        {
            CheckPoint(t, nameof(texCoords));
        }

        _points = points.ToArray();
        _colors = colors.IsEmpty ? null : colors.ToArray();
        _values = values.IsEmpty ? null : values.ToArray();
        _texCoords = texCoords.IsEmpty ? null : texCoords.ToArray();
        Bounds = RectF.FromPoints(_points);
    }

    /// <summary>Gets which surface the patch is.</summary>
    public PatchKind Kind { get; }

    /// <summary>Gets the control points: 12 for a Coons patch, 16 for a tensor patch, in the orders the class describes.</summary>
    public ReadOnlyMemory<Vector2> Points => _points;

    /// <summary>Gets the corner colors, or empty when there are none: for a patch with inks, their appearances in sRGB.</summary>
    public ReadOnlyMemory<RgbaF32> CornerColors => _colors;

    /// <summary>Gets the corner colours as inks, or empty when the patch has none (see <see cref="WithInks"/>).</summary>
    public ReadOnlyMemory<InkColor> CornerInks => _inks;

    /// <summary>Gets the corner values, or empty when there are none.</summary>
    public ReadOnlyMemory<float> CornerValues => _values;

    /// <summary>Gets the corner texture coordinates, or empty when there are none.</summary>
    public ReadOnlyMemory<Vector2> CornerTexCoords => _texCoords;

    /// <summary>Gets the bounds of the control points, which hold the whole surface.</summary>
    public RectF Bounds { get; }

    /// <summary>Creates a Coons patch from its four boundary curves.</summary>
    /// <param name="points">The 12 boundary points, clockwise from the top-left corner.</param>
    /// <param name="cornerColors">Four straight-alpha corner colors, or empty.</param>
    /// <param name="cornerTexCoords">Four corner points in the paint's coordinates, or empty.</param>
    /// <param name="cornerValues">Four corner values, mapped through the stops given to <see cref="Canvas.DrawPatch"/>, or empty.</param>
    /// <returns>The patch.</returns>
    /// <exception cref="ArgumentException">There are not 12 points or four of each corner list, both colors and values are given, or a number is not finite.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="patch"/>
    /// </example>
    public static Patch Coons(ReadOnlySpan<Vector2> points, ReadOnlySpan<RgbaF32> cornerColors = default, ReadOnlySpan<Vector2> cornerTexCoords = default, ReadOnlySpan<float> cornerValues = default)
    {
        if (points.Length != 12)
        {
            throw new ArgumentException("A Coons patch has 12 points.", nameof(points));
        }

        return new Patch(PatchKind.Coons, points, cornerColors, cornerValues, cornerTexCoords);
    }

    /// <summary>Creates a tensor-product patch from its 4×4 net.</summary>
    /// <param name="points">The 16 points, row by row from the top, each row left to right.</param>
    /// <param name="cornerColors">Four straight-alpha corner colors, or empty.</param>
    /// <param name="cornerTexCoords">Four corner points in the paint's coordinates, or empty.</param>
    /// <param name="cornerValues">Four corner values, or empty.</param>
    /// <returns>The patch.</returns>
    /// <exception cref="ArgumentException">There are not 16 points or four of each corner list, both colors and values are given, or a number is not finite.</exception>
    public static Patch Tensor(ReadOnlySpan<Vector2> points, ReadOnlySpan<RgbaF32> cornerColors = default, ReadOnlySpan<Vector2> cornerTexCoords = default, ReadOnlySpan<float> cornerValues = default)
    {
        if (points.Length != 16)
        {
            throw new ArgumentException("A tensor patch has 16 points.", nameof(points));
        }

        return new Patch(PatchKind.Tensor, points, cornerColors, cornerValues, cornerTexCoords);
    }

    /// <summary>
    /// Returns this patch with its four corner colours given as inks: an ink canvas blends their tints across the surface (an ink
    /// a corner does not name counts as 0 there); other canvases use the appearances in sRGB, which become <see cref="CornerColors"/>.
    /// </summary>
    /// <param name="cornerInks">Four corner colours, in the order of <see cref="CornerColors"/>.</param>
    /// <returns>The new patch.</returns>
    /// <exception cref="ArgumentException">There are not four, or the patch has corner values.</exception>
    /// <exception cref="ArgumentNullException">A colour is <see langword="null"/>.</exception>
    public Patch WithInks(ReadOnlySpan<InkColor> cornerInks)
    {
        if (_values is not null)
        {
            throw new ArgumentException("A patch has corner colors or corner values, not both.", nameof(cornerInks));
        }

        if (cornerInks.Length != 4)
        {
            throw new ArgumentException("A patch has four corner colours.", nameof(cornerInks));
        }

        RgbaF32[] colors = new RgbaF32[4];
        for (int i = 0; i < 4; i++)
        {
            colors[i] = Inks.Appearance(cornerInks[i] ?? throw new ArgumentNullException(nameof(cornerInks)));
        }

        Patch patch = new(Kind, _points, colors, default, _texCoords);
        patch._inks = cornerInks.ToArray();
        return patch;
    }

    /// <summary>Returns the point of the surface at parameters (u, v).</summary>
    /// <param name="u">The horizontal parameter, 0 to 1.</param>
    /// <param name="v">The vertical parameter, 0 to 1.</param>
    /// <returns>The point, in the canvas's local coordinates.</returns>
    public Vector2 Evaluate(float u, float v)
    {
        if (Kind == PatchKind.Tensor)
        {
            Span<float> bu = stackalloc float[4], bv = stackalloc float[4];
            Bernstein(u, bu);
            Bernstein(v, bv);
            Vector2 sum = Vector2.Zero;
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    sum += bv[r] * bu[c] * _points[(r * 4) + c];
                }
            }

            return sum;
        }

        // Coons: the two ruled surfaces between opposite curves, less the bilinear surface of the corners.
        Vector2[] p = _points;
        Vector2 top = Cubic(p[0], p[1], p[2], p[3], u), bottom = Cubic(p[9], p[8], p[7], p[6], u);
        Vector2 left = Cubic(p[0], p[11], p[10], p[9], v), right = Cubic(p[3], p[4], p[5], p[6], v);
        Vector2 corners = ((1 - u) * (1 - v) * p[0]) + (u * (1 - v) * p[3]) + (u * v * p[6]) + ((1 - u) * v * p[9]);
        return ((1 - v) * top) + (v * bottom) + ((1 - u) * left) + (u * right) - corners;
    }

    /// <summary>
    /// Returns the patch's 4×4 tensor net. A Coons patch's inner points come from its boundary as PDF section 8.7.4.5.8 gives them,
    /// so the tensor patch of that net is the same surface.
    /// </summary>
    /// <returns>The 16 points, row by row from the top.</returns>
    public ReadOnlyMemory<Vector2> ToTensorNet() => TensorNet();

    /// <summary>The tensor net as a new array the caller may change.</summary>
    internal Vector2[] TensorNet()
    {
        if (Kind == PatchKind.Tensor)
        {
            return (Vector2[])_points.Clone();
        }

        Vector2[] p = _points;
        Vector2[] n = new Vector2[16];
        n[0] = p[0];
        n[1] = p[1];
        n[2] = p[2];
        n[3] = p[3];
        n[7] = p[4];
        n[11] = p[5];
        n[15] = p[6];
        n[14] = p[7];
        n[13] = p[8];
        n[12] = p[9];
        n[8] = p[10];
        n[4] = p[11];

        // Each inner point from its nearest corner, the corner's two neighbors, the far ends of its edges, the points next to the
        // opposite corner and the opposite corner itself.
        n[5] = Inner(n[0], n[1], n[4], n[3], n[12], n[13], n[7], n[15]);
        n[6] = Inner(n[3], n[2], n[7], n[0], n[15], n[14], n[4], n[12]);
        n[9] = Inner(n[12], n[13], n[8], n[15], n[0], n[1], n[11], n[3]);
        n[10] = Inner(n[15], n[14], n[11], n[12], n[3], n[2], n[8], n[0]);
        return n;
    }

    /// <summary>
    /// Cuts the patch into a grid of triangles fine enough that, under <paramref name="toDevice"/>, no triangle strays more than a
    /// quarter of a device pixel from the surface and the corner data's bilinear twist stays under half a level. The triangles go
    /// row by row from v = 0, each row from u = 0, so where the patch folds over itself the later part covers the earlier, as PDF
    /// section 8.7.4.5.7 requires.
    /// </summary>
    internal Mesh? Tessellate(Matrix3x2 toDevice)
    {
        (int nu, int nv) = Divisions(toDevice);
        int count = (nu + 1) * (nv + 1);
        Vector2[] positions = new Vector2[count];
        RgbaF32[] colors = _colors is null ? [] : new RgbaF32[count];
        float[] values = _values is null ? [] : new float[count];
        Vector2[] tex = _texCoords is null ? [] : new Vector2[count];
        for (int j = 0; j <= nv; j++)
        {
            float v = (float)j / nv;
            for (int i = 0; i <= nu; i++)
            {
                float u = (float)i / nu;
                int k = (j * (nu + 1)) + i;
                positions[k] = Evaluate(u, v);
                if (!(float.IsFinite(positions[k].X) && float.IsFinite(positions[k].Y)))
                {
                    return null;
                }

                float w0 = (1 - u) * (1 - v), w1 = u * (1 - v), w2 = u * v, w3 = (1 - u) * v;
                if (_colors is RgbaF32[] c)
                {
                    colors[k] = new RgbaF32(
                        (w0 * c[0].R) + (w1 * c[1].R) + (w2 * c[2].R) + (w3 * c[3].R),
                        (w0 * c[0].G) + (w1 * c[1].G) + (w2 * c[2].G) + (w3 * c[3].G),
                        (w0 * c[0].B) + (w1 * c[1].B) + (w2 * c[2].B) + (w3 * c[3].B),
                        (w0 * c[0].A) + (w1 * c[1].A) + (w2 * c[2].A) + (w3 * c[3].A));
                }

                if (_values is float[] t)
                {
                    values[k] = (w0 * t[0]) + (w1 * t[1]) + (w2 * t[2]) + (w3 * t[3]);
                }

                if (_texCoords is Vector2[] x)
                {
                    tex[k] = (w0 * x[0]) + (w1 * x[1]) + (w2 * x[2]) + (w3 * x[3]);
                }
            }
        }

        int[] indices = new int[nu * nv * 6];
        int n = 0;
        for (int j = 0; j < nv; j++)
        {
            for (int i = 0; i < nu; i++)
            {
                int a = (j * (nu + 1)) + i, b = a + 1, d = a + nu + 1, c = d + 1;
                indices[n++] = a;
                indices[n++] = b;
                indices[n++] = c;
                indices[n++] = a;
                indices[n++] = c;
                indices[n++] = d;
            }
        }

        return new Mesh(MeshMode.Triangles, positions, colors, tex, values, indices);
    }

    /// <summary>The grid size: from the net's second differences along each direction and its twist, in device pixels, and the corner data's twist.</summary>
    internal (int U, int V) Divisions(Matrix3x2 toDevice)
    {
        Vector2[] net = TensorNet();
        for (int i = 0; i < 16; i++)
        {
            net[i] = Vector2.Transform(net[i], toDevice);
        }

        float du = 0, dv = 0, twist = 0;
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 2; c++)
            {
                du = MathF.Max(du, (net[(r * 4) + c] - (2 * net[(r * 4) + c + 1]) + net[(r * 4) + c + 2]).Length());
                dv = MathF.Max(dv, (net[(c * 4) + r] - (2 * net[((c + 1) * 4) + r]) + net[((c + 2) * 4) + r]).Length());
            }
        }

        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                twist = MathF.Max(twist, (net[(r * 4) + c] - net[(r * 4) + c + 1] - net[((r + 1) * 4) + c] + net[((r + 1) * 4) + c + 1]).Length());
            }
        }

        // A cubic's chord strays at most 6·d / (8·k²) from it over k equal steps; a cell's two triangles miss its twist by a quarter.
        double ku = Math.Ceiling(Math.Sqrt(0.75 * du / Flatness)), kv = Math.Ceiling(Math.Sqrt(0.75 * dv / Flatness));
        ku = Math.Max(ku, 1);
        kv = Math.Max(kv, 1);
        double need = Math.Max(9 * twist / (4 * Flatness), DataTwist() / 2);
        if (ku * kv < need)
        {
            double f = Math.Sqrt(need / (ku * kv));
            ku = Math.Ceiling(ku * f);
            kv = Math.Ceiling(kv * f);
        }

        // Coordinates that overflow give infinities and NaNs: the most divisions, which the clip then mostly skips.
        return (double.IsNaN(ku) ? MaxDivisions : (int)Math.Clamp(ku, 1, MaxDivisions), double.IsNaN(kv) ? MaxDivisions : (int)Math.Clamp(kv, 1, MaxDivisions));
    }

    /// <summary>The corner data's bilinear twist, in 8-bit levels, which two triangles per cell cannot follow.</summary>
    private float DataTwist()
    {
        float t = 0;
        if (_colors is RgbaF32[] c)
        {
            t = MathF.Max(MathF.Max(MathF.Abs(c[0].R - c[1].R + c[2].R - c[3].R), MathF.Abs(c[0].G - c[1].G + c[2].G - c[3].G)), MathF.Max(MathF.Abs(c[0].B - c[1].B + c[2].B - c[3].B), MathF.Abs(c[0].A - c[1].A + c[2].A - c[3].A)));
        }

        if (_values is float[] v)
        {
            t = MathF.Abs(v[0] - v[1] + v[2] - v[3]);
        }

        return MathF.Min(t * 255, 1e6f);
    }

    private static Vector2 Inner(Vector2 corner, Vector2 a, Vector2 b, Vector2 farA, Vector2 farB, Vector2 nearOpposite1, Vector2 nearOpposite2, Vector2 opposite)
        => ((-4 * corner) + (6 * (a + b)) - (2 * (farA + farB)) + (3 * (nearOpposite1 + nearOpposite2)) - opposite) / 9;

    private static Vector2 Cubic(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float t)
    {
        float s = 1 - t;
        return (s * s * s * a) + (3 * s * s * t * b) + (3 * s * t * t * c) + (t * t * t * d);
    }

    private static void Bernstein(float t, Span<float> b)
    {
        float s = 1 - t;
        b[0] = s * s * s;
        b[1] = 3 * s * s * t;
        b[2] = 3 * s * t * t;
        b[3] = t * t * t;
    }

    private static void CheckCorners(int length, string name)
    {
        if (length != 0 && length != 4)
        {
            throw new ArgumentException("There must be four corners, or none.", name);
        }
    }

    private static void CheckPoint(Vector2 p, string name)
    {
        if (!(float.IsFinite(p.X) && float.IsFinite(p.Y)))
        {
            throw new ArgumentException("The points must be finite.", name);
        }
    }
}
