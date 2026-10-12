using System.Numerics;
using System.Runtime.CompilerServices;

using Egami.Colors;
using Egami.Pixels;

namespace Egami.Drawing.Shading;

/// <summary>
/// A gradient's stops ready to evaluate: offsets made non-decreasing and padded to 0 and 1, colors converted once into the
/// interpolation space (premultiplied unless the gradient says otherwise, the hue excepted), and Oklch hues unwrapped per segment
/// by the hue method. One ramp, with its 1024-entry table, is kept per gradient instance.
/// </summary>
internal sealed class GradientRamp
{
    /// <summary>The 8-bit table's size: t picks entry round(t · 1023).</summary>
    internal const int TableSize = 1024;

    private static readonly ConditionalWeakTable<Gradient, GradientRamp> Cache = [];

    private readonly InterpolationSpace _space;
    private readonly bool _premultiplied;
    private readonly Segment[] _segments;
    private readonly Vector4 _first;
    private readonly Vector4 _last;
    private PremulRgbaF32[]? _table;

    private GradientRamp(Gradient gradient)
    {
        _space = gradient.Interpolation.Space;
        _premultiplied = gradient.Premultiplied;
        IReadOnlyList<GradientStop> stops = gradient.Stops;

        // CSS's fix-ups: an offset below the one before it takes that one's, and the ends repeat the nearest stop.
        List<(float Offset, Vector4 Color)> list = [];
        float previous = 0;
        foreach (GradientStop stop in stops)
        {
            previous = MathF.Max(previous, stop.Offset);
            list.Add((previous, ToSpace(stop.Color)));
        }

        if (list[0].Offset > 0)
        {
            list.Insert(0, (0, list[0].Color));
        }

        if (list[^1].Offset < 1)
        {
            list.Add((1, list[^1].Color));
        }

        List<Segment> segments = [];
        for (int i = 0; i + 1 < list.Count; i++)
        {
            (float t0, Vector4 a) = list[i];
            (float t1, Vector4 b) = list[i + 1];
            if (t1 > t0)
            {
                if (_space == InterpolationSpace.Oklch)
                {
                    (a, b) = FixHues(a, b, gradient.Interpolation.Hue);
                }

                segments.Add(new Segment(t0, t1, Premultiply(a), Premultiply(b)));
            }
        }

        _segments = [.. segments];
        _first = Premultiply(list[0].Color);
        _last = Premultiply(list[^1].Color);
        IsOpaque = list.TrueForAll(s => s.Color.W >= 1);
        Mean = ComputeMean();
    }

    /// <summary>Gets a value indicating whether every stop is opaque.</summary>
    internal bool IsOpaque { get; }

    /// <summary>Gets the color averaged over t from 0 to 1, for a gradient with no extent that repeats.</summary>
    internal PremulRgbaF32 Mean { get; }

    /// <summary>Gets the 1024 colors at t = i / 1023, made on first use.</summary>
    internal PremulRgbaF32[] Table => _table ??= BuildTable();

    /// <summary>Returns the gradient's ramp, made once per instance.</summary>
    internal static GradientRamp Of(Gradient gradient) => Cache.GetValue(gradient, static g => new GradientRamp(g));

    /// <summary>Returns the color at <paramref name="t"/>, from 0 to 1, exactly.</summary>
    internal PremulRgbaF32 Evaluate(float t)
    {
        Segment[] segments = _segments;
        if (segments.Length == 0 || t < segments[0].T0)
        {
            return ToOutput(_first);
        }

        // The segment holding t, with t1 exclusive, so at a hard edge the later stop applies.
        int lo = 0, hi = segments.Length - 1;
        if (t >= segments[hi].T1)
        {
            return ToOutput(_last);
        }

        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (t >= segments[mid].T1)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        Segment s = segments[lo];
        float u = (t - s.T0) / (s.T1 - s.T0);
        return ToOutput(Vector4.Lerp(s.A, s.B, u));
    }

