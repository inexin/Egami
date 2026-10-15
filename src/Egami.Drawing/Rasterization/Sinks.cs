using System.Numerics;
using System.Runtime.InteropServices;

using Egami.Drawing.Shading;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Rasterization;

/// <summary>A clip's coverage, one byte per device pixel over <see cref="Bounds"/>; immutable once built, shared by saved states.</summary>
internal sealed class ClipMask(Rect bounds, System.Buffers.IMemoryOwner<byte> owner) : IDisposable
{
    internal Rect Bounds { get; } = bounds;

    internal Span<byte> Row(int y) => owner.Memory.Span.Slice((y - Bounds.Y) * Bounds.Width, Bounds.Width);

    internal Span<byte> All => owner.Memory.Span[..(Bounds.Width * Bounds.Height)];

    public void Dispose() => owner.Dispose();
}

/// <summary>Coverage arithmetic shared by the sinks.</summary>
internal static class CoverageMath
{
    /// <summary>Multiplies <paramref name="coverage"/> by <paramref name="mask"/> in place, as a·b/255 rounded.</summary>
    internal static void Multiply(Span<byte> coverage, ReadOnlySpan<byte> mask)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && coverage.Length >= Vector<byte>.Count)
        {
            Vector<ushort> half = new(128);
            for (; i + Vector<byte>.Count <= coverage.Length; i += Vector<byte>.Count)
            {
                Vector.Widen(new Vector<byte>(coverage[i..]), out Vector<ushort> a0, out Vector<ushort> a1);
                Vector.Widen(new Vector<byte>(mask[i..]), out Vector<ushort> b0, out Vector<ushort> b1);
                Vector<ushort> p0 = (a0 * b0) + half, p1 = (a1 * b1) + half;
                p0 = (p0 + Vector.ShiftRightLogical(p0, 8)) >>> 8;
                p1 = (p1 + Vector.ShiftRightLogical(p1, 8)) >>> 8;
                Vector.Narrow(p0, p1).CopyTo(coverage[i..]);
            }
        }

        for (; i < coverage.Length; i++)
        {
            int p = (coverage[i] * mask[i]) + 128;
            coverage[i] = (byte)((p + (p >> 8)) >> 8);
        }
    }

    /// <summary>Multiplies coverage by a constant 0..255.</summary>
    internal static void Scale(Span<byte> coverage, byte factor)
    {
        for (int i = 0; i < coverage.Length; i++)
        {
            int p = (coverage[i] * factor) + 128;
            coverage[i] = (byte)((p + (p >> 8)) >> 8);
        }
    }
}

/// <summary>The clip and coverage scale every raster sink applies before blending.</summary>
internal abstract class RasterSink(ClipMask? mask, byte scale) : ISpanSink
{
    /// <summary>Gets a value indicating whether a fully covered run needs no per-pixel coverage at all.</summary>
    protected bool Plain => mask is null && scale == 255;

    public virtual bool FillsDirectly => false;

    public void Blend(int y, int x, Span<byte> coverage)
    {
        if (mask is not null)
        {
            CoverageMath.Multiply(coverage, mask.Row(y).Slice(x - mask.Bounds.X, coverage.Length));
        }

        if (scale != 255)
        {
            CoverageMath.Scale(coverage, scale);
        }

        Write(y, x, coverage);
    }

    public void Fill(int y, int x, int length, byte coverage, Span<byte> scratch)
    {
        if (coverage == 255 && Plain && TryFill(y, x, length))
        {
            return;
        }

        Span<byte> row = scratch[..length];
        row.Fill(coverage);
        Blend(y, x, row);
    }

    /// <summary>Blends pixels through final coverage.</summary>
    protected abstract void Write(int y, int x, ReadOnlySpan<byte> coverage);

    /// <summary>Writes a fully covered, unclipped run directly, when the paint allows it.</summary>
    protected virtual bool TryFill(int y, int x, int length) => false;
}

