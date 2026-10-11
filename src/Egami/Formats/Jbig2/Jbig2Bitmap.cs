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
    internal void Compose(Jbig2Bitmap source, int x, int y, Jbig2Combine op) => Compose(source, x, y, op, 0, 0, Width, Height);

    /// <summary>Composes as <see cref="Compose(Jbig2Bitmap, int, int, Jbig2Combine)"/> does, changing only the pixels inside a clip rectangle.</summary>
    internal void Compose(Jbig2Bitmap source, int x, int y, Jbig2Combine op, int clipLeft, int clipTop, int clipRight, int clipBottom)
    {
        clipLeft = Math.Max(clipLeft, 0);
        clipTop = Math.Max(clipTop, 0);
        clipRight = Math.Min(clipRight, Width);
        clipBottom = Math.Min(clipBottom, Height);
        switch (op)
        {
            case Jbig2Combine.Or:
                Compose<OrOp>(source, x, y, clipLeft, clipTop, clipRight, clipBottom);
                break;
            case Jbig2Combine.And:
                Compose<AndOp>(source, x, y, clipLeft, clipTop, clipRight, clipBottom);
                break;
            case Jbig2Combine.Xor:
                Compose<XorOp>(source, x, y, clipLeft, clipTop, clipRight, clipBottom);
                break;
            case Jbig2Combine.Xnor:
                Compose<XnorOp>(source, x, y, clipLeft, clipTop, clipRight, clipBottom);
                break;
            default:
                Compose<ReplaceOp>(source, x, y, clipLeft, clipTop, clipRight, clipBottom);
                break;
        }
    }

    /// <summary>
    /// Composes with one operator, a row at a time: the source row is read as a run of 64-bit words shifted into the target's
    /// word grid, the first and last target words masked to the clipped columns.
    /// </summary>
    private void Compose<TOp>(Jbig2Bitmap source, int x, int y, int clipLeft, int clipTop, int clipRight, int clipBottom)
        where TOp : struct, ICombineOp
    {
        int sx0 = (int)Math.Max(0, (long)clipLeft - x);
        int sy0 = (int)Math.Max(0, (long)clipTop - y);
        int sx1 = (int)Math.Min(source.Width, (long)clipRight - x);
        int sy1 = (int)Math.Min(source.Height, (long)clipBottom - y);
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
        if (firstWord == lastWord)
        {
            firstMask &= lastMask;
        }

        // Target word w holds source bits from (w * 64 - x) on: source word k = (w * 64 - x) >> 6 shifted left by r, with the
        // next source word's top bits after it.
        int offset = (firstWord << 6) - x;
        int r = offset & 63;
        int k0 = offset >> 6;
        ulong[] srcWords = source.Words;
        ulong[] dstWords = Words;
        int srcStride = source.Stride;
        for (int sy = sy0; sy < sy1; sy++)
        {
            int srcRow = sy * srcStride;
            int dstRow = ((sy + y) * Stride) + firstWord;
            int k = k0;
            for (int w = 0, words = lastWord - firstWord + 1; w < words; w++, k++)
            {
                ulong high = (uint)k < (uint)srcStride ? srcWords[srcRow + k] : 0;
                ulong bits = high << r;
                if (r != 0 && (uint)(k + 1) < (uint)srcStride)
                {
                    bits |= srcWords[srcRow + k + 1] >> (64 - r);
                }

                ref ulong target = ref dstWords[dstRow + w];
                ulong mask = w == 0 ? firstMask : w == words - 1 ? lastMask : ulong.MaxValue;
                ulong old = target;
                target = (old & ~mask) | (TOp.Apply(old, bits) & mask);
            }
        }
    }

    private interface ICombineOp
    {
        static abstract ulong Apply(ulong old, ulong bits);
    }

    private struct OrOp : ICombineOp
    {
        public static ulong Apply(ulong old, ulong bits) => old | bits;
    }

    private struct AndOp : ICombineOp
    {
        public static ulong Apply(ulong old, ulong bits) => old & bits;
    }

    private struct XorOp : ICombineOp
    {
        public static ulong Apply(ulong old, ulong bits) => old ^ bits;
    }

    private struct XnorOp : ICombineOp
    {
        public static ulong Apply(ulong old, ulong bits) => ~(old ^ bits);
    }

    private struct ReplaceOp : ICombineOp
    {
        public static ulong Apply(ulong old, ulong bits) => bits;
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