    /// <summary>A color in the interpolation space, straight: (c0, c1, c2, alpha), Oklch's hue in degrees as c2.</summary>
    private Vector4 ToSpace(RgbaF32 color)
    {
        float r = Math.Clamp(color.R, 0, 1), g = Math.Clamp(color.G, 0, 1), b = Math.Clamp(color.B, 0, 1), a = Math.Clamp(color.A, 0, 1);
        switch (_space)
        {
            case InterpolationSpace.LinearSrgb:
                return new Vector4(SrgbCurve.ToLinear(r), SrgbCurve.ToLinear(g), SrgbCurve.ToLinear(b), a);
            case InterpolationSpace.Oklab:
                OklabColor lab = OklabColor.From(new RgbaF32(r, g, b, 1));
                return new Vector4(lab.L, lab.A, lab.B, a);
            case InterpolationSpace.Oklch:
                OklchColor lch = OklchColor.From(new RgbaF32(r, g, b, 1));
                return new Vector4(lch.L, lch.C, float.IsFinite(lch.H) ? lch.H : 0, a);
            default:
                return new Vector4(r, g, b, a);
        }
    }

    /// <summary>Premultiplies the color channels by alpha when the gradient mixes premultiplied; a hue is never multiplied.</summary>
    private Vector4 Premultiply(Vector4 c)
    {
        if (!_premultiplied)
        {
            return c;
        }

        return _space == InterpolationSpace.Oklch
            ? new Vector4(c.X * c.W, c.Y * c.W, c.Z, c.W)
            : new Vector4(c.X * c.W, c.Y * c.W, c.Z * c.W, c.W);
    }

    /// <summary>A mixed color back to premultiplied encoded sRGB.</summary>
    private PremulRgbaF32 ToOutput(Vector4 v)
    {
        float a = Math.Clamp(v.W, 0, 1);
        if (a <= 0)
        {
            return default;
        }

        if (_space == InterpolationSpace.Encoded && _premultiplied)
        {
            return new PremulRgbaF32(Math.Clamp(v.X, 0, a), Math.Clamp(v.Y, 0, a), Math.Clamp(v.Z, 0, a), a);
        }

        // Straight channels in the space; a hue is never premultiplied.
        float k = _premultiplied ? 1 / v.W : 1;
        float c0 = v.X * k, c1 = v.Y * k, c2 = _space == InterpolationSpace.Oklch ? v.Z : v.Z * k;
        float r, g, b;
        switch (_space)
        {
            case InterpolationSpace.LinearSrgb:
                (r, g, b) = (SrgbCurve.ToEncoded(c0), SrgbCurve.ToEncoded(c1), SrgbCurve.ToEncoded(c2));
                break;
            case InterpolationSpace.Oklab:
                RgbaF32 fromLab = new OklabColor(c0, c1, c2).ToRgbaF32();
                (r, g, b) = (fromLab.R, fromLab.G, fromLab.B);
                break;
            case InterpolationSpace.Oklch:
                RgbaF32 fromLch = new OklchColor(c0, c1, c2).ToRgbaF32();
                (r, g, b) = (fromLch.R, fromLch.G, fromLch.B);
                break;
            default:
                (r, g, b) = (c0, c1, c2);
                break;
        }

        return new PremulRgbaF32(Math.Clamp(r, 0, 1) * a, Math.Clamp(g, 0, 1) * a, Math.Clamp(b, 0, 1) * a, a);
    }

    /// <summary>
    /// CSS Color 4's hue fix-ups for one segment: a hue that is powerless (no chroma) takes the other end's, and the method decides
    /// which way around the circle the hues go.
    /// </summary>
    private static (Vector4 A, Vector4 B) FixHues(Vector4 a, Vector4 b, HueMethod method)
    {
        const float Powerless = 1e-4f;
        float h1 = a.Z, h2 = b.Z;
        bool none1 = a.Y < Powerless, none2 = b.Y < Powerless;
        if (none1 && !none2)
        {
            h1 = h2;
        }
        else if (none2 && !none1)
        {
            h2 = h1;
        }

        h1 = Wrap(h1);
        h2 = Wrap(h2);
        float delta = h2 - h1;
        switch (method)
        {
            case HueMethod.Shorter:
                if (delta > 180)
                {
                    h1 += 360;
                }
                else if (delta < -180)
                {
                    h2 += 360;
                }

                break;
            case HueMethod.Longer:
                if (delta > 0 && delta < 180)
                {
                    h1 += 360;
                }
                else if (delta > -180 && delta <= 0)
                {
                    h2 += 360;
                }

                break;
            case HueMethod.Increasing:
                if (delta < 0)
                {
                    h2 += 360;
                }

                break;
            default:
                if (delta > 0)
                {
                    h1 += 360;
                }

                break;
        }

        return (a with { Z = h1 }, b with { Z = h2 });
    }

