using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Operations.Compose;

/// <summary>
/// Blends rows onto a pixel type with a <see cref="BlendMode"/>. Premultiplied 8-bit destinations go straight to the 8-bit
/// kernels, and straight Rgba8 and Bgra8 ones are premultiplied on the way in and divided back on the way out; there the source
/// is read in the destination's own type. Every other type, and every type in linear light, blends in the float working form
/// (RGB, premultiplied), with the source read at full precision as <see cref="RgbaF32"/>, so a gray or 5-6-5 destination blends
/// with the source's real colors. Scratch rows come from the shared pool, so a call allocates nothing once the pool is warm.
/// </summary>
internal readonly struct BlendRows<TPixel>
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private static readonly bool Premul8 = RowConverter<TPixel>.Premul8;
    private static readonly bool Straight8 = RowConverter<TPixel>.FourByte;
    private static readonly bool Bgr = typeof(TPixel) == typeof(PremulBgra8) || typeof(TPixel) == typeof(Bgra8);
    private static readonly RowConverter<TPixel> Encoded = new(null, inksAsRgb: true);
    private static readonly RowConverter<TPixel> SrgbLinear = new(Colors.LinearCurve.Srgb, inksAsRgb: true);
    private static readonly RowConverter<RgbaF32> EncodedSource = new(null);
    private static readonly RowConverter<RgbaF32> SrgbLinearSource = new(Colors.LinearCurve.Srgb);

    private readonly BlendMode _mode;
    private readonly float _opacity;
    private readonly int _opacity255;
    private readonly RowConverter<TPixel>? _converter;
    private readonly RowConverter<RgbaF32>? _source;

    /// <param name="mode">The mode.</param>
    /// <param name="opacity">Scales the source, 0 to 1.</param>
    /// <param name="curve">The curve to blend in linear light with, or <see langword="null"/> to blend the stored values.</param>
    internal BlendRows(BlendMode mode, float opacity, Colors.LinearCurve? curve)
    {
        _mode = mode;
        _opacity = opacity;
        _opacity255 = (int)MathF.Round(opacity * 255);
        UsesBytes = curve is null && (Premul8 || Straight8);
        if (!UsesBytes)
        {
            (_converter, _source) = curve is null ? (Encoded, EncodedSource)
                : ReferenceEquals(curve, Colors.LinearCurve.Srgb) ? (SrgbLinear, SrgbLinearSource)
                : (new RowConverter<TPixel>(curve, inksAsRgb: true), new RowConverter<RgbaF32>(curve));
        }
    }

    /// <summary>Gets whether this blend runs on 8-bit rows of the destination's type; otherwise sources are read as <see cref="RgbaF32"/>.</summary>
    internal bool UsesBytes { get; }

    /// <summary>Gets the 8-bit premultiplied form of a straight color in this type's channel order.</summary>
    internal static uint Solid8(Rgba8 color)
    {
        PremulRgba8 p = PremulRgba8.FromRgba8(color);
        return Bgr ? p.B | ((uint)p.G << 8) | ((uint)p.R << 16) | ((uint)p.A << 24) : p.R | ((uint)p.G << 8) | ((uint)p.B << 16) | ((uint)p.A << 24);
    }

    /// <summary>Blends <paramref name="source"/> onto <paramref name="destination"/>, both the same length.</summary>
    internal void Blend(ReadOnlySpan<TPixel> source, Span<TPixel> destination, ReadOnlySpan<byte> coverage = default)
    {
        if (UsesBytes && Premul8)
        {
            BlendKernels.Blend8(_mode, MemoryMarshal.Cast<TPixel, uint>(source), MemoryMarshal.Cast<TPixel, uint>(destination), coverage, _opacity255, Bgr);
            return;
        }

        if (UsesBytes)
        {
            // The kernel premultiplies the straight source as it loads it.
            ReadOnlySpan<uint> s = MemoryMarshal.Cast<TPixel, uint>(source);
            if (Opaque(destination))
            {
                // An opaque pixel is its own premultiplied form: blend the row where it is, and divide back in place only when the
                // mode can leave it less than opaque.
                Span<uint> d = MemoryMarshal.Cast<TPixel, uint>(destination);
                BlendKernels.Blend8(_mode, s, d, coverage, _opacity255, Bgr, straightSource: true);
                if (!KeepsOpaque(_mode))
                {
                    Unpremultiply(d, destination);
                }

                return;
            }

            uint[] buffer = ArrayPool<uint>.Shared.Rent(destination.Length);
            try
            {
                Span<uint> d = buffer.AsSpan(0, destination.Length);
                Premultiply(destination, d);
                BlendKernels.Blend8(_mode, s, d, coverage, _opacity255, Bgr, straightSource: true);
                Unpremultiply(d, destination);
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(buffer);
            }

            return;
        }

        RgbaF32[] straight = ArrayPool<RgbaF32>.Shared.Rent(source.Length);
        try
        {
            Span<RgbaF32> row = straight.AsSpan(0, source.Length);
            PixelConvert.Convert(source, row);
            BlendStraight(row, destination, coverage);
        }
        finally
        {
            ArrayPool<RgbaF32>.Shared.Return(straight);
        }
    }

    /// <summary>Blends straight float <paramref name="source"/> pixels onto <paramref name="destination"/>; only when not <see cref="UsesBytes"/>.</summary>
    internal void BlendStraight(ReadOnlySpan<RgbaF32> source, Span<TPixel> destination, ReadOnlySpan<byte> coverage = default)
    {
        float[] floats = ArrayPool<float>.Shared.Rent(8 * destination.Length);
        try
        {
            int n = 4 * destination.Length;
            Span<float> s = floats.AsSpan(0, source.Length * 4), d = floats.AsSpan(n, n);
            _source!.Load(source, s);
            _converter!.Load(destination, d);
            BlendKernels.BlendFloat(_mode, s, d, coverage, _opacity);
            _converter.Store(d, destination, opaque: false);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(floats);
        }
    }

    /// <summary>Blends one straight color onto every pixel of <paramref name="destination"/>.</summary>
    internal void BlendSolid(Rgba8 color, Span<TPixel> destination, ReadOnlySpan<byte> coverage = default)
    {
        if (UsesBytes)
        {
            uint solid = Solid8(color);
            ReadOnlySpan<uint> one = new(in solid);
            if (Premul8)
            {
                BlendKernels.Blend8(_mode, one, MemoryMarshal.Cast<TPixel, uint>(destination), coverage, _opacity255, Bgr);
                return;
            }

            if (KeepsOpaque(_mode) && Opaque(destination))
            {
                BlendKernels.Blend8(_mode, one, MemoryMarshal.Cast<TPixel, uint>(destination), coverage, _opacity255, Bgr);
                return;
            }

            uint[] buffer = ArrayPool<uint>.Shared.Rent(destination.Length);
            try
            {
                Span<uint> d = buffer.AsSpan(0, destination.Length);
                Premultiply(destination, d);
                BlendKernels.Blend8(_mode, one, d, coverage, _opacity255, Bgr);
                Unpremultiply(d, destination);
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(buffer);
            }

            return;
        }

        // A four-float source is a solid color to the float kernel.
        Span<RgbaF32> straight = [RgbaF32.FromRgba8(color)];
        BlendStraight(straight, destination, coverage);
    }

    /// <summary>Gets whether a mode gives an opaque result wherever the destination is opaque, whatever the source.</summary>
    private static bool KeepsOpaque(BlendMode mode)
    {
        return BlendReference.IsBlend(mode) || mode is BlendMode.SourceOver or BlendMode.DestinationOver or BlendMode.SourceAtop
            or BlendMode.Plus or BlendMode.Destination;
    }

    /// <summary>Gets whether every pixel of an 8-bit row with alpha in the top byte is opaque.</summary>
    private static bool Opaque(ReadOnlySpan<TPixel> row)
    {
        ReadOnlySpan<uint> pixels = MemoryMarshal.Cast<TPixel, uint>(row);
        int i = 0;
        if (System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated)
        {
            System.Runtime.Intrinsics.Vector256<uint> all = System.Runtime.Intrinsics.Vector256.Create(uint.MaxValue);
            for (; i + 8 <= pixels.Length; i += 8)
            {
                all &= System.Runtime.Intrinsics.Vector256.Create(pixels.Slice(i, 8));
            }

            if (!System.Runtime.Intrinsics.Vector256.GreaterThanOrEqualAll(all, System.Runtime.Intrinsics.Vector256.Create(0xFF000000u)))
            {
                return false;
            }
        }

        for (; i < pixels.Length; i++)
        {
            if (pixels[i] < 0xFF000000u)
            {
                return false;
            }
        }

        return true;
    }

    private static void Premultiply(ReadOnlySpan<TPixel> source, Span<uint> destination)
    {
        if (Bgr)
        {
            PixelConvert.Convert(MemoryMarshal.Cast<TPixel, Bgra8>(source), MemoryMarshal.Cast<uint, PremulBgra8>(destination));
        }
        else
        {
            PixelConvert.Convert(MemoryMarshal.Cast<TPixel, Rgba8>(source), MemoryMarshal.Cast<uint, PremulRgba8>(destination));
        }
    }

    private static void Unpremultiply(ReadOnlySpan<uint> source, Span<TPixel> destination)
    {
        if (Bgr)
        {
            PixelConvert.Convert(MemoryMarshal.Cast<uint, PremulBgra8>(source), MemoryMarshal.Cast<TPixel, Bgra8>(destination));
        }
        else
        {
            PixelConvert.Convert(MemoryMarshal.Cast<uint, PremulRgba8>(source), MemoryMarshal.Cast<TPixel, Rgba8>(destination));
        }
    }
}
