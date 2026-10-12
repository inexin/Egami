using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Analysis;

/// <summary>Counts distinct stored pixel values, and scans frames for alpha and color with early exit.</summary>
internal static class ColorScans
{
    /// <summary>
    /// Counts the distinct stored values of a plane. Types of up to 3 bytes mark a bit per possible value (at most 2 MB); wider
    /// types keep a set per band of rows, merged at the end. The sets' worst case is checked against MaxTotalBytes first.
    /// </summary>
    internal static long CountColors<TPixel>(PixelPlane<TPixel> plane, EgamiContext context, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int size = Unsafe.SizeOf<TPixel>();
        long rowBytes = (long)plane.Width * size;
        if (size <= 3)
        {
            ulong[] bits = new ulong[(1 << (8 * size)) / 64 + 1];
            RowRunner.Run(plane.Height, rowBytes, context.MaxParallelism, (start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(plane.Row(y));
                    for (int i = 0; i < bytes.Length; i += size)
                    {
                        uint key = size switch
                        {
                            1 => bytes[i],
                            2 => (uint)(bytes[i] | (bytes[i + 1] << 8)),
                            _ => (uint)(bytes[i] | (bytes[i + 1] << 8) | (bytes[i + 2] << 16)),
                        };
                        ref ulong word = ref bits[key >> 6];
                        ulong bit = 1UL << (int)key;
                        if ((Volatile.Read(ref word) & bit) == 0)
                        {
                            Interlocked.Or(ref word, bit);
                        }
                    }
                }
            }, cancellationToken);

            long count = 0;
            foreach (ulong word in bits)
            {
                count += (long)ulong.PopCount(word);
            }

            return count;
        }

        // A set holds at most every pixel once, at about 3 words of hash table per entry.
        long pixels = (long)plane.Width * plane.Height;
        new BudgetedMemorySource(context.Memory, context.Limits.MaxTotalBytes).Check(pixels * 3 * Math.Max(size, 8));
        return size switch
        {
            4 => Count<TPixel, uint>(plane, context, cancellationToken),
            <= 8 => Count<TPixel, ulong>(plane, context, cancellationToken),
            _ => Count<TPixel, UInt128>(plane, context, cancellationToken),
        };
    }

    private static long Count<TPixel, TKey>(PixelPlane<TPixel> plane, EgamiContext context, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
        where TKey : unmanaged, IEquatable<TKey>
    {
        int size = Unsafe.SizeOf<TPixel>();
        HashSet<TKey> all = [];
        object gate = new();
        RowRunner.Run(plane.Height, (long)plane.Width * size, context.MaxParallelism, (start, end) =>
        {
            HashSet<TKey> band = [];
            for (int y = start; y < end; y++)
            {
                foreach (TPixel pixel in plane.Row(y))
                {
                    band.Add(Key<TPixel, TKey>(pixel));
                }
            }

            lock (gate)
            {
                all.UnionWith(band);
            }
        }, cancellationToken);
        return all.Count;
    }

    /// <summary>Returns a pixel's bytes as a key, zero-extended.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TKey Key<TPixel, TKey>(TPixel pixel)
        where TPixel : unmanaged
        where TKey : unmanaged
    {
        TKey key = default;
        Unsafe.CopyBlockUnaligned(ref Unsafe.As<TKey, byte>(ref key), ref Unsafe.As<TPixel, byte>(ref pixel), (uint)Unsafe.SizeOf<TPixel>());
        return key;
    }

    /// <summary>Returns the distinct colors of a frame as Rgba16, sorted, or null once there are more than <paramref name="limit"/>.</summary>
    internal static List<Rgba16>? GetColors(Raster raster, int frame, int limit)
    {
        int width = raster.Width;
        HashSet<ulong> seen = [];
        Rgba16[] row = ArrayPool<Rgba16>.Shared.Rent(width);
        try
        {
            for (int y = 0; y < raster.Height; y++)
            {
                Span<Rgba16> pixels = row.AsSpan(0, width);
                raster.ReadFrameRow(frame, y, 0, pixels);
                foreach (Rgba16 p in pixels)
                {
                    ulong key = ((ulong)p.R << 48) | ((ulong)p.G << 32) | ((ulong)p.B << 16) | p.A;
                    if (seen.Add(key) && seen.Count > limit)
                    {
                        return null;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<Rgba16>.Shared.Return(row);
        }

        List<ulong> keys = [.. seen];
        keys.Sort();
        return [.. keys.Select(k => new Rgba16((ushort)(k >> 48), (ushort)(k >> 32), (ushort)(k >> 16), (ushort)k))];
    }

    /// <summary>Returns whether any pixel of a frame fails <paramref name="test"/>, stopping the scan at the first that does.</summary>
    internal static bool Any(Raster raster, int frame, Func<RgbaF32, bool> test, CancellationToken cancellationToken)
    {
        int width = raster.Width;
        int found = 0;
        RowRunner.Run(raster.Height, (long)width * 16, raster.Context.MaxParallelism, (start, end) =>
        {
            if (Volatile.Read(ref found) != 0)
            {
                return;
            }

            RgbaF32[] buffer = ArrayPool<RgbaF32>.Shared.Rent(width);
            try
            {
                for (int y = start; y < end && Volatile.Read(ref found) == 0; y++)
                {
                    Span<RgbaF32> row = buffer.AsSpan(0, width);
                    raster.ReadFrameRow(frame, y, 0, row);
                    foreach (RgbaF32 p in row)
                    {
                        if (test(p))
                        {
                            Volatile.Write(ref found, 1);
                            return;
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<RgbaF32>.Shared.Return(buffer);
            }
        }, cancellationToken);
        return found != 0;
    }
}