/// <summary>Blends one color through coverage, with Compositor's solid-color kernels; an opaque source-over run is a plain fill.</summary>
internal sealed class SolidSink<TPixel> : RasterSink
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private readonly RasterTarget<TPixel> _target;
    private readonly BlendMode _mode;
    private readonly RgbaF32 _color;
    private readonly float _opacity;
    private readonly ColorSpace _colorSpace;
    private readonly bool _canFill;
    private readonly TPixel _solid;

    internal SolidSink(RasterTarget<TPixel> target, SolidPaint paint, ColorSpace colorSpace, ClipMask? mask, byte scale)
        : base(mask, scale)
    {
        _target = target;
        _mode = paint.BlendMode;
        _color = paint.Color;
        _opacity = paint.Opacity;
        _colorSpace = colorSpace;

        // Source-over with an opaque color in range, or Source with full opacity, leaves exactly the color under full coverage.
        RgbaF32 c = paint.Color;
        bool inRange = c.R is >= 0 and <= 1 && c.G is >= 0 and <= 1 && c.B is >= 0 and <= 1 && c.A is >= 0 and <= 1;
        _canFill = inRange && _opacity == 1 && ((_mode == BlendMode.SourceOver && c.A >= 1) || _mode == BlendMode.Source);
        if (_canFill)
        {
            // An 8-bit blend uses the color rounded to 8 bits; a direct write must match it.
            Span<TPixel> one = [default];
            if (EightBit)
            {
                PixelConvert.Convert<Rgba8, TPixel>([c.ToRgba8()], one);
            }
            else
            {
                PixelConvert.Convert<RgbaF32, TPixel>([c], one);
            }

            _solid = one[0];
        }
    }

    private static bool EightBit => TPixel.Layout.ChannelType == PixelChannelType.Unorm8;

    public override bool FillsDirectly => _canFill && Plain;

    protected override void Write(int y, int x, ReadOnlySpan<byte> coverage)
        => Compositor.Blend(_mode, _color, _target.Row(y, x, coverage.Length), coverage, _opacity, _colorSpace);

    protected override bool TryFill(int y, int x, int length)
    {
        if (!_canFill)
        {
            return false;
        }

        _target.Row(y, x, length).Fill(_solid);
        return true;
    }
}

/// <summary>
/// Blends a shaded paint through coverage: the shader fills a run of <typeparamref name="TSource"/> pixels, which Compositor blends
/// onto the target. <typeparamref name="TSource"/> is the target's own type when it has alpha; for a target without alpha it is
/// Rgba8 or RgbaF32, and the target's pixels are blended in that type and converted back, so transparent parts of the paint stay
/// transparent. A covered run of an opaque paint drawn source-over is shaded straight into the target.
/// </summary>
internal sealed class ShaderSink<TPixel, TSource> : RasterSink
    where TPixel : unmanaged, IPixelType<TPixel>
    where TSource : unmanaged, IPixelType<TSource>
{
    private readonly RasterTarget<TPixel> _target;
    private readonly PixelShader<TSource> _shader;
    private readonly BlendMode _mode;
    private readonly float _opacity;
    private readonly ColorSpace _colorSpace;
    private readonly bool _direct;

    internal ShaderSink(RasterTarget<TPixel> target, PixelShader<TSource> shader, Paint paint, ColorSpace colorSpace, ClipMask? mask, byte scale)
        : base(mask, scale)
    {
        _target = target;
        _shader = shader;
        _mode = paint.BlendMode;
        _opacity = paint.Opacity;
        _colorSpace = colorSpace;
        _direct = paint.Opacity == 1 && (paint.BlendMode == BlendMode.Source || (paint.BlendMode == BlendMode.SourceOver && shader.IsOpaque));
    }

    private static bool Same => typeof(TPixel) == typeof(TSource);

    public override bool FillsDirectly => Plain;

    protected override void Write(int y, int x, ReadOnlySpan<byte> coverage) => Run(y, x, coverage.Length, coverage);

    protected override bool TryFill(int y, int x, int length)
    {
        if (_direct && Same)
        {
            _shader.Shade(y, x, MemoryMarshal.Cast<TPixel, TSource>(_target.Row(y, x, length)));
            return true;
        }

        Run(y, x, length, default);
        return true;
    }

    private void Run(int y, int x, int length, ReadOnlySpan<byte> coverage)
    {
        Span<TSource> source = stackalloc TSource[Shader.Chunk];
        Span<TSource> under = Same ? default : stackalloc TSource[Shader.Chunk];
        Span<TPixel> row = _target.Row(y, x, length);
        for (int i = 0; i < length; i += Shader.Chunk)
        {
            int n = Math.Min(Shader.Chunk, length - i);
            Span<TSource> part = source[..n];
            _shader.Shade(y, x + i, part);
            ReadOnlySpan<byte> c = coverage.IsEmpty ? default : coverage.Slice(i, n);
            if (Same)
            {
                Blend(part, MemoryMarshal.Cast<TPixel, TSource>(row.Slice(i, n)), c);
                continue;
            }

            Span<TSource> dst = under[..n];
            PixelConvert.Convert<TPixel, TSource>(row.Slice(i, n), dst);
            Blend(part, dst, c);
            PixelConvert.Convert<TSource, TPixel>(dst, row.Slice(i, n));
        }
    }

    private void Blend(ReadOnlySpan<TSource> source, Span<TSource> destination, ReadOnlySpan<byte> coverage)
    {
        if (coverage.IsEmpty)
        {
            Compositor.Blend(_mode, source, destination, _opacity, _colorSpace);
        }
        else
        {
            Compositor.Blend(_mode, source, destination, coverage, _opacity, _colorSpace);
        }
    }
}

