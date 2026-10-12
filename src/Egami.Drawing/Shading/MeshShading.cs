using System.Buffers;
using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Shading;

/// <summary>
/// A mesh's colors in device space. Each pixel belongs to one triangle, chosen when the shader is built: the last triangle whose
/// area holds the pixel's center, or, for an edge pixel whose center no triangle holds, the nearest triangle within half a
/// diagonal. The pixel's color is that triangle's paint (through its texture mapping when the mesh has texture coordinates),
/// combined with the triangle's interpolated vertex color or value. Coverage comes separately, from the triangles' union.
/// </summary>
internal sealed class MeshShader : Shader, IDisposable
{
    /// <summary>A pixel's center more than this far outside every triangle belongs to none: half a pixel's diagonal, and a little.</summary>
    private const float Reach = 0.7072f;

    private readonly Rect _area;
    private readonly int[] _owner;
    private readonly Triangle[] _triangles;
    private readonly PremulRgbaF32[]? _colors;
    private readonly float[]? _values;
    private readonly GradientRamp? _ramp;
    private readonly bool _linear;
    private readonly Shader? _paint;
    private readonly bool _colorsOnly;
    private readonly float _alpha;
    private readonly BlendMode _blend;

    private MeshShader(Rect area, int[] owner, Triangle[] triangles, PremulRgbaF32[]? colors, float[]? values, GradientRamp? ramp, bool linear, Shader? paint, bool colorsOnly, float alpha, BlendMode blend)
    {
        _area = area;
        _owner = owner;
        _triangles = triangles;
        _colors = colors;
        _values = values;
        _ramp = ramp;
        _linear = linear;
        _paint = paint;
        _colorsOnly = colorsOnly;
        _alpha = alpha;
        _blend = blend;
    }

    /// <summary>
    /// Builds the shader over <paramref name="area"/> for the kept triangles (vertex index triples, in drawing order) of a mesh whose
    /// vertices are at <paramref name="device"/> in device space. <paramref name="images"/> carries the texels of an image paint from
    /// call to call, so an atlas converts its sheet once.
    /// </summary>
    internal static MeshShader Create(
        Mesh mesh, ReadOnlySpan<int> kept, ReadOnlySpan<Vector2> device, Rect area, Paint paint, BlendMode colorBlend, ReadOnlySpan<GradientStop> stops,
        Matrix3x2 toDevice, ColorSpace colorSpace, ref ImageShader? images, Paint[]? trianglePaints = null)
    {
        int count = kept.Length / 3;
        int[] owner = ArrayPool<int>.Shared.Rent(area.Width * area.Height);
        Triangle[] triangles = new Triangle[count];
        try
        {
            Assign(kept, device, area, owner);
            for (int k = 0; k < count; k++)
            {
                int a = kept[3 * k], b = kept[(3 * k) + 1], c = kept[(3 * k) + 2];
                triangles[k] = Triangle.Create(a, b, c, device[a], device[b], device[c]);
            }

            bool linear = colorSpace == ColorSpace.Linear;
            PremulRgbaF32[]? colors = null;
            if (!mesh.Colors.IsEmpty)
            {
                ReadOnlySpan<RgbaF32> straight = mesh.Colors.Span;
                colors = new PremulRgbaF32[straight.Length];
                for (int i = 0; i < colors.Length; i++)
                {
                    RgbaF32 s = straight[i];
                    colors[i] = SolidShader.Premultiply(linear ? new RgbaF32(SrgbCurve.ToLinear(s.R), SrgbCurve.ToLinear(s.G), SrgbCurve.ToLinear(s.B), s.A) : s);
                }
            }

            float[]? values = mesh.Values.IsEmpty ? null : mesh.Values.ToArray();
            GradientRamp? ramp = values is null ? null : GradientRamp.Of(new LinearGradient(Vector2.Zero, Vector2.UnitX, stops));
            bool hasVertex = colors is not null || values is not null;

            // A solid paint's color gives way to the vertex colors, as in Skia; only its alpha scales them.
            bool colorsOnly = hasVertex && paint is SolidPaint;
            float alpha = paint is SolidPaint solid ? Math.Clamp(solid.Color.A, 0, 1) : 1;
            Shader? shader = null;
            if (!colorsOnly)
            {
                if (mesh.TexCoords.IsEmpty)
                {
                    shader = paint.CreateShader(toDevice, colorSpace);
                }
                else
                {
                    Textures(mesh, triangles, owner, area, paint, trianglePaints, toDevice, colorSpace, ref images);
                }
            }

            return new MeshShader(area, owner, triangles, colors, values, ramp, linear, shader, colorsOnly, alpha, colorBlend);
        }
        catch
        {
            ArrayPool<int>.Shared.Return(owner);
            throw;
        }
    }

