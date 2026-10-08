using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Egami.Pixels;

namespace Egami.Formats.Png;

/// <summary>
/// SIMD reconstruction of PNG filters. Up is vectorized across the whole row. Sub, Average and Paeth depend on the
/// reconstructed pixel to the left, so they work one pixel at a time with the whole pixel in one Vector128,
/// for filter strides of 3, 4, 6 and 8 bytes. Every result is identical to <see cref="PngFilter.Unfilter"/>.
/// </summary>
internal static class PngFilterSimd
{
    /// <summary>Reverses the filter on one scanline in place, using vectors no wider than <paramref name="vectorBits"/>.</summary>
    /// <param name="filter">The filter-type byte. Values above 4 are rejected by <see cref="PngFilter.Unfilter"/>.</param>
    /// <param name="row">The filtered bytes, replaced by the reconstructed bytes.</param>
    /// <param name="previous">The reconstructed previous row, all zero for the first row.</param>
    /// <param name="stride">Bytes per complete pixel, at least 1.</param>
    /// <param name="vectorBits">The widest vector to use; below 128 means scalar only.</param>
    internal static void Unfilter(byte filter, Span<byte> row, ReadOnlySpan<byte> previous, int stride, int vectorBits)
    {
        if (vectorBits < 128 || filter == PngFilter.None)
        {
            PngFilter.Unfilter(filter, row, previous, stride);
            return;
        }

        if (filter == PngFilter.Up)
        {
            UnfilterUp(row, previous, vectorBits);
            return;
        }

        if (filter > PngFilter.Paeth)
        {
            PngFilter.Unfilter(filter, row, previous, stride);
            return;
        }

        switch (stride)
        {
            case 3:
                PerPixel<Three>(filter, row, previous);
                break;
            case 4:
                PerPixel<Four>(filter, row, previous);
                break;
            case 6:
                PerPixel<Six>(filter, row, previous);
                break;
            case 8:
                PerPixel<Eight>(filter, row, previous);
                break;
            default:
                PngFilter.Unfilter(filter, row, previous, stride);
                break;
        }
    }

    /// <summary>Unfilters with the widest vectors the hardware accelerates.</summary>
    internal static void Unfilter(byte filter, Span<byte> row, ReadOnlySpan<byte> previous, int stride)
    {
        Unfilter(filter, row, previous, stride, PixelKernels.HardwareBits);
    }

    private static void UnfilterUp(Span<byte> row, ReadOnlySpan<byte> previous, int vectorBits)
    {
        ref byte r = ref MemoryMarshal.GetReference(row);
        ref byte p = ref MemoryMarshal.GetReference(previous);
        int length = row.Length;
        int i = 0;
        if (vectorBits >= 512 && Vector512.IsHardwareAccelerated)
        {
            for (; i + 64 <= length; i += 64)
            {
                (Vector512.LoadUnsafe(ref r, (nuint)i) + Vector512.LoadUnsafe(ref p, (nuint)i)).StoreUnsafe(ref r, (nuint)i);
            }
        }

        if (vectorBits >= 256 && Vector256.IsHardwareAccelerated)
        {
            for (; i + 32 <= length; i += 32)
            {
                (Vector256.LoadUnsafe(ref r, (nuint)i) + Vector256.LoadUnsafe(ref p, (nuint)i)).StoreUnsafe(ref r, (nuint)i);
            }
        }

        for (; i + 16 <= length; i += 16)
        {
            (Vector128.LoadUnsafe(ref r, (nuint)i) + Vector128.LoadUnsafe(ref p, (nuint)i)).StoreUnsafe(ref r, (nuint)i);
        }

        for (; i < length; i++)
        {
            Unsafe.Add(ref r, i) += Unsafe.Add(ref p, i);
        }
    }

