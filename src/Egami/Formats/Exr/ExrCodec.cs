using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Egami.Formats.Exr;

/// <summary>
/// One block to decompress: its pixel area, the part's channels, and where the samples go. The decompressed layout is the
/// OpenEXR one: for each scanline, for each channel sampled on it (in channel order), the line's samples, little-endian.
/// </summary>
internal readonly record struct ExrBlock(ExrPartHeader Part, int X0, int Y0, int X1, int Y1)
{
    public int Width => X1 - X0 + 1;

    public int Height => Y1 - Y0 + 1;

    /// <summary>Gets the decompressed size in bytes.</summary>
    public int UnpackedBytes
    {
        get
        {
            long total = 0;
            foreach (ExrChannelInfo c in Part.Channels)
            {
                total += (long)Sampled(X0, X1, c.XSampling) * Sampled(Y0, Y1, c.YSampling) * c.Bytes;
            }

            return (int)Math.Min(total, int.MaxValue);
        }
    }

    /// <summary>Counts the multiples of <paramref name="s"/> in [<paramref name="lo"/>, <paramref name="hi"/>].</summary>
    public static int Sampled(int lo, int hi, int s)
    {
        if (s == 1)
        {
            return hi - lo + 1;
        }

        long first = (long)Math.Ceiling(lo / (double)s) * s;
        return first > hi ? 0 : (int)(((hi - first) / s) + 1);
    }
}

/// <summary>The lossless OpenEXR codecs and the dispatch to the others.</summary>
internal static class ExrCodec
{
    /// <summary>
    /// Decompresses <paramref name="source"/> into <paramref name="destination"/>, which is <see cref="ExrBlock.UnpackedBytes"/> long.
    /// A block whose compressed size equals its raw size is stored raw, whatever the compression. Returns false when the
    /// data is damaged.
    /// </summary>
    public static bool Decompress(in ExrBlock block, ReadOnlySpan<byte> source, Span<byte> destination, Span<byte> scratch)
    {
        if (source.Length == destination.Length || block.Part.Compression == ExrCompression.None)
        {
            if (source.Length != destination.Length)
            {
                return false;
            }

            source.CopyTo(destination);
            return true;
        }

        return block.Part.Compression switch
        {
            ExrCompression.Rle => Rle(source, destination, scratch),
            ExrCompression.Zips or ExrCompression.Zip => Zip(source, destination, scratch),
            ExrCompression.Piz => ExrPiz.Decompress(block, source, destination),
            ExrCompression.Pxr24 => ExrPxr24.Decompress(block, source, destination, scratch),
            ExrCompression.B44 or ExrCompression.B44A => ExrB44.Decompress(block, source, destination),
            ExrCompression.Dwaa or ExrCompression.Dwab => ExrDwa.Decompress(block, source, destination),
            _ => throw new UnsupportedFormatException($"OpenEXR {block.Part.Compression} compression is not supported."),
        };
    }

    /// <summary>Gets the scratch bytes <see cref="Decompress"/> needs for a block of <paramref name="unpacked"/> bytes.</summary>
    public static int ScratchBytes(int unpacked) => unpacked + 64;

    /// <summary>RLE: a count byte n ≥ 0 repeats the next byte n + 1 times, n &lt; 0 copies −n bytes; then the byte predictor.</summary>
    private static bool Rle(ReadOnlySpan<byte> source, Span<byte> destination, Span<byte> scratch)
    {
        Span<byte> t = scratch[..destination.Length];
        int pos = 0, outPos = 0;
        while (pos < source.Length)
        {
            int count = (sbyte)source[pos++];
            if (count < 0)
            {
                count = -count;
                if (count > source.Length - pos || count > t.Length - outPos)
                {
                    return false;
                }

                source.Slice(pos, count).CopyTo(t[outPos..]);
                pos += count;
                outPos += count;
            }
            else
            {
                count++;
                if (pos >= source.Length || count > t.Length - outPos)
                {
                    return false;
                }

                t.Slice(outPos, count).Fill(source[pos++]);
                outPos += count;
            }
        }

        if (outPos != t.Length)
        {
            return false;
        }

        Unpredict(t, destination);
        return true;
    }

    /// <summary>ZIP and ZIPS: a zlib stream, then the byte predictor.</summary>
    private static bool Zip(ReadOnlySpan<byte> source, Span<byte> destination, Span<byte> scratch)
    {
        Span<byte> t = scratch[..destination.Length];
        if (!Inflate(source, t))
        {
            return false;
        }

        Unpredict(t, destination);
        return true;
    }

    /// <summary>Inflates a zlib stream into exactly <paramref name="destination"/>.</summary>
    internal static unsafe bool Inflate(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty)
        {
            return destination.IsEmpty;
        }

