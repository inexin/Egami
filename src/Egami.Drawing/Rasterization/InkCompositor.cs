using System.Buffers;

using Egami.Drawing.Shading;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Rasterization;

/// <summary>
/// Composites straight ink tints onto an ink target's planes, as ISO 32000-2 section 11.3 has it for subtractive spaces. Planes
/// hold ink premultiplied by alpha; blending works on the complement 1 − tint, so Multiply darkens as on screen. Source-over is
/// done here per plane; every other mode goes through Egami's <see cref="Compositor"/>, three planes at a time as the red, green
/// and blue of a straight colour, so each mode's arithmetic is exactly the RGB canvas's. Hue, Saturation, Color and Luminosity
/// take Cyan, Magenta and Yellow as one colour; Black then comes from the backdrop (from the source for Luminosity), and spots
/// and extra process inks blend Normal (PDF allows only separable modes on spots). Difference and Exclusion blend spots Normal
/// too, as Ghostscript and MuPDF do: on complements they would print full ink where neither side has any. A plane a paint does not paint (overprint)
/// keeps its colour, its value only rescaled to the new alpha.
/// </summary>
internal static class InkCompositor
{
    /// <summary>
    /// Composites <paramref name="n"/> pixels from device (<paramref name="x"/>, <paramref name="y"/>). <paramref name="tints"/>
    /// holds every plane's straight tint per pixel (<paramref name="stride"/> = the plane count), or one colour for all
    /// (<paramref name="stride"/> = 0); <paramref name="alpha"/> the source alpha per pixel, or one for all; <paramref name="coverage"/>
    /// one byte per pixel, or empty for full; <paramref name="painted"/> one flag per plane, or empty for every plane. With a
    /// <paramref name="backdrop"/> (a knockout layer's first content), the source composites onto it at full coverage and the
    /// result replaces the planes by coverage.
    /// </summary>
    internal static void Composite(
        InkTarget target, int y, int x, int n, ReadOnlySpan<float> tints, int stride, ReadOnlySpan<float> alpha, ReadOnlySpan<byte> coverage,
        float opacity, BlendMode mode, ReadOnlySpan<bool> painted, InkTarget? backdrop = null)
    {
        int planes = target.Inks;
        float[] under = ArrayPool<float>.Shared.Rent(n * (planes + 1));
        float[] result = ArrayPool<float>.Shared.Rent(n * (planes + 1));
        try
        {
            Span<float> d = under.AsSpan(0, n * (planes + 1)), r = result.AsSpan(0, n * (planes + 1));
            (backdrop ?? target).ReadAll(y, x, n, d);
            Mix(target.Resolver, n, planes, tints, stride, alpha, backdrop is null ? coverage : default, opacity, mode, painted, d, r);
            if (backdrop is not null)
            {
                // Knockout: what the plane held, mixed toward the composite by coverage.
                target.ReadAll(y, x, n, d);
                for (int i = 0; i < n; i++)
                {
                    float c = coverage.IsEmpty ? 1 : coverage[i] / 255f;
                    for (int p = 0; p <= planes; p++)
                    {
                        int at = (p * n) + i;
                        r[at] = d[at] + ((r[at] - d[at]) * c);
                    }
                }
            }

            target.WriteAll(y, x, n, r);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(under);
            ArrayPool<float>.Shared.Return(result);
        }
    }