/// <summary>Blends rows of an image already placed in device space (pixel (x, y) at <c>Origin</c> + (x, y)) through coverage.</summary>
internal sealed class ImageSink<TPixel>(
    RasterTarget<TPixel> target, Raster<TPixel> placed, Point origin, BlendMode mode, float opacity, ColorSpace colorSpace, ClipMask? mask)
    : RasterSink(mask, 255)
    where TPixel : unmanaged, IPixelType<TPixel>
{
    public override bool FillsDirectly => Plain;

    protected override void Write(int y, int x, ReadOnlySpan<byte> coverage)
    {
        ReadOnlySpan<TPixel> source = placed.Row(y - origin.Y).Slice(x - origin.X, coverage.Length);
        Compositor.Blend(mode, source, target.Row(y, x, coverage.Length), coverage, opacity, colorSpace);
    }

    protected override bool TryFill(int y, int x, int length)
    {
        ReadOnlySpan<TPixel> source = placed.Row(y - origin.Y).Slice(x - origin.X, length);
        Compositor.Blend(mode, source, target.Row(y, x, length), opacity, colorSpace);
        return true;
    }
}

/// <summary>Writes coverage into a plain buffer laid out over a device rectangle.</summary>
internal sealed class BufferSink(Memory<byte> buffer, Rect bounds) : ISpanSink
{
    public bool FillsDirectly => true;

    public void Blend(int y, int x, Span<byte> coverage) => coverage.CopyTo(Row(y).Slice(x - bounds.X));

    public void Fill(int y, int x, int length, byte coverage, Span<byte> scratch) => Row(y).Slice(x - bounds.X, length).Fill(coverage);

    private Span<byte> Row(int y) => buffer.Span.Slice((y - bounds.Y) * bounds.Width, bounds.Width);
}