    private static float Wrap(float degrees)
    {
        float h = degrees % 360;
        return h < 0 ? h + 360 : h;
    }

    private PremulRgbaF32[] BuildTable()
    {
        PremulRgbaF32[] table = new PremulRgbaF32[TableSize];
        for (int i = 0; i < TableSize; i++)
        {
            table[i] = Evaluate(i / (float)(TableSize - 1));
        }

        return table;
    }

    private PremulRgbaF32 ComputeMean()
    {
        // The mean of 1024 evenly spaced samples, near enough for a gradient with no extent.
        const int N = 1024;
        Vector4 sum = default;
        for (int i = 0; i < N; i++)
        {
            PremulRgbaF32 c = Evaluate((i + 0.5f) / N);
            sum += new Vector4(c.R, c.G, c.B, c.A);
        }

        sum /= N;
        return new PremulRgbaF32(sum.X, sum.Y, sum.Z, sum.W);
    }

    private readonly record struct Segment(float T0, float T1, Vector4 A, Vector4 B);
}

/// <summary>Maps device pixel centers to a gradient's t: a row of raw t values, NaN where the gradient has no value.</summary>
internal abstract class GradientGeometry
{
    /// <summary>Gets a value indicating whether every pixel has a t (only a conical gradient can leave pixels without one).</summary>
    internal virtual bool AlwaysDefined => true;

    /// <summary>Writes the raw t of pixels (<paramref name="x"/> + i, <paramref name="y"/>) at their centers.</summary>
    internal abstract void Parameters(int y, int x, Span<float> t);

    /// <summary>The paint-space point of a device pixel center.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected static Vector2 Map(in Matrix3x2 inverse, int x, int y) => Vector2.Transform(new Vector2(x + 0.5f, y + 0.5f), inverse);
}

/// <summary>t along a line: an affine function of the device position, so a row is a start value plus a step.</summary>
internal sealed class LinearGeometry : GradientGeometry
{
    private readonly double _a;
    private readonly double _b;
    private readonly double _c;

    private LinearGeometry(double a, double b, double c)
    {
        _a = a;
        _b = b;
        _c = c;
    }

    internal static LinearGeometry? Create(Matrix3x2 inverse, Vector2 start, Vector2 end)
    {
        Vector2 d = end - start;
        double length2 = ((double)d.X * d.X) + ((double)d.Y * d.Y);
        if (!(length2 > 1e-12))
        {
            return null;
        }

        // t = ((p − start) · d) / |d|², with p = inverse · (X, Y).
        double k = 1 / length2;
        double a = ((inverse.M11 * (double)d.X) + (inverse.M12 * (double)d.Y)) * k;
        double b = ((inverse.M21 * (double)d.X) + (inverse.M22 * (double)d.Y)) * k;
        double c = (((inverse.M31 - (double)start.X) * d.X) + ((inverse.M32 - (double)start.Y) * d.Y)) * k;
        return new LinearGeometry(a, b, c);
    }

    internal override void Parameters(int y, int x, Span<float> t)
    {
        double row = (_b * (y + 0.5)) + _c + (_a * (x + 0.5));
        for (int i = 0; i < t.Length; i++)
        {
            t[i] = (float)(row + (_a * i));
        }
    }
}

/// <summary>t is the distance from the center over the radius.</summary>
internal sealed class RadialGeometry : GradientGeometry
{
    private readonly Matrix3x2 _inverse;
    private readonly Vector2 _center;
    private readonly float _scale;

    private RadialGeometry(Matrix3x2 inverse, Vector2 center, float radius)
    {
        _inverse = inverse;
        _center = center;
        _scale = 1 / radius;
    }

    internal static RadialGeometry? Create(Matrix3x2 inverse, Vector2 center, float radius)
        => radius > 1e-6f ? new RadialGeometry(inverse, center, radius) : null;

    internal override void Parameters(int y, int x, Span<float> t)
    {
        Vector2 p = Map(_inverse, x, y) - _center, step = new(_inverse.M11, _inverse.M12);
        for (int i = 0; i < t.Length; i++)
        {
            Vector2 q = p + (step * i);
            t[i] = q.Length() * _scale;
        }
    }
}

/// <summary>t is the angle from the start angle over the sweep.</summary>
internal sealed class SweepGeometry(Matrix3x2 inverse, Vector2 center, float startAngle, float endAngle) : GradientGeometry
{
    private readonly float _scale = 1 / (endAngle - startAngle);