        try
        {
            fixed (byte* p = source)
            {
                using UnmanagedMemoryStream input = new(p, source.Length);
                using ZLibStream zlib = new(input, CompressionMode.Decompress);
                int total = 0;
                while (total < destination.Length)
                {
                    int n = zlib.Read(destination[total..]);
                    if (n == 0)
                    {
                        return false;
                    }

                    total += n;
                }

                return true;
            }
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Inflates a zlib stream into at most <paramref name="destination"/>.Length bytes; returns the count, or −1 when damaged.</summary>
    internal static unsafe int InflateUpTo(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty)
        {
            return -1;
        }

        try
        {
            fixed (byte* p = source)
            {
                using UnmanagedMemoryStream input = new(p, source.Length);
                using ZLibStream zlib = new(input, CompressionMode.Decompress);
                int total = 0;
                while (total < destination.Length)
                {
                    int n = zlib.Read(destination[total..]);
                    if (n == 0)
                    {
                        break;
                    }

                    total += n;
                }

                return total;
            }
        }
        catch (IOException)
        {
            return -1;
        }
    }

    /// <summary>
    /// Undoes OpenEXR's byte predictor and split: each byte was stored as its difference from the one before plus 128, the
    /// even-indexed bytes in the first half and the odd ones in the second.
    /// </summary>
    internal static void Unpredict(Span<byte> t, Span<byte> destination)
    {
        int n = t.Length;
        if (n == 0)
        {
            return;
        }

        // A running sum of (byte - 128): 16 bytes at a time as a prefix sum by doubling shifts, plus the carry.
        ref byte r = ref MemoryMarshal.GetReference(t);
        int i = 1;
        byte previous = r;
        if (Vector128.IsHardwareAccelerated && n >= 17)
        {
            Vector128<byte> bias = Vector128.Create((byte)128);
            Vector128<byte> carry = Vector128.Create(previous);
            for (; i + 16 <= n; i += 16)
            {
                Vector128<byte> v = Vector128.LoadUnsafe(ref r, (nuint)i) - bias;
                v += Vector128.Shuffle(v, Shift1);
                v += Vector128.Shuffle(v, Shift2);
                v += Vector128.Shuffle(v, Shift4);
                v += Vector128.Shuffle(v, Shift8);
                v += carry;
                v.StoreUnsafe(ref r, (nuint)i);
                carry = Vector128.Shuffle(v, Last);
            }

            previous = Unsafe.Add(ref r, i - 1);
        }

        for (; i < n; i++)
        {
            previous = (byte)(previous + Unsafe.Add(ref r, i) - 128);
            Unsafe.Add(ref r, i) = previous;
        }

        // Interleave the halves: even bytes from the first, odd from the second.
        int half = (n + 1) / 2;
        int pairs = n / 2;
        ref byte d = ref MemoryMarshal.GetReference(destination);
        ref byte second = ref Unsafe.Add(ref r, half);
        int k = 0;
        if (Sse2.IsSupported)
        {
            for (; k + 16 <= pairs; k += 16)
            {
                Vector128<byte> a = Vector128.LoadUnsafe(ref r, (nuint)k), b = Vector128.LoadUnsafe(ref second, (nuint)k);
                Sse2.UnpackLow(a, b).StoreUnsafe(ref d, (nuint)(2 * k));
                Sse2.UnpackHigh(a, b).StoreUnsafe(ref d, (nuint)((2 * k) + 16));
            }
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            for (; k + 16 <= pairs; k += 16)
            {
                Vector128<byte> a = Vector128.LoadUnsafe(ref r, (nuint)k), b = Vector128.LoadUnsafe(ref second, (nuint)k);
                AdvSimd.Arm64.ZipLow(a, b).StoreUnsafe(ref d, (nuint)(2 * k));
                AdvSimd.Arm64.ZipHigh(a, b).StoreUnsafe(ref d, (nuint)((2 * k) + 16));
            }
        }

        for (; k < pairs; k++)
        {
            Unsafe.Add(ref d, 2 * k) = Unsafe.Add(ref r, k);
            Unsafe.Add(ref d, (2 * k) + 1) = Unsafe.Add(ref second, k);
        }

        if ((n & 1) != 0)
        {
            Unsafe.Add(ref d, n - 1) = Unsafe.Add(ref r, half - 1);
        }
    }

    /// <summary>Shuffle indices that shift a vector's bytes up by 1, 2, 4 or 8 lanes, filling with zero (index 255).</summary>
    private static readonly Vector128<byte> Shift1 = Vector128.Create((byte)255, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14);
    private static readonly Vector128<byte> Shift2 = Vector128.Create((byte)255, 255, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13);
    private static readonly Vector128<byte> Shift4 = Vector128.Create((byte)255, 255, 255, 255, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
    private static readonly Vector128<byte> Shift8 = Vector128.Create((byte)255, 255, 255, 255, 255, 255, 255, 255, 0, 1, 2, 3, 4, 5, 6, 7);
    private static readonly Vector128<byte> Last = Vector128.Create((byte)15);

    /// <summary>Applies OpenEXR's byte split and predictor, the inverse of <see cref="Unpredict"/>.</summary>
    internal static void Predict(ReadOnlySpan<byte> source, Span<byte> t)
    {
        int n = source.Length;
        int half = (n + 1) / 2;
        for (int i = 0, a = 0, b = half; i < n; i++)
        {
            if ((i & 1) == 0)
            {
                t[a++] = source[i];
            }
            else
            {
                t[b++] = source[i];
            }
        }

        int p = t[0];
        for (int i = 1; i < n; i++)
        {
            int v = t[i];
            t[i] = (byte)(v - p + 128 + 256);
            p = v;
        }
    }
}
