using System.Numerics;

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
    private readonly PixelPlane<TPixel> _plane;
    private readonly BlendMode _mode;
    private readonly RgbaF32 _color;
    private readonly float _opacity;
    private readonly ColorSpace _colorSpace;
    private readonly bool _canFill;
    private readonly TPixel _solid;

    internal SolidSink(PixelPlane<TPixel> plane, SolidPaint paint, ColorSpace colorSpace, ClipMask? mask, byte scale)
        : base(mask, scale)
    {
        _plane = plane;
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
        => Compositor.Blend(_mode, _color, _plane.Row(y).Slice(x, coverage.Length), coverage, _opacity, _colorSpace);

    protected override bool TryFill(int y, int x, int length)
    {
        if (!_canFill)
        {
            return false;
        }

        _plane.Row(y).Slice(x, length).Fill(_solid);
        return true;
    }
}

/// <summary>Blends rows of an image already placed in device space (pixel (x, y) at <c>Origin</c> + (x, y)) through coverage.</summary>
internal sealed class ImageSink<TPixel>(
    PixelPlane<TPixel> plane, Raster<TPixel> placed, Point origin, BlendMode mode, float opacity, ColorSpace colorSpace, ClipMask? mask)
    : RasterSink(mask, 255)
    where TPixel : unmanaged, IPixelType<TPixel>
{
    public override bool FillsDirectly => Plain;

    protected override void Write(int y, int x, ReadOnlySpan<byte> coverage)
    {
        ReadOnlySpan<TPixel> source = placed.Row(y - origin.Y).Slice(x - origin.X, coverage.Length);
        Compositor.Blend(mode, source, plane.Row(y).Slice(x, coverage.Length), coverage, opacity, colorSpace);
    }

    protected override bool TryFill(int y, int x, int length)
    {
        ReadOnlySpan<TPixel> source = placed.Row(y - origin.Y).Slice(x - origin.X, length);
        Compositor.Blend(mode, source, plane.Row(y).Slice(x, length), opacity, colorSpace);
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