    /// <summary>
    /// Sub, Average and Paeth, one pixel per step. The pixel left of the first one, and the one above-left of it, are zero,
    /// which gives exactly the specification's rules for the first pixel.
    /// </summary>
    private static void PerPixel<TPixel>(byte filter, Span<byte> row, ReadOnlySpan<byte> previous)
        where TPixel : struct, IPixelBytes
    {
        ref byte r = ref MemoryMarshal.GetReference(row);
        ref byte p = ref MemoryMarshal.GetReference(previous);
        int stride = TPixel.Size;
        Vector128<byte> left = Vector128<byte>.Zero;
        Vector128<byte> upperLeft = Vector128<byte>.Zero;
        for (int i = 0; i + stride <= row.Length; i += stride)
        {
            Vector128<byte> raw = TPixel.Load(ref Unsafe.Add(ref r, i));
            Vector128<byte> above = filter == PngFilter.Sub ? default : TPixel.Load(ref Unsafe.Add(ref p, i));
            Vector128<byte> result = filter switch
            {
                PngFilter.Sub => raw + left,
                PngFilter.Average => raw + FloorAverage(left, above),
                _ => raw + Paeth(left, above, upperLeft),
            };
            TPixel.Store(ref Unsafe.Add(ref r, i), result);
            left = result;
            upperLeft = above;
        }
    }

    /// <summary>floor((a + b) / 2) per byte, without overflow.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> FloorAverage(Vector128<byte> a, Vector128<byte> b)
    {
        return (a & b) + Vector128.ShiftRightLogical(a ^ b, 1);
    }

    /// <summary>The Paeth predictor for the low 8 bytes, branch-free in 16-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Paeth(Vector128<byte> left, Vector128<byte> above, Vector128<byte> upperLeft)
    {
        Vector128<short> a = Vector128.WidenLower(left).AsInt16();
        Vector128<short> b = Vector128.WidenLower(above).AsInt16();
        Vector128<short> c = Vector128.WidenLower(upperLeft).AsInt16();

        // With p = a + b - c: |p - a| = |b - c|, |p - b| = |a - c|, |p - c| = |a + b - 2c|.
        Vector128<short> pa = Vector128.Abs(b - c);
        Vector128<short> pb = Vector128.Abs(a - c);
        Vector128<short> pc = Vector128.Abs(a + b - c - c);

        Vector128<short> useA = Vector128.LessThanOrEqual(pa, pb) & Vector128.LessThanOrEqual(pa, pc);
        Vector128<short> useB = Vector128.LessThanOrEqual(pb, pc);
        Vector128<short> predicted = Vector128.ConditionalSelect(useA, a, Vector128.ConditionalSelect(useB, b, c));
        return Vector128.Narrow(predicted.AsUInt16(), predicted.AsUInt16());
    }

    /// <summary>Loads and stores exactly one pixel of a given size in the low bytes of a vector.</summary>
    private interface IPixelBytes
    {
        static abstract int Size { get; }

        static abstract Vector128<byte> Load(ref byte source);

        static abstract void Store(ref byte destination, Vector128<byte> value);
    }

    private readonly struct Three : IPixelBytes
    {
        public static int Size => 3;

        public static Vector128<byte> Load(ref byte source)
        {
            uint value = Unsafe.ReadUnaligned<ushort>(ref source) | ((uint)Unsafe.Add(ref source, 2) << 16);
            return Vector128.CreateScalar(value).AsByte();
        }

        public static void Store(ref byte destination, Vector128<byte> value)
        {
            uint bytes = value.AsUInt32().ToScalar();
            Unsafe.WriteUnaligned(ref destination, (ushort)bytes);
            Unsafe.Add(ref destination, 2) = (byte)(bytes >> 16);
        }
    }

    private readonly struct Four : IPixelBytes
    {
        public static int Size => 4;

        public static Vector128<byte> Load(ref byte source)
        {
            return Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref source)).AsByte();
        }

        public static void Store(ref byte destination, Vector128<byte> value)
        {
            Unsafe.WriteUnaligned(ref destination, value.AsUInt32().ToScalar());
        }
    }

    private readonly struct Six : IPixelBytes
    {
        public static int Size => 6;

        public static Vector128<byte> Load(ref byte source)
        {
            ulong value = Unsafe.ReadUnaligned<uint>(ref source) | ((ulong)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref source, 4)) << 32);
            return Vector128.CreateScalar(value).AsByte();
        }

        public static void Store(ref byte destination, Vector128<byte> value)
        {
            ulong bytes = value.AsUInt64().ToScalar();
            Unsafe.WriteUnaligned(ref destination, (uint)bytes);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 4), (ushort)(bytes >> 32));
        }
    }

    private readonly struct Eight : IPixelBytes
    {
        public static int Size => 8;

        public static Vector128<byte> Load(ref byte source)
        {
            return Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref source)).AsByte();
        }

        public static void Store(ref byte destination, Vector128<byte> value)
        {
            Unsafe.WriteUnaligned(ref destination, value.AsUInt64().ToScalar());
        }
    }
}