    internal override void Parameters(int y, int x, Span<float> t)
    {
        Vector2 p = Map(inverse, x, y) - center, step = new(inverse.M11, inverse.M12);
        for (int i = 0; i < t.Length; i++)
        {
            Vector2 q = p + (step * i);
            float degrees = MathF.Atan2(q.Y, q.X) * (180 / MathF.PI);
            if (degrees < 0)
            {
                degrees += 360;
            }

            t[i] = (degrees - startAngle) * _scale;
        }
    }
}

/// <summary>
/// The two-circle gradient: for a point p, the t whose circle c(t) = c0 + t·(c1 − c0), r(t) = r0 + t·(r1 − r0) passes through p,
/// that is a·t² − 2b·t + c = 0 with a = |c1 − c0|² − (r1 − r0)², b = (p − c0)·(c1 − c0) + r0·(r1 − r0) and c = |p − c0|² − r0².
/// Of the roots with r(t) ≥ 0 the larger is taken, as the canvas specification and PDF say; for a clamped gradient with an end
/// not extended, a root past that end does not count, so the smaller one can still draw, as a PDF viewer shows it.
/// </summary>
internal sealed class ConicalGeometry : GradientGeometry
{
    private readonly Matrix3x2 _inverse;
    private readonly Vector2 _c0;
    private readonly Vector2 _cd;
    private readonly float _r0;
    private readonly float _dr;
    private readonly double _a;
    private readonly bool _limitStart;
    private readonly bool _limitEnd;

    private ConicalGeometry(Matrix3x2 inverse, Vector2 c0, float r0, Vector2 c1, float r1, Gradient gradient)
    {
        _inverse = inverse;
        _c0 = c0;
        _cd = c1 - c0;
        _r0 = r0;
        _dr = r1 - r0;
        _a = ((double)_cd.X * _cd.X) + ((double)_cd.Y * _cd.Y) - ((double)_dr * _dr);
        _limitStart = gradient.Tile == TileMode.Clamp && !gradient.ExtendStart;
        _limitEnd = gradient.Tile == TileMode.Clamp && !gradient.ExtendEnd;
    }

    internal override bool AlwaysDefined => false;

    internal static ConicalGeometry? Create(Matrix3x2 inverse, Vector2 c0, float r0, Vector2 c1, float r1, Gradient gradient)
    {
        // The same circle at both ends has no extent.
        if (Vector2.DistanceSquared(c0, c1) < 1e-12f && MathF.Abs(r1 - r0) < 1e-6f)
        {
            return null;
        }

        return new ConicalGeometry(inverse, c0, r0, c1, r1, gradient);
    }

    internal override void Parameters(int y, int x, Span<float> t)
    {
        Vector2 p0 = Map(_inverse, x, y) - _c0, step = new(_inverse.M11, _inverse.M12);
        for (int i = 0; i < t.Length; i++)
        {
            Vector2 pd = p0 + (step * i);
            double b = ((double)pd.X * _cd.X) + ((double)pd.Y * _cd.Y) + ((double)_r0 * _dr);
            double c = ((double)pd.X * pd.X) + ((double)pd.Y * pd.Y) - ((double)_r0 * _r0);
            t[i] = Solve(b, c);
        }
    }

    private float Solve(double b, double c)
    {
        if (Math.Abs(_a) < 1e-9)
        {
            // One circle touches the other from inside: the equation is linear.
            if (b == 0)
            {
                return float.NaN;
            }

            double only = c / (2 * b);
            return Accept(only) ? (float)only : float.NaN;
        }

        double disc = (b * b) - (_a * c);
        if (disc < 0)
        {
            return float.NaN;
        }

        double root = Math.Sqrt(disc);
        double t1 = (b + root) / _a, t2 = (b - root) / _a;
        if (t1 < t2)
        {
            (t1, t2) = (t2, t1);
        }

        if (Accept(t1))
        {
            return (float)t1;
        }

        return Accept(t2) ? (float)t2 : float.NaN;
    }

    private bool Accept(double t)
        => _r0 + (t * _dr) >= 0 && !(_limitEnd && t > 1) && !(_limitStart && t < 0);
}