/// <summary>
/// Draws into a knockout layer: the inner sink composites each run fully onto a copy of the layer's first content, and the run's
/// coverage (with the clip's) then mixes that result with what the layer holds, as PDF's knockout groups do.
/// </summary>
internal sealed class KnockoutSink<TPixel>(RasterTarget<TPixel> target, RasterTarget backdrop, ISpanSink inner, ClipMask? mask, byte scale) : ISpanSink
    where TPixel : unmanaged, IPixelType<TPixel>
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
        int n = coverage.Length;
        if (mask is not null)
        {
            CoverageMath.Multiply(coverage, mask.Row(y).Slice(x - mask.Bounds.X, n));
        }

        if (scale != 255)
        {
            CoverageMath.Scale(coverage, scale);
        }

        Span<TPixel> row = target.Row(y, x, n);
        PremulRgbaF32[] before = System.Buffers.ArrayPool<PremulRgbaF32>.Shared.Rent(n), after = System.Buffers.ArrayPool<PremulRgbaF32>.Shared.Rent(n);
        byte[] full = System.Buffers.ArrayPool<byte>.Shared.Rent(n);
        try
        {
            PixelConvert.Convert<TPixel, PremulRgbaF32>(row, before.AsSpan(0, n));
            backdrop.ReadRow(y, x, row);
            full.AsSpan(0, n).Fill(255);
            inner.Blend(y, x, full.AsSpan(0, n));
            PixelConvert.Convert<TPixel, PremulRgbaF32>(row, after.AsSpan(0, n));
            for (int i = 0; i < n; i++)
            {
                float c = coverage[i] / 255f;
                PremulRgbaF32 a = before[i], b = after[i];
                after[i] = new PremulRgbaF32(a.R + ((b.R - a.R) * c), a.G + ((b.G - a.G) * c), a.B + ((b.B - a.B) * c), a.A + ((b.A - a.A) * c));
            }

            PixelConvert.Convert<PremulRgbaF32, TPixel>(after.AsSpan(0, n), row);
        }
        finally
        {
            System.Buffers.ArrayPool<PremulRgbaF32>.Shared.Return(before);
            System.Buffers.ArrayPool<PremulRgbaF32>.Shared.Return(after);
            System.Buffers.ArrayPool<byte>.Shared.Return(full);
        }
    }
}

/// <summary>Multiplies each run's coverage by an image mask's alpha before passing it on: <see cref="Canvas.DrawImageMask"/>'s sink.</summary>
internal sealed class MaskedSink(ISpanSink inner, Shader mask) : ISpanSink
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
        Span<PremulRgbaF32> alpha = stackalloc PremulRgbaF32[Shader.Chunk];
        for (int i = 0; i < coverage.Length; i += Shader.Chunk)
        {
            int n = Math.Min(Shader.Chunk, coverage.Length - i);
            mask.Shade(y, x + i, alpha[..n]);
            for (int k = 0; k < n; k++)
            {
                coverage[i + k] = (byte)MathF.Round(coverage[i + k] * Math.Clamp(alpha[k].A, 0, 1));
            }
        }

        inner.Blend(y, x, coverage);
    }
}

/// <summary>
/// Draws into a non-isolated layer that merges with a blend mode: each run is blended as usual, and the alpha it would have on its
/// own, found by blending it once onto transparency first, is added to the layer's group alpha.
/// </summary>
internal sealed class GroupAlphaSink<TPixel>(RasterTarget<TPixel> target, ISpanSink inner) : ISpanSink
    where TPixel : unmanaged, IPixelType<TPixel>
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
        int n = coverage.Length;
        Span<TPixel> row = target.Row(y, x, n);
        TPixel[] saved = System.Buffers.ArrayPool<TPixel>.Shared.Rent(n);
        byte[] alone = System.Buffers.ArrayPool<byte>.Shared.Rent(n);
        PremulRgbaF32[] own = System.Buffers.ArrayPool<PremulRgbaF32>.Shared.Rent(n);
        float[] alpha = System.Buffers.ArrayPool<float>.Shared.Rent(n);
        try
        {
            row.CopyTo(saved);
            coverage.CopyTo(alone);
            row.Clear();
            inner.Blend(y, x, alone.AsSpan(0, n));
            PixelConvert.Convert<TPixel, PremulRgbaF32>(row, own.AsSpan(0, n));
            for (int i = 0; i < n; i++)
            {
                alpha[i] = own[i].A;
            }

            saved.AsSpan(0, n).CopyTo(row);
            inner.Blend(y, x, coverage);
            target.AddGroupAlpha(y, x, alpha.AsSpan(0, n));
        }
        finally
        {
            System.Buffers.ArrayPool<TPixel>.Shared.Return(saved);
            System.Buffers.ArrayPool<byte>.Shared.Return(alone);
            System.Buffers.ArrayPool<PremulRgbaF32>.Shared.Return(own);
            System.Buffers.ArrayPool<float>.Shared.Return(alpha);
        }
    }
}