    // Planes are laid out plane by plane, n values each, alpha last; values premultiplied, 0 to 1.
    private static void Mix(
        InkResolver resolver, int n, int planes, ReadOnlySpan<float> tints, int stride, ReadOnlySpan<float> alpha, ReadOnlySpan<byte> coverage,
        float opacity, BlendMode mode, ReadOnlySpan<bool> painted, ReadOnlySpan<float> d, Span<float> r)
    {
        ReadOnlySpan<float> ad = d.Slice(planes * n, n);
        Span<float> ar = r.Slice(planes * n, n);
        if (mode == BlendMode.SourceOver)
        {
            for (int i = 0; i < n; i++)
            {
                float s = Alpha(alpha, i) * opacity * Cover(coverage, i);
                ar[i] = s + (ad[i] * (1 - s));
            }

            for (int p = 0; p < planes; p++)
            {
                bool paints = painted.IsEmpty || painted[p];
                for (int i = 0; i < n; i++)
                {
                    int at = (p * n) + i;
                    if (paints)
                    {
                        float s = Alpha(alpha, i) * opacity * Cover(coverage, i);
                        r[at] = (Tint(tints, stride, i, p) * s) + (d[at] * (1 - s));
                    }
                    else
                    {
                        r[at] = ad[i] > 0 ? ar[i] * Math.Clamp(d[at] / ad[i], 0, 1) : 0;
                    }
                }
            }

            return;
        }

        bool nonSeparable = mode is BlendMode.Hue or BlendMode.Saturation or BlendMode.Color or BlendMode.Luminosity;
        int[]? cmy = resolver.CmyPlanes;
        int black = resolver.BlackPlane;
        bool[] done = new bool[planes];
        bool alphaSet = false;
        RgbaF32[] src = ArrayPool<RgbaF32>.Shared.Rent(n), dst = ArrayPool<RgbaF32>.Shared.Rent(n);
        try
        {
            // Triples through the Compositor: Cyan, Magenta and Yellow for the non-separable modes, every plane otherwise.
            List<int[]> triples = [];
            if (nonSeparable)
            {
                if (cmy is not null)
                {
                    triples.Add(cmy);
                }
            }
            else
            {
                // Difference and Exclusion would put full ink where neither side has any; on spots they blend Normal, as
                // Ghostscript and MuPDF have them.
                int blended = mode is BlendMode.Difference or BlendMode.Exclusion ? resolver.ProcessPlanes : planes;
                for (int p = 0; p < blended; p += 3)
                {
                    triples.Add([p, Math.Min(p + 1, blended - 1), Math.Min(p + 2, blended - 1)]);
                }
            }

            foreach (int[] triple in triples)
            {
                for (int i = 0; i < n; i++)
                {
                    src[i] = new RgbaF32(1 - Tint(tints, stride, i, triple[0]), 1 - Tint(tints, stride, i, triple[1]), 1 - Tint(tints, stride, i, triple[2]), Alpha(alpha, i));
                    dst[i] = new RgbaF32(Colour(d, ad, n, triple[0], i), Colour(d, ad, n, triple[1], i), Colour(d, ad, n, triple[2], i), ad[i]);
                }

                if (coverage.IsEmpty)
                {
                    Compositor.Blend(mode, src.AsSpan(0, n), dst.AsSpan(0, n), opacity, ColorSpace.Encoded);
                }
                else
                {
                    Compositor.Blend(mode, src.AsSpan(0, n), dst.AsSpan(0, n), coverage, opacity, ColorSpace.Encoded);
                }

                for (int i = 0; i < n; i++)
                {
                    RgbaF32 c = dst[i];
                    float a = Math.Clamp(c.A, 0, 1);
                    if (!alphaSet)
                    {
                        ar[i] = a;
                    }

                    r[(triple[0] * n) + i] = a * Math.Clamp(1 - c.R, 0, 1);
                    r[(triple[1] * n) + i] = a * Math.Clamp(1 - c.G, 0, 1);
                    r[(triple[2] * n) + i] = a * Math.Clamp(1 - c.B, 0, 1);
                }

                alphaSet = true;
                foreach (int p in triple)
                {
                    done[p] = true;
                }
            }

            // The rest: Normal, or Black from the backdrop under Hue, Saturation and Color.
            for (int p = 0; p < planes; p++)
            {
                if (done[p])
                {
                    continue;
                }

                bool keepBackdrop = nonSeparable && p == black && mode != BlendMode.Luminosity && cmy is not null;
                for (int i = 0; i < n; i++)
                {
                    float c = Cover(coverage, i), sa = Alpha(alpha, i) * opacity, ba = ad[i];
                    float cb = Colour(d, ad, n, p, i), cs = 1 - Tint(tints, stride, i, p);
                    float blended = keepBackdrop ? cb : cs;
                    float full = sa + ba - (sa * ba);
                    float colour = ((1 - sa) * ba * cb) + ((1 - ba) * sa * cs) + (sa * ba * blended);
                    float resultAlpha = ((1 - c) * ba) + (c * full);
                    float resultColour = ((1 - c) * ba * cb) + (c * colour);
                    if (!alphaSet)
                    {
                        ar[i] = resultAlpha;
                    }

                    r[(p * n) + i] = Math.Clamp(resultAlpha - resultColour, 0, 1);
                }

                alphaSet = true;
            }
        }
        finally
        {
            ArrayPool<RgbaF32>.Shared.Return(src);
            ArrayPool<RgbaF32>.Shared.Return(dst);
        }

        // Planes the paint leaves alone keep their colour under the new alpha.
        if (!painted.IsEmpty)
        {
            for (int p = 0; p < planes; p++)
            {
                if (!painted[p])
                {
                    for (int i = 0; i < n; i++)
                    {
                        int at = (p * n) + i;
                        r[at] = ad[i] > 0 ? ar[i] * Math.Clamp(d[at] / ad[i], 0, 1) : 0;
                    }
                }
            }
        }
    }