/// <summary>Brings a raw t into 0 to 1 by the gradient's tile mode, or NaN where nothing is drawn.</summary>
internal readonly struct GradientTiling(TileMode mode, bool extendStart, bool extendEnd)
{
    /// <summary>How far past an end rounding may put a pixel that lies exactly on it, so it still draws.</summary>
    private const float Slack = 1e-6f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal float Apply(float t)
    {
        switch (mode)
        {
            case TileMode.Clamp:
                if (t < 0)
                {
                    return extendStart || t > -Slack ? 0 : float.NaN;
                }

                return t > 1 ? (extendEnd || t < 1 + Slack ? 1 : float.NaN) : t;
            case TileMode.Repeat:
                return t - MathF.Floor(t);
            case TileMode.Mirror:
                float u = t - (2 * MathF.Floor(t * 0.5f));
                return u > 1 ? 2 - u : u;
            default:
                return t is >= -Slack and <= 1 + Slack ? Math.Clamp(t, 0, 1) : float.NaN;
        }
    }

    /// <summary>The table entry for a tiled t, or −1 for none.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Index(float tiled) => float.IsNaN(tiled) ? -1 : (int)((tiled * (GradientRamp.TableSize - 1)) + 0.5f);
}

/// <summary>A gradient's float colors: its stops mixed exactly, or read from its table before dithering.</summary>
internal sealed class GradientShader(GradientGeometry geometry, GradientRamp ramp, Gradient gradient, bool useTable) : Shader
{
    private readonly GradientTiling _tiling = new(gradient.Tile, gradient.ExtendStart, gradient.ExtendEnd);

    public override bool IsOpaque => ramp.IsOpaque && geometry.AlwaysDefined && gradient.Tile != TileMode.Decal
        && (gradient.Tile != TileMode.Clamp || (gradient.ExtendStart && gradient.ExtendEnd));

    public override void Shade(int y, int x, Span<PremulRgbaF32> row)
    {
        Span<float> t = stackalloc float[Chunk];
        PremulRgbaF32[]? table = useTable ? ramp.Table : null;
        for (int start = 0; start < row.Length; start += Chunk)
        {
            int n = Math.Min(Chunk, row.Length - start);
            geometry.Parameters(y, x + start, t[..n]);
            Span<PremulRgbaF32> part = row.Slice(start, n);
            for (int i = 0; i < n; i++)
            {
                float tiled = _tiling.Apply(t[i]);
                if (float.IsNaN(tiled))
                {
                    part[i] = default;
                }
                else
                {
                    part[i] = table is null ? ramp.Evaluate(tiled) : table[GradientTiling.Index(tiled)];
                }
            }
        }
    }
}

/// <summary>An 8-bit target's gradient: t picks one of the table's 1024 colors, already in the target's pixel type.</summary>
internal sealed class GradientPixelShader<TPixel> : PixelShader<TPixel>
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private readonly GradientGeometry _geometry;
    private readonly GradientTiling _tiling;
    private readonly TPixel[] _table;
    private readonly bool _opaque;

    internal GradientPixelShader(GradientGeometry geometry, GradientRamp ramp, Gradient gradient)
    {
        _geometry = geometry;
        _tiling = new GradientTiling(gradient.Tile, gradient.ExtendStart, gradient.ExtendEnd);
        _table = new TPixel[GradientRamp.TableSize];
        PixelConvert.Convert<PremulRgbaF32, TPixel>(ramp.Table, _table);
        _opaque = new GradientShader(geometry, ramp, gradient, false).IsOpaque;
    }

    public override bool IsOpaque => _opaque;

    public override void Shade(int y, int x, Span<TPixel> row)
    {
        Span<float> t = stackalloc float[Shader.Chunk];
        TPixel[] table = _table;
        for (int start = 0; start < row.Length; start += Shader.Chunk)
        {
            int n = Math.Min(Shader.Chunk, row.Length - start);
            _geometry.Parameters(y, x + start, t[..n]);
            Span<TPixel> part = row.Slice(start, n);
            for (int i = 0; i < n; i++)
            {
                int index = GradientTiling.Index(_tiling.Apply(t[i]));
                part[i] = index < 0 ? default : table[index];
            }
        }
    }
}

/// <summary>One premultiplied color everywhere.</summary>
internal sealed class PremulShader(PremulRgbaF32 color) : Shader
{
    public override bool IsOpaque => color.A >= 1;

    public override void Shade(int y, int x, Span<PremulRgbaF32> row) => row.Fill(color);
}
