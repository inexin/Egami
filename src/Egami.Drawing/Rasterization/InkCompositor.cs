using System.Buffers;

using Egami.Drawing.Shading;
using Egami.Operations;
using Egami.Operations.Compose;
using Egami.Pixels;

namespace Egami.Drawing.Rasterization;

/// <summary>
/// Composites straight ink tints onto an ink target's planes with core Egami's ink blend math (<see cref="InkBlend"/>), which the
/// PSD flattener shares: ISO 32000-2 section 11.3 for subtractive spaces, every mode through the RGB kernels on complements.
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
            Mix(target.Resolver, n, planes, tints, stride, alpha, backdrop is null ? coverage : default, opacity, mode, painted, d, r, x, y);
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
        float opacity, BlendMode mode, ReadOnlySpan<bool> painted, ReadOnlySpan<float> d, Span<float> r, int x, int y)
    {
        InkBlend.InkLayout layout = new(planes, resolver.ProcessPlanes, resolver.CmyPlanes, resolver.BlackPlane);
        InkBlend.Mix(layout, n, tints, stride, alpha, coverage, opacity, mode, painted, d, r, x, y);
    }
}

/// <summary>One colour (tints on every plane, an alpha) through coverage.</summary>
internal sealed class InkSolidSink : RasterSink
{
    private readonly InkTarget _target;
    private readonly float[] _tints;
    private readonly float _alpha;
    private readonly Paint _paint;
    private readonly bool[] _painted;

    // Source-over of 8-bit planes, every plane painted, outside a knockout group: plane by plane in place, with exactly the
    // arithmetic of InkCompositor's source-over, so the values are the same; an opaque fully covered run (or a Source one, as
    // Clear draws) is the colour itself.
    private readonly bool _fast;
    private readonly bool _source;

    internal InkSolidSink(InkTarget target, float[] tints, float alpha, Paint paint, ClipMask? mask, byte scale)
        : base(mask, scale)
    {
        _target = target;
        _tints = tints;
        _alpha = alpha;
        _paint = paint;
        _painted = target.Resolver.Painted(paint);
        _source = paint.BlendMode == BlendMode.Source;
        _fast = !target.Wide && target.KnockoutBackdrop is null && Array.TrueForAll(_painted, p => p);
    }

    // Source runs only for tints of 0 or 1, which the Compositor's complements give back exactly (Clear draws none).
    public override bool FillsDirectly => _fast && Plain
        && ((_source && Array.TrueForAll(_tints, t => t is 0 or 1)) || (_paint.BlendMode == BlendMode.SourceOver && _alpha * _paint.Opacity >= 1));

    protected override bool TryFill(int y, int x, int length)
    {
        if (!FillsDirectly)
        {
            return false;
        }

        // Source, or opaque source-over at full coverage: the plane becomes the colour (premultiplied by the source alpha).
        float a = _alpha * _paint.Opacity;
        for (int p = 0; p < _target.Inks; p++)
        {
            _target.Row8(p, y, x, length).Fill(InkTarget.To8(_tints[p] * a));
        }

        _target.Row8(_target.Inks, y, x, length).Fill(InkTarget.To8(a));
        return true;
    }

    protected override void Write(int y, int x, ReadOnlySpan<byte> coverage)
    {
        // Source through a clip: where coverage is full the plane becomes the colour and where none it stays, which the
        // Compositor gives exactly; runs of partial coverage (the clip's edges) go the general way.
        if (_fast && _source && Array.TrueForAll(_tints, t => t is 0 or 1))
        {
            float a = _alpha * _paint.Opacity;
            for (int i = 0; i < coverage.Length;)
            {
                int end = i + 1;
                bool partial = coverage[i] is not (0 or 255);
                while (end < coverage.Length && (coverage[end] is not (0 or 255)) == partial)
                {
                    end++;
                }

                if (partial)
                {
                    InkCompositor.Composite(_target, y, x + i, end - i, _tints, 0, [_alpha], coverage[i..end], _paint.Opacity, _paint.BlendMode, _painted);
                }
                else
                {
                    for (int p = 0; p <= _target.Inks; p++)
                    {
                        byte value = InkTarget.To8(p < _target.Inks ? _tints[p] * a : a);
                        Span<byte> row = _target.Row8(p, y, x + i, end - i);
                        for (int j = 0; j < row.Length; j++)
                        {
                            if (coverage[i + j] == 255)
                            {
                                row[j] = value;
                            }
                        }
                    }
                }

                i = end;
            }

            return;
        }

        if (!_fast || _paint.BlendMode != BlendMode.SourceOver)
        {
            InkCompositor.Composite(_target, y, x, coverage.Length, _tints, 0, [_alpha], coverage, _paint.Opacity, _paint.BlendMode, _painted, _target.KnockoutBackdrop as InkTarget);
            return;
        }

        // InkCompositor's source-over, plane by plane: s = alpha · opacity · coverage, then t·s + d·(1 − s) (alpha: 1 for t).
        int n = coverage.Length;
        float[] rented = ArrayPool<float>.Shared.Rent(n);
        try
        {
            Span<float> s = rented.AsSpan(0, n);
            float ao = _alpha * _paint.Opacity;
            for (int i = 0; i < n; i++)
            {
                s[i] = ao * (coverage[i] / 255f);
            }

            for (int p = 0; p <= _target.Inks; p++)
            {
                float t = p < _target.Inks ? _tints[p] : 1;
                Span<byte> row = _target.Row8(p, y, x, n);

                // A run already at a colour a byte holds exactly (an unpainted spot, opaque paper's alpha) stays as it is.
                byte exact = InkTarget.To8(t);
                if (exact / 255f == t && n >= 16 && row.IndexOfAnyExcept(exact) < 0)
                {
                    continue;
                }

                for (int i = 0; i < n; i++)
                {
                    row[i] = InkTarget.To8((t * s[i]) + (row[i] / 255f * (1 - s[i])));
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }
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