    private static float Tint(ReadOnlySpan<float> tints, int stride, int i, int p) => tints[(stride * i) + p];

    private static float Alpha(ReadOnlySpan<float> alpha, int i) => alpha.Length == 1 ? alpha[0] : alpha[i];

    private static float Cover(ReadOnlySpan<byte> coverage, int i) => coverage.IsEmpty ? 1 : coverage[i] / 255f;

    // A plane's straight complement (1 − tint): paper where nothing is drawn.
    private static float Colour(ReadOnlySpan<float> d, ReadOnlySpan<float> ad, int n, int p, int i) => ad[i] > 0 ? 1 - Math.Clamp(d[(p * n) + i] / ad[i], 0, 1) : 1;
}

/// <summary>One colour (tints on every plane, an alpha) through coverage.</summary>
internal sealed class InkSolidSink : RasterSink
{
    private readonly InkTarget _target;
    private readonly float[] _tints;
    private readonly float _alpha;
    private readonly Paint _paint;
    private readonly bool[] _painted;

    // Source-over or Source of 8-bit planes, every plane painted, outside a knockout group: plane by plane in place, with the
    // arithmetic of InkCompositor's source-over (and Source as coverage between the backdrop and the colour).
    private readonly bool _fast;
    private readonly bool _source;
    private readonly float _sourceAlpha;
    private readonly float[] _premultiplied;

    internal InkSolidSink(InkTarget target, float[] tints, float alpha, Paint paint, ClipMask? mask, byte scale)
        : base(mask, scale)
    {
        _target = target;
        _tints = tints;
        _alpha = alpha;
        _paint = paint;
        _painted = target.Resolver.Painted(paint);
        _source = paint.BlendMode == BlendMode.Source;
        _fast = (_source || paint.BlendMode == BlendMode.SourceOver) && !target.Wide && target.KnockoutBackdrop is null && Array.TrueForAll(_painted, p => p);
        _sourceAlpha = Math.Clamp(alpha * paint.Opacity, 0, 1);
        _premultiplied = new float[target.Inks + 1];
        for (int p = 0; p < target.Inks; p++)
        {
            _premultiplied[p] = tints[p] * _sourceAlpha;
        }

        _premultiplied[target.Inks] = _sourceAlpha;

        // Byte weights are exact when the alpha is a whole number of 255ths (as an 8-bit colour's is); else plain floats.
        float levels = _sourceAlpha * 255;
        _exact = _source || MathF.Abs(levels - MathF.Round(levels)) < 1e-3f;
        _weight = (int)MathF.Round(levels);
    }

    private readonly bool _exact;
    private readonly int _weight;

    public override bool FillsDirectly => _fast && Plain;

    protected override bool TryFill(int y, int x, int length)
    {
        if (!_fast)
        {
            return false;
        }

        if (_source || _sourceAlpha >= 1)
        {
            // The result is the colour itself.
            for (int p = 0; p <= _target.Inks; p++)
            {
                _target.Row8(p, y, x, length).Fill(InkTarget.To8(_premultiplied[p]));
            }

            return true;
        }

        if (!_exact)
        {
            return false;
        }

        byte weight = InkTarget.To8(_sourceAlpha);
        for (int p = 0; p <= _target.Inks; p++)
        {
            PlaneMath.Lerp(_target.Row8(p, y, x, length), Colour(p), weight);
        }

        return true;
    }

