using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Video.Analysis;

/// <summary>Sums and counts over planes of 8-bit and wider samples, for the video detectors.</summary>
internal static class PlaneMath
{
    /// <summary>Counts the samples at or under <paramref name="threshold"/>.</summary>
    public static long CountAtMost<T>(YuvPlane<T> plane, int threshold)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (threshold < 0)
        {
            return 0;
        }

        T limit = T.CreateSaturating(threshold);
        long count = 0;
        for (int y = 0; y < plane.Height; y++)
        {
            ReadOnlySpan<T> row = plane.Row(y)[..plane.Width];
            int x = 0;
            if (Vector256.IsHardwareAccelerated && row.Length >= Vector256<T>.Count)
            {
                Vector256<T> top = Vector256.Create(limit);
                for (; x <= row.Length - Vector256<T>.Count; x += Vector256<T>.Count)
                {
                    count += BitOperations.PopCount(Vector256.LessThanOrEqual(Vector256.Create(row.Slice(x, Vector256<T>.Count)), top).ExtractMostSignificantBits());
                }
            }

            for (; x < row.Length; x++)
            {
                if (row[x] <= limit)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>Sums the absolute differences between two planes of the same size.</summary>
    public static long AbsoluteDifference<T>(YuvPlane<T> a, YuvPlane<T> b)
        where T : unmanaged, IBinaryInteger<T>
    {
        long total = 0;
        for (int y = 0; y < a.Height; y++)
        {
            total += AbsoluteDifference(a.Row(y)[..a.Width], b.Row(y)[..a.Width]);
        }

        return total;
    }

    /// <summary>Sums the absolute differences between two rows.</summary>
    public static long AbsoluteDifference<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b)
        where T : unmanaged, IBinaryInteger<T>
    {
        long total = 0;
        int x = 0;
        if (typeof(T) == typeof(byte) && Vector256.IsHardwareAccelerated && a.Length >= Vector256<byte>.Count)
        {
            ReadOnlySpan<byte> ab = MemoryMarshal.AsBytes(a);
            ReadOnlySpan<byte> bb = MemoryMarshal.AsBytes(b);
            Vector256<uint> sum = Vector256<uint>.Zero;
            for (; x <= ab.Length - Vector256<byte>.Count; x += Vector256<byte>.Count)
            {
                Vector256<byte> va = Vector256.Create(ab.Slice(x, Vector256<byte>.Count));
                Vector256<byte> vb = Vector256.Create(bb.Slice(x, Vector256<byte>.Count));
                Vector256<byte> diff = Vector256.Max(va, vb) - Vector256.Min(va, vb);
                (Vector256<ushort> lo, Vector256<ushort> hi) = Vector256.Widen(diff);
                (Vector256<uint> l0, Vector256<uint> l1) = Vector256.Widen(lo + hi);
                sum += l0 + l1;
            }

            total = (long)SumWide(sum);
        }
        else if (typeof(T) == typeof(ushort) && Vector256.IsHardwareAccelerated && a.Length >= Vector256<ushort>.Count)
        {
            ReadOnlySpan<ushort> aw = MemoryMarshal.Cast<T, ushort>(a);
            ReadOnlySpan<ushort> bw = MemoryMarshal.Cast<T, ushort>(b);
            Vector256<uint> sum = Vector256<uint>.Zero;
            for (; x <= aw.Length - Vector256<ushort>.Count; x += Vector256<ushort>.Count)
            {
                Vector256<ushort> va = Vector256.Create(aw.Slice(x, Vector256<ushort>.Count));
                Vector256<ushort> vb = Vector256.Create(bw.Slice(x, Vector256<ushort>.Count));
                (Vector256<uint> lo, Vector256<uint> hi) = Vector256.Widen(Vector256.Max(va, vb) - Vector256.Min(va, vb));
                sum += lo + hi;
            }

            total = (long)SumWide(sum);
        }

        for (; x < a.Length; x++)
        {
            int d = int.CreateTruncating(a[x]) - int.CreateTruncating(b[x]);
            total += d < 0 ? -d : d;
        }

        return total;
    }

    /// <summary>Copies a plane into an array, row after row.</summary>
    public static void CopyTo<T>(YuvPlane<T> plane, T[] target)
        where T : unmanaged
    {
        for (int y = 0; y < plane.Height; y++)
        {
            plane.Row(y)[..plane.Width].CopyTo(target.AsSpan(y * plane.Width, plane.Width));
        }
    }

    private static ulong SumWide(Vector256<uint> sum)
    {
        Vector256<ulong> wide = Vector256.WidenLower(sum) + Vector256.WidenUpper(sum);
        return Vector256.Sum(wide);
    }
}