    public void Dispose() => ArrayPool<int>.Shared.Return(_owner);

    public override void Shade(int y, int x, Span<PremulRgbaF32> row)
    {
        if (y < _area.Y || y >= _area.Bottom)
        {
            row.Clear();
            return;
        }

        int rowStart = ((y - _area.Y) * _area.Width) - _area.X;
        int i = 0;
        while (i < row.Length)
        {
            int px = x + i;
            int o = px >= _area.X && px < _area.Right ? _owner[rowStart + px] : -1;
            int j = i + 1;
            while (j < row.Length && x + j >= _area.X && x + j < _area.Right && _owner[rowStart + x + j] == o)
            {
                j++;
            }

            Span<PremulRgbaF32> run = row[i..j];
            if (o < 0)
            {
                run.Clear();
            }
            else
            {
                ShadeRun(_triangles[o], y, px, run);
            }

            i = j;
        }
    }

    /// <summary>
    /// Gives each pixel of the area its triangle: a pixel whose center lies inside a triangle (on its edges included) belongs to the
    /// last such triangle; any other pixel within reach of a triangle belongs to the nearest one. Owners are triangle numbers, or −1.
    /// </summary>
    private static void Assign(ReadOnlySpan<int> kept, ReadOnlySpan<Vector2> device, Rect area, int[] owner)
    {
        int size = area.Width * area.Height;
        owner.AsSpan(0, size).Fill(-1);
        float[] nearest = ArrayPool<float>.Shared.Rent(size);
        try
        {
            nearest.AsSpan(0, size).Fill(float.NegativeInfinity);
            for (int k = 0; k < kept.Length / 3; k++)
            {
                Vector2 p0 = device[kept[3 * k]], p1 = device[kept[(3 * k) + 1]], p2 = device[kept[(3 * k) + 2]];
                if (Cross(p1 - p0, p2 - p0) < 0)
                {
                    (p1, p2) = (p2, p1);
                }

                Edge e0 = new(p0, p1), e1 = new(p1, p2), e2 = new(p2, p0);
                float minX = MathF.Min(p0.X, MathF.Min(p1.X, p2.X)) - Reach, maxX = MathF.Max(p0.X, MathF.Max(p1.X, p2.X)) + Reach;
                float minY = MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y)) - Reach, maxY = MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y)) + Reach;
                int x0 = Math.Max(area.X, (int)MathF.Floor(minX - 0.5f)), x1 = Math.Min(area.Right, (int)MathF.Ceiling(maxX + 0.5f));
                int y0 = Math.Max(area.Y, (int)MathF.Floor(minY - 0.5f)), y1 = Math.Min(area.Bottom, (int)MathF.Ceiling(maxY + 0.5f));
                for (int y = y0; y < y1; y++)
                {
                    float cy = y + 0.5f;
                    int rowStart = (y - area.Y) * area.Width;
                    float d0 = e0.At(x0 + 0.5f, cy), d1 = e1.At(x0 + 0.5f, cy), d2 = e2.At(x0 + 0.5f, cy);
                    for (int x = x0; x < x1; x++, d0 += e0.Step, d1 += e1.Step, d2 += e2.Step)
                    {
                        float d = MathF.Min(d0, MathF.Min(d1, d2));
                        int at = rowStart + x - area.X;
                        if (d >= 0)
                        {
                            owner[at] = k;
                            nearest[at] = float.PositiveInfinity;
                        }
                        else if (d > -Reach && d > nearest[at])
                        {
                            owner[at] = k;
                            nearest[at] = d;
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(nearest);
        }
    }

    /// <summary>
    /// Creates the paint's shader for each triangle that owns a pixel, mapped through the triangle's texture coordinates. With a
    /// paint per triangle (a lattice's cells), each image's texels are prepared once.
    /// </summary>
    private static void Textures(Mesh mesh, Triangle[] triangles, int[] owner, Rect area, Paint paint, Paint[]? trianglePaints, Matrix3x2 toDevice, ColorSpace colorSpace, ref ImageShader? images)
    {
        Dictionary<Paint, ImageShader>? perPaint = trianglePaints is null ? null : new(ReferenceEqualityComparer.Instance);
        bool[] used = new bool[triangles.Length];
        foreach (int o in owner.AsSpan(0, area.Width * area.Height))
        {
            if (o >= 0)
            {
                used[o] = true;
            }
        }

        ReadOnlySpan<Vector2> tex = mesh.TexCoords.Span, positions = mesh.Positions.Span;
        for (int k = 0; k < triangles.Length; k++)
        {
            if (!used[k])
            {
                continue;
            }

            ref Triangle t = ref triangles[k];

            // Texture space → local: the affine map taking the triangle's texture coordinates to its positions.
            Matrix3x2 fromTex = Frame(tex[t.A], tex[t.B], tex[t.C]), toLocal = Frame(positions[t.A], positions[t.B], positions[t.C]);
            if (!Matrix3x2.Invert(fromTex, out Matrix3x2 texToFrame))
            {
                continue;
            }

            Paint own = trianglePaints?[k] ?? paint;
            Matrix3x2 mapping = own.LocalMatrix * texToFrame * toLocal;
            if (own is ImagePaint image)
            {
                if (!Matrix3x2.Invert(mapping * toDevice, out Matrix3x2 inverse) || !Finite(inverse))
                {
                    continue;
                }

                if (trianglePaints is null)
                {
                    images = images is null ? ImageShader.Create(image, inverse) : images.Rebase(image, inverse);
                    t.Texture = images;
                }
                else
                {
                    ImageShader shader = perPaint!.TryGetValue(own, out ImageShader? first) ? first.Rebase(image, inverse) : ImageShader.Create(image, inverse);
                    perPaint[own] = shader;
                    t.Texture = shader;
                }
            }
            else
            {
                t.Texture = (own with { LocalMatrix = mapping }).CreateShader(toDevice, colorSpace);
            }
        }
    }

    /// <summary>The affine map from the unit triangle's frame ((1, 0) and (0, 1) from the origin) onto a triangle.</summary>
    private static Matrix3x2 Frame(Vector2 a, Vector2 b, Vector2 c) => new(b.X - a.X, b.Y - a.Y, c.X - a.X, c.Y - a.Y, a.X, a.Y);

    private static bool Finite(Matrix3x2 m) => float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M31) && float.IsFinite(m.M32);

    private static float Cross(Vector2 a, Vector2 b) => (a.X * b.Y) - (a.Y * b.X);

    private void ShadeRun(in Triangle t, int y, int x, Span<PremulRgbaF32> run)
    {
        Shader? paint = _paint ?? t.Texture;
        bool hasVertex = _colors is not null || _values is not null;
        if (!hasVertex)
        {
            if (paint is null)
            {
                run.Clear();
            }
            else
            {
                paint.Shade(y, x, run);
            }

            return;
        }

        Span<PremulRgbaF32> vertex = stackalloc PremulRgbaF32[Chunk];
        for (int s = 0; s < run.Length; s += Chunk)
        {
            int n = Math.Min(Chunk, run.Length - s);
            Span<PremulRgbaF32> v = vertex[..n], part = run.Slice(s, n);
            Interpolate(t, y, x + s, v);
            if (_colorsOnly || paint is null)
            {
                float a = _colorsOnly ? _alpha : 0;
                for (int i = 0; i < n; i++)
                {
                    PremulRgbaF32 c = v[i];
                    part[i] = new PremulRgbaF32(c.R * a, c.G * a, c.B * a, c.A * a);
                }

                continue;
            }

            paint.Shade(y, x + s, part);
            Compositor.Blend(_blend, part, v, 1, ColorSpace.Encoded);
            v.CopyTo(part);
        }
    }

    /// <summary>The vertex colors (or the values' colors) at pixel centers, barycentric in the triangle, clamped onto it outside it.</summary>
    private void Interpolate(in Triangle t, int y, int x, Span<PremulRgbaF32> colors)
    {
        float cy = y + 0.5f;
        for (int i = 0; i < colors.Length; i++)
        {
            (float w0, float w1, float w2) = t.Weights(x + i + 0.5f, cy);
            if (_values is float[] values)
            {
                float v = (w0 * values[t.A]) + (w1 * values[t.B]) + (w2 * values[t.C]);
                colors[i] = _ramp!.Evaluate(Math.Clamp(v, 0, 1));
                continue;
            }

            PremulRgbaF32 a = _colors![t.A], b = _colors[t.B], c = _colors[t.C];
            PremulRgbaF32 mix = new(
                (w0 * a.R) + (w1 * b.R) + (w2 * c.R),
                (w0 * a.G) + (w1 * b.G) + (w2 * c.G),
                (w0 * a.B) + (w1 * b.B) + (w2 * c.B),
                (w0 * a.A) + (w1 * b.A) + (w2 * c.A));
            colors[i] = _linear ? ToEncoded(mix) : mix;
        }
    }

    private static PremulRgbaF32 ToEncoded(PremulRgbaF32 c)
    {
        if (c.A <= 0)
        {
            return default;
        }

        float k = 1 / c.A;
        return new PremulRgbaF32(SrgbCurve.ToEncoded(c.R * k) * c.A, SrgbCurve.ToEncoded(c.G * k) * c.A, SrgbCurve.ToEncoded(c.B * k) * c.A, c.A);
    }

    /// <summary>A triangle edge's signed distance, positive inside a counterclockwise (in y-up terms) triangle, and its step along x.</summary>
    private readonly struct Edge
    {
        private readonly float _a;
        private readonly float _b;
        private readonly float _c;

        internal Edge(Vector2 from, Vector2 to)
        {
            Vector2 d = to - from;
            float length = d.Length();
            _a = -d.Y / length;
            _b = d.X / length;
            _c = -((_a * from.X) + (_b * from.Y));
        }

        internal float Step => _a;

        internal float At(float x, float y) => (_a * x) + (_b * y) + _c;
    }

    /// <summary>A triangle's vertex indices, its barycentric frame in device space and, for a textured mesh, its paint's shader.</summary>
    private struct Triangle
    {
        internal int A;
        internal int B;
        internal int C;
        internal Shader? Texture;
        private Vector2 _origin;
        private float _i00;
        private float _i01;
        private float _i10;
        private float _i11;

        internal static Triangle Create(int a, int b, int c, Vector2 pa, Vector2 pb, Vector2 pc)
        {
            Vector2 u = pb - pa, v = pc - pa;
            float det = (u.X * v.Y) - (u.Y * v.X);
            float k = 1 / det;
            return new Triangle { A = a, B = b, C = c, _origin = pa, _i00 = v.Y * k, _i01 = -v.X * k, _i10 = -u.Y * k, _i11 = u.X * k };
        }

        /// <summary>The weights of the three vertices at a point; outside the triangle, negative weights are dropped and the rest rescaled.</summary>
        internal readonly (float W0, float W1, float W2) Weights(float x, float y)
        {
            float dx = x - _origin.X, dy = y - _origin.Y;
            float w1 = (_i00 * dx) + (_i01 * dy), w2 = (_i10 * dx) + (_i11 * dy), w0 = 1 - w1 - w2;
            if (w0 >= 0 && w1 >= 0 && w2 >= 0)
            {
                return (w0, w1, w2);
            }

            w0 = MathF.Max(w0, 0);
            w1 = MathF.Max(w1, 0);
            w2 = MathF.Max(w2, 0);
            float sum = w0 + w1 + w2;
            return sum > 0 ? (w0 / sum, w1 / sum, w2 / sum) : (1, 0, 0);
        }
    }
}

/// <summary>A paint that hands the raster backend a prepared <see cref="MeshShader"/>, with the mesh paint's blend mode and opacity.</summary>
internal sealed record class MeshShadingPaint : Paint
{
    internal MeshShadingPaint(MeshShader shader)
    {
        Shader = shader;
    }

    internal MeshShader Shader { get; }

    internal override Shader? CreateShader(Matrix3x2 toDevice, ColorSpace colorSpace) => Shader;
}
