using System.Numerics;
using System.Runtime.CompilerServices;

namespace Egami.Formats.Jbig2;

/// <summary>How a region or symbol is combined with what is under it (T.88 7.4.6.4 and 7.4.8.5).</summary>
internal enum Jbig2Combine
{
    Or = 0,
    And = 1,
    Xor = 2,
    Xnor = 3,
    Replace = 4,
}

/// <summary>
/// A 1-bit image packed into rows of 64-bit words, the leftmost pixel in the most significant bit, 1 meaning black (T.88 5.6).
/// Bits past the width in each row are kept zero, so a row's words can be read whole.
/// </summary>
internal sealed class Jbig2Bitmap
{
    internal Jbig2Bitmap(int width, int height)
    {
        Width = width;
        Height = height;
        Stride = (width + 63) >> 6;
        Words = new ulong[checked((long)Stride * height)];
    }

    internal int Width { get; }

    internal int Height { get; private set; }

    /// <summary>Gets the words in each row.</summary>
    internal int Stride { get; }

    internal ulong[] Words { get; private set; }

    /// <summary>Gets the bytes the bitmap holds.</summary>
    internal long Bytes => Words.LongLength * sizeof(ulong);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Get(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            return 0;
        }

        return (int)(Words[(y * Stride) + (x >> 6)] >> (63 - (x & 63))) & 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Set(int x, int y, int value)
    {
        ref ulong word = ref Words[(y * Stride) + (x >> 6)];
        ulong bit = 1UL << (63 - (x & 63));
        word = value != 0 ? word | bit : word & ~bit;
    }

    internal Span<ulong> Row(int y) => Words.AsSpan(y * Stride, Stride);

    /// <summary>Sets every pixel to <paramref name="value"/>, keeping the bits past the width zero.</summary>
    internal void Fill(int value)
    {
        if (value == 0)
        {
            Array.Clear(Words);
            return;
        }

        for (int y = 0; y < Height; y++)
        {
            Span<ulong> row = Row(y);
            row.Fill(ulong.MaxValue);
            row[^1] = EdgeMask(Width);
        }
    }

    /// <summary>Copies row <paramref name="from"/> over row <paramref name="to"/>.</summary>
    internal void CopyRow(int from, int to)
    {
        Words.AsSpan(from * Stride, Stride).CopyTo(Words.AsSpan(to * Stride, Stride));
    }

    /// <summary>Grows the bitmap to <paramref name="height"/> rows, filling the new rows with <paramref name="value"/> (a page of unknown height).</summary>
    internal void GrowTo(int height, int value)
    {
        if (height <= Height)
        {
            return;
        }

        ulong[] words = new ulong[checked((long)Stride * height)];
        Words.AsSpan().CopyTo(words);
        int old = Height;
        Words = words;
        Height = height;
        if (value != 0)
        {
            for (int y = old; y < height; y++)
            {
                Span<ulong> row = Row(y);
                row.Fill(ulong.MaxValue);
                row[^1] = EdgeMask(Width);
            }
        }
    }

    /// <summary>Returns a copy of the rectangle at (<paramref name="x"/>, <paramref name="y"/>); pixels outside the bitmap are 0.</summary>
    internal Jbig2Bitmap Crop(int x, int y, int width, int height)
    {
        Jbig2Bitmap crop = new(width, height);
        crop.Compose(this, -x, -y, Jbig2Combine.Replace);
        return crop;
    }

    /// <summary>
    /// Combines <paramref name="source"/> into this bitmap with its top left at (<paramref name="x"/>, <paramref name="y"/>),
    /// clipped to both bitmaps, a word at a time (T.88 6.4.5 step 3 c x, 7.4.6.4 and 8.2).
    /// </summary>
    internal void Compose(Jbig2Bitmap source, int x, int y, Jbig2Combine op)
    {
        int sx0 = Math.Max(0, -x);
        int sy0 = Math.Max(0, -y);
        int sx1 = Math.Min(source.Width, Width - x);
        int sy1 = Math.Min(source.Height, Height - y);
        if (sx0 >= sx1 || sy0 >= sy1)
        {
            return;
        }

        int dx0 = x + sx0;
        int dx1 = x + sx1;
        int firstWord = dx0 >> 6;
        int lastWord = (dx1 - 1) >> 6;
        ulong firstMask = ulong.MaxValue >> (dx0 & 63);
        ulong lastMask = EdgeMask(dx1);
        int shift = dx0 - sx0;
        for (int sy = sy0; sy < sy1; sy++)
        {
            ReadOnlySpan<ulong> src = source.Words.AsSpan(sy * source.Stride, source.Stride);
            Span<ulong> dst = Words.AsSpan((sy + y) * Stride, Stride);
            for (int w = firstWord; w <= lastWord; w++)
            {
                ulong mask = ulong.MaxValue;
                if (w == firstWord)
                {
                    mask &= firstMask;
                }

                if (w == lastWord)
                {
                    mask &= lastMask;
                }

                ulong bits = Fetch(src, (w << 6) - shift);
                ulong old = dst[w];
                ulong combined = op switch
                {
                    Jbig2Combine.Or => old | bits,
                    Jbig2Combine.And => old & bits,
                    Jbig2Combine.Xor => old ^ bits,
                    Jbig2Combine.Xnor => ~(old ^ bits),
                    _ => bits,
                };
                dst[w] = (old & ~mask) | (combined & mask);
            }
        }
    }

    /// <summary>Returns the 64 bits of a row starting at bit <paramref name="start"/>, which may be negative; bits outside the row are 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Fetch(ReadOnlySpan<ulong> row, int start)
    {
        int word = start >> 6;
        int bit = start & 63;
        ulong high = (uint)word < (uint)row.Length ? row[word] : 0;
        if (bit == 0)
        {
            return high;
        }

        ulong low = (uint)(word + 1) < (uint)row.Length ? row[word + 1] : 0;
        return (high << bit) | (low >> (64 - bit));
    }

    /// <summary>Returns the mask of a row's last word holding pixels 0 to <paramref name="width"/> - 1 of it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong EdgeMask(int width)
    {
        int bits = width & 63;
        return bits == 0 ? ulong.MaxValue : ~(ulong.MaxValue >> bits);
    }

    /// <summary>Counts the black pixels, for tests and checks.</summary>
    internal long CountBlack()
    {
        long count = 0;
        foreach (ulong word in Words)
        {
            count += BitOperations.PopCount(word);
        }

        return count;
    }
}