    protected override void Write(int y, int x, ReadOnlySpan<byte> coverage)
    {
        if (!_fast || !_exact)
        {
            InkCompositor.Composite(_target, y, x, coverage.Length, _tints, 0, [_alpha], coverage, _paint.Opacity, _paint.BlendMode, _painted, _target.KnockoutBackdrop as InkTarget);
            return;
        }

        int n = coverage.Length;
        byte[]? rented = null;
        bool scale = !_source && _weight < 255;
        Span<byte> scaled = !scale ? default : n <= 1024 ? stackalloc byte[n] : (rented = ArrayPool<byte>.Shared.Rent(n)).AsSpan(0, n);
        for (int i = 0; i < scaled.Length; i++)
        {
            int w = (coverage[i] * _weight) + 128;
            scaled[i] = (byte)((w + (w >> 8)) >> 8);
        }

        ReadOnlySpan<byte> weights = scale ? scaled : coverage;

        for (int p = 0; p <= _target.Inks; p++)
        {
            PlaneMath.Lerp(_target.Row8(p, y, x, n), Colour(p), weights);
        }

        if (rented is not null)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    // What a plane moves toward with full weight: the premultiplied colour for Source, the straight tint (1 for alpha) for
    // source-over, whose weight carries the alpha.
    private byte Colour(int p) => InkTarget.To8(_source ? _premultiplied[p] : p < _target.Inks ? _tints[p] : 1);
}

/// <summary>A shaded RGB paint: each run is shaded in straight RGBA, converted to the process inks, and composited.</summary>
internal sealed class InkShaderSink(InkTarget target, PixelShader<RgbaF32> shader, Paint paint, ClipMask? mask, byte scale) : RasterSink(mask, scale)
{
    private readonly bool[] _painted = target.Resolver.Painted(paint);

    public override bool FillsDirectly => Plain;

    protected override void Write(int y, int x, ReadOnlySpan<byte> coverage) => Run(y, x, coverage.Length, coverage);

    protected override bool TryFill(int y, int x, int length)
    {
        Run(y, x, length, default);
        return true;
    }

    private void Run(int y, int x, int length, ReadOnlySpan<byte> coverage)
    {
        int inks = target.Inks;
        Span<RgbaF32> colors = stackalloc RgbaF32[Shader.Chunk];
        float[] tints = new float[Shader.Chunk * inks];
        Span<float> a = stackalloc float[Shader.Chunk];
        for (int i = 0; i < length; i += Shader.Chunk)
        {
            int n = Math.Min(Shader.Chunk, length - i);
            shader.Shade(y, x + i, colors[..n]);
            target.Resolver.ResolveRgb(colors[..n], tints.AsSpan(0, n * inks));
            for (int j = 0; j < n; j++)
            {
                a[j] = Math.Clamp(colors[j].A, 0, 1);
            }

            InkCompositor.Composite(target, y, x + i, n, tints, inks, a[..n], coverage.IsEmpty ? default : coverage.Slice(i, n), paint.Opacity, paint.BlendMode, _painted, target.KnockoutBackdrop as InkTarget);
        }
    }
}

/// <summary>
/// Draws into a non-isolated layer that merges with a blend mode: each run is drawn once onto nothing, to learn the alpha it has
/// alone, which joins the layer's group alpha, and then drawn as usual.
/// </summary>
internal sealed class InkGroupAlphaSink(InkTarget target, ISpanSink inner) : ISpanSink
{
    public bool FillsDirectly => false;

    public void Fill(int y, int x, int length, byte coverage, Span<byte> scratch)
    {
        Span<byte> row = scratch[..length];
        row.Fill(coverage);
        Blend(y, x, row);
    }

