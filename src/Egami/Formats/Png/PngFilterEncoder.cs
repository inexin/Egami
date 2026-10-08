using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Egami.Pixels;

namespace Egami.Formats.Png;

/// <summary>
/// Applies PNG filters when writing. Filtering reads only the unfiltered row and the unfiltered previous row,
/// so unlike unfiltering every filter vectorizes across the whole row.
/// </summary>
internal static class PngFilterEncoder
{
    /// <summary>Writes <paramref name="row"/> filtered with <paramref name="filter"/> into <paramref name="destination"/>, using the scalar path.</summary>
    internal static void FilterScalar(byte filter, ReadOnlySpan<byte> row, ReadOnlySpan<byte> previous, int stride, Span<byte> destination)
    {
        for (int i = 0; i < row.Length; i++)
        {
            int left = i >= stride ? row[i - stride] : 0;
            int upperLeft = i >= stride ? previous[i - stride] : 0;
            int predicted = filter switch
            {
                PngFilter.None => 0,
                PngFilter.Sub => left,
                PngFilter.Up => previous[i],
                PngFilter.Average => (left + previous[i]) >> 1,
                _ => PngFilter.Predict((byte)left, previous[i], (byte)upperLeft),
            };
            destination[i] = (byte)(row[i] - predicted);
        }
    }

    /// <summary>Writes <paramref name="row"/> filtered with <paramref name="filter"/> into <paramref name="destination"/>.</summary>
    /// <param name="filter">The filter type, 0 to 4.</param>
    /// <param name="row">The unfiltered row.</param>
    /// <param name="previous">The unfiltered previous row, all zero for the first row.</param>
    /// <param name="stride">Bytes per complete pixel, at least 1.</param>
    /// <param name="destination">Where to write the filtered bytes.</param>
    /// <param name="vectorBits">Below 128 means scalar only.</param>
    internal static void Filter(byte filter, ReadOnlySpan<byte> row, ReadOnlySpan<byte> previous, int stride, Span<byte> destination, int vectorBits)
    {
        int head = Math.Min(stride, row.Length);
        if (vectorBits < 128 || row.Length < stride + 16)
        {
            FilterScalar(filter, row, previous, stride, destination);
            return;
        }

        // The first pixel has no left neighbor; the vector loop starts after it.
        FilterScalar(filter, row[..head], previous[..head], stride, destination[..head]);
        ref byte r = ref MemoryMarshal.GetReference(row);
        ref byte p = ref MemoryMarshal.GetReference(previous);
        ref byte d = ref MemoryMarshal.GetReference(destination);
        nuint s = (nuint)stride;
        int i = head;
        for (; i + 16 <= row.Length; i += 16)
        {
            nuint n = (nuint)i;
            Vector128<byte> x = Vector128.LoadUnsafe(ref r, n);
            Vector128<byte> predicted = filter switch
            {
                PngFilter.None => Vector128<byte>.Zero,
                PngFilter.Sub => Vector128.LoadUnsafe(ref r, n - s),
                PngFilter.Up => Vector128.LoadUnsafe(ref p, n),
                PngFilter.Average => FloorAverage(Vector128.LoadUnsafe(ref r, n - s), Vector128.LoadUnsafe(ref p, n)),
                _ => Paeth(Vector128.LoadUnsafe(ref r, n - s), Vector128.LoadUnsafe(ref p, n), Vector128.LoadUnsafe(ref p, n - s)),
            };
            (x - predicted).StoreUnsafe(ref d, n);
        }

        for (; i < row.Length; i++)
        {
            int left = row[i - stride];
            int predicted = filter switch
            {
                PngFilter.None => 0,
                PngFilter.Sub => left,
                PngFilter.Up => previous[i],
                PngFilter.Average => (left + previous[i]) >> 1,
                _ => PngFilter.Predict((byte)left, previous[i], previous[i - stride]),
            };
            destination[i] = (byte)(row[i] - predicted);
        }
    }

    /// <summary>Returns the sum of the bytes read as signed values' magnitudes: the specification's heuristic for choosing a filter.</summary>
    internal static long SumOfAbsolutes(ReadOnlySpan<byte> filtered, int vectorBits)
    {
        long total = 0;
        int i = 0;
        if (vectorBits >= 128)
        {
            ref byte f = ref MemoryMarshal.GetReference(filtered);
            while (i + 16 <= filtered.Length)
            {
                // 16-bit lanes hold at most 2 × 255 per step, so flush before 128 steps.
                Vector128<ushort> sums = Vector128<ushort>.Zero;
                int end = Math.Min(filtered.Length - 15, i + (128 * 16));
                for (; i < end; i += 16)
                {
                    Vector128<byte> magnitude = Vector128.Abs(Vector128.LoadUnsafe(ref f, (nuint)i).AsSByte()).AsByte();
                    (Vector128<ushort> low, Vector128<ushort> high) = Vector128.Widen(magnitude);
                    sums += low + high;
                }

                (Vector128<uint> a, Vector128<uint> b) = Vector128.Widen(sums);
                total += Vector128.Sum(a + b);
            }
        }

        for (; i < filtered.Length; i++)
        {
            total += Math.Abs((int)(sbyte)filtered[i]);
        }

        return total;
    }

    /// <summary>Filters with the widest vectors the hardware accelerates.</summary>
    internal static void Filter(byte filter, ReadOnlySpan<byte> row, ReadOnlySpan<byte> previous, int stride, Span<byte> destination)
    {
        Filter(filter, row, previous, stride, destination, PixelKernels.HardwareBits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> FloorAverage(Vector128<byte> a, Vector128<byte> b)
    {
        return (a & b) + Vector128.ShiftRightLogical(a ^ b, 1);
    }

    /// <summary>The Paeth predictor for 16 bytes, branch-free in 16-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Paeth(Vector128<byte> left, Vector128<byte> above, Vector128<byte> upperLeft)
    {
        (Vector128<ushort> a0, Vector128<ushort> a1) = Vector128.Widen(left);
        (Vector128<ushort> b0, Vector128<ushort> b1) = Vector128.Widen(above);
        (Vector128<ushort> c0, Vector128<ushort> c1) = Vector128.Widen(upperLeft);
        return Vector128.Narrow(Paeth16(a0.AsInt16(), b0.AsInt16(), c0.AsInt16()), Paeth16(a1.AsInt16(), b1.AsInt16(), c1.AsInt16()));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> Paeth16(Vector128<short> a, Vector128<short> b, Vector128<short> c)
    {
        Vector128<short> pa = Vector128.Abs(b - c);
        Vector128<short> pb = Vector128.Abs(a - c);
        Vector128<short> pc = Vector128.Abs(a + b - c - c);
        Vector128<short> useA = Vector128.LessThanOrEqual(pa, pb) & Vector128.LessThanOrEqual(pa, pc);
        Vector128<short> useB = Vector128.LessThanOrEqual(pb, pc);
        return Vector128.ConditionalSelect(useA, a, Vector128.ConditionalSelect(useB, b, c)).AsUInt16();
    }
}