    public void Blend(int y, int x, Span<byte> coverage)
    {
        int n = coverage.Length, planes = target.Inks;
        float[] saved = ArrayPool<float>.Shared.Rent(n * (planes + 1));
        float[] blank = ArrayPool<float>.Shared.Rent(n * (planes + 1));
        byte[] alone = ArrayPool<byte>.Shared.Rent(n);
        float[] alpha = ArrayPool<float>.Shared.Rent(n);
        try
        {
            Span<float> run = saved.AsSpan(0, n * (planes + 1));
            target.ReadAll(y, x, n, run);
            blank.AsSpan(0, n * (planes + 1)).Clear();
            target.WriteAll(y, x, n, blank.AsSpan(0, n * (planes + 1)));
            coverage.CopyTo(alone);
            inner.Blend(y, x, alone.AsSpan(0, n));
            target.ReadPlane(planes, y, x, alpha.AsSpan(0, n));
            target.WriteAll(y, x, n, run);
            inner.Blend(y, x, coverage);
            target.AddGroupAlpha(y, x, alpha.AsSpan(0, n));
        }
        finally
        {
            ArrayPool<float>.Shared.Return(saved);
            ArrayPool<float>.Shared.Return(blank);
            ArrayPool<byte>.Shared.Return(alone);
            ArrayPool<float>.Shared.Return(alpha);
        }
    }
}

/// <summary>8-bit plane arithmetic: d + (t − d)·w / 255 rounded, as (t·w + d·(255 − w)) / 255, exact in 16 bits.</summary>
internal static class PlaneMath
{
    internal static void Lerp(Span<byte> row, byte target, ReadOnlySpan<byte> weights)
    {
        // A run already at the target (an unpainted spot, opaque paper's alpha) stays; checking is cheaper than writing, but
        // not for a few pixels.
        if (row.Length >= 16 && row.IndexOfAnyExcept(target) < 0)
        {
            return;
        }

        int i = 0;
        if (System.Numerics.Vector.IsHardwareAccelerated)
        {
            System.Numerics.Vector<ushort> t = new(target), full = new(255), half = new(128);
            for (; i <= row.Length - System.Numerics.Vector<byte>.Count; i += System.Numerics.Vector<byte>.Count)
            {
                System.Numerics.Vector.Widen(new System.Numerics.Vector<byte>(row[i..]), out System.Numerics.Vector<ushort> dl, out System.Numerics.Vector<ushort> dh);
                System.Numerics.Vector.Widen(new System.Numerics.Vector<byte>(weights[i..]), out System.Numerics.Vector<ushort> wl, out System.Numerics.Vector<ushort> wh);
                System.Numerics.Vector.Narrow(Divide((t * wl) + (dl * (full - wl)) + half), Divide((t * wh) + (dh * (full - wh)) + half)).CopyTo(row[i..]);
            }
        }

        for (; i < row.Length; i++)
        {
            int v = (target * weights[i]) + (row[i] * (255 - weights[i])) + 128;
            row[i] = (byte)((v + (v >> 8)) >> 8);
        }
    }

    internal static void Lerp(Span<byte> row, byte target, byte weight)
    {
        if (row.Length >= 16 && row.IndexOfAnyExcept(target) < 0)
        {
            return;
        }

        int i = 0;
        int add = (target * weight) + 128, keep = 255 - weight;
        if (System.Numerics.Vector.IsHardwareAccelerated)
        {
            System.Numerics.Vector<ushort> a = new((ushort)add), k = new((ushort)keep);
            for (; i <= row.Length - System.Numerics.Vector<byte>.Count; i += System.Numerics.Vector<byte>.Count)
            {
                System.Numerics.Vector.Widen(new System.Numerics.Vector<byte>(row[i..]), out System.Numerics.Vector<ushort> dl, out System.Numerics.Vector<ushort> dh);
                System.Numerics.Vector.Narrow(Divide((dl * k) + a), Divide((dh * k) + a)).CopyTo(row[i..]);
            }
        }

        for (; i < row.Length; i++)
        {
            int v = (row[i] * keep) + add;
            row[i] = (byte)((v + (v >> 8)) >> 8);
        }
    }

    // v / 255 for v = x + 128 ≤ 65153: (v + (v >> 8)) >> 8.
    private static System.Numerics.Vector<ushort> Divide(System.Numerics.Vector<ushort> v) => (v + (v >> 8)) >> 8;
}
