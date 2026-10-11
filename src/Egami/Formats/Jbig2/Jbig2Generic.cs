using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Egami.Formats.Jbig2;

/// <summary>The parameters of the generic region decoding procedure (T.88 6.2.2, Table 2).</summary>
internal sealed class Jbig2GenericParams
{
    internal bool Mmr { get; init; }

    internal int Width { get; init; }

    internal int Height { get; init; }

    internal int Template { get; init; }

    internal bool TypicalPrediction { get; init; }

    internal bool ExtendedTemplate { get; init; }

    /// <summary>Gets the adaptive template pixels A1 to A4 (or A1 to A12 with the extended template), as (x, y) pairs.</summary>
    internal int[] At { get; init; } = [];

    /// <summary>Gets the pixels to skip (USESKIP), or <see langword="null"/>.</summary>
    internal Jbig2Bitmap? Skip { get; init; }

    /// <summary>Returns the nominal AT pixels of a template (Table 5).</summary>
    internal static int[] NominalAt(int template, bool extended) => (template, extended) switch
    {
        (0, true) => [-2, 0, 0, -2, -2, -1, -1, -2, 1, -2, 2, -1, -3, 0, -4, 0, 2, -2, 3, -1, -2, -2, -3, -1],
        (0, false) => [3, -1, -3, -1, 2, -2, -2, -2],
        (1, _) => [3, -1],
        _ => [2, -1],
    };
}

/// <summary>
/// The generic region decoding procedure (T.88 6.2): template-based arithmetic coding with typical prediction and skipped pixels,
/// or MMR. Contexts gather the template's pixels in reading order with the AT pixels at their nominal places, the order of the
/// T.88 examples, so the SLTP contexts of Figures 8 to 11 are fixed numbers. Fixed pixels are kept in sliding windows, one per
/// row, and AT pixels away from their nominal places are fetched one by one.
/// </summary>
internal static class Jbig2Generic
{
    /// <summary>Gets the number of contexts each template needs.</summary>
    internal static int ContextCount(int template) => template == 0 ? 1 << 16 : template == 1 ? 1 << 13 : 1 << 10;

    /// <summary>Decodes a region with arithmetic coding, using and updating <paramref name="contexts"/>.</summary>
    internal static Jbig2Bitmap DecodeArithmetic(Jbig2GenericParams p, MqDecoder mq, byte[] contexts)
    {
        Jbig2Bitmap bitmap = new(p.Width, p.Height);
        if (p.Width == 0 || p.Height == 0)
        {
            return bitmap;
        }

        Layout layout = Layout.For(p);
        int sltp = p.Template switch
        {
            0 => 0x9B25,
            1 => 0x0795,
            2 => 0x00E5,
            _ => 0x0195,
        };
        ulong[] words = bitmap.Words;
        int stride = bitmap.Stride;
        int width = p.Width;
        ulong[]? skip = p.Skip?.Words;
        int ltp = 0;
        for (int y = 0; y < p.Height; y++)
        {
            int row = y * stride;
            if (p.TypicalPrediction)
            {
                ltp ^= mq.Decode(ref contexts[sltp]);
                if (ltp != 0)
                {
                    if (y > 0)
                    {
                        bitmap.CopyRow(y - 1, y);
                    }

                    continue;
                }
            }

            int row1 = y >= 1 ? row - stride : -1;
            int row2 = y >= 2 ? row - (2 * stride) : -1;
            uint w2 = Start(words, row2, layout.Lo2, layout.Hi2, width);
            uint w1 = Start(words, row1, layout.Lo1, layout.Hi1, width);
            uint w0 = 0;
            for (int x = 0; x < width; x++)
            {
                int d = 0;
                if (skip is null || ((skip[row + (x >> 6)] >> (~x & 63)) & 1) == 0)
                {
                    int context = (int)(((w2 & layout.Mask2) << layout.Shift2) | ((w1 & layout.Mask1) << layout.Shift1) | ((w0 & layout.Mask0) << layout.Shift0));
                    if (layout.At.Length != 0)
                    {
                        context |= AtBits(words, stride, layout.At, x, y, width);
                    }

                    d = mq.Decode(ref contexts[context]);
                    if (d != 0)
                    {
                        words[row + (x >> 6)] |= 1UL << (~x & 63);
                    }
                }

                w0 = (w0 << 1) | (uint)d;
                w1 = (w1 << 1) | (uint)Pixel(words, row1, x + 1 + layout.Hi1, width);
                w2 = (w2 << 1) | (uint)Pixel(words, row2, x + 1 + layout.Hi2, width);
            }
        }

        return bitmap;
    }

    /// <summary>Decodes an MMR region (T.88 6.2.6) from <paramref name="data"/>; returns the bitmap and the bytes used.</summary>
    internal static (Jbig2Bitmap Bitmap, int Consumed) DecodeMmr(int width, int height, ReadOnlySpan<byte> data)
    {
        Jbig2Bitmap bitmap = new(width, height);
        if (width == 0 || height == 0)
        {
            return (bitmap, 0);
        }

        int rowBytes = (width + 7) / 8;
        byte[] rows = new byte[checked(rowBytes * height)];
        Tiff.CcittFax.DecodeMmr(data, rows, width, height, out int consumed);
        FromBytes(bitmap, rows, rowBytes);
        return (bitmap, consumed);
    }

    /// <summary>Fills a bitmap from rows of bytes, most significant bit first, each row padded to a byte.</summary>
    internal static void FromBytes(Jbig2Bitmap bitmap, ReadOnlySpan<byte> rows, int rowBytes)
    {
        int stride = bitmap.Stride;
        ulong edge = Jbig2Bitmap.EdgeMask(bitmap.Width);
        Span<byte> word = stackalloc byte[8];
        for (int y = 0; y < bitmap.Height; y++)
        {
            ReadOnlySpan<byte> source = rows.Slice(y * rowBytes, rowBytes);
            Span<ulong> target = bitmap.Row(y);
            for (int w = 0; w < stride; w++)
            {
                int start = w * 8;
                int count = Math.Min(8, source.Length - start);
                word.Clear();
                source.Slice(start, count).CopyTo(word);
                target[w] = BinaryPrimitives.ReadUInt64BigEndian(word);
            }

            target[^1] &= edge;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Pixel(ulong[] words, int row, int x, int width)
    {
        return row >= 0 && (uint)x < (uint)width ? (int)(words[row + (x >> 6)] >> (~x & 63)) & 1 : 0;
    }

    /// <summary>Returns the window of a row's pixels from x + <paramref name="lo"/> to x + <paramref name="hi"/> at x = 0, the last in bit 0.</summary>
    private static uint Start(ulong[] words, int row, int lo, int hi, int width)
    {
        uint w = 0;
        for (int dx = lo; dx <= hi; dx++)
        {
            w = (w << 1) | (uint)Pixel(words, row, dx, width);
        }

        return w;
    }

    private static int AtBits(ulong[] words, int stride, (int Dx, int Dy, int Bit)[] at, int x, int y, int width)
    {
        int bits = 0;
        foreach ((int dx, int dy, int bit) in at)
        {
            int ay = y + dy;
            if (ay >= 0)
            {
                bits |= Pixel(words, ay * stride, x + dx, width) << bit;
            }
        }

        return bits;
    }

    /// <summary>
    /// Where each part of a template's context comes from: a window over row y - 2, one over row y - 1, one over the decoded
    /// pixels of row y, and the AT pixels not at their nominal places, each with its bit in the context.
    /// </summary>
    private sealed class Layout
    {
        internal int Lo2 { get; private init; }

        internal int Hi2 { get; private init; } = -1;

        internal uint Mask2 { get; private init; }

        internal int Shift2 { get; private init; }

        internal int Lo1 { get; private init; }

        internal int Hi1 { get; private init; } = -1;

        internal uint Mask1 { get; private init; }

        internal int Shift1 { get; private init; }

        internal uint Mask0 { get; private init; }

        internal int Shift0 { get; private init; }

        internal (int Dx, int Dy, int Bit)[] At { get; private init; } = [];

        internal static Layout For(Jbig2GenericParams p)
        {
            int[] at = p.At;
            int[] nominal = Jbig2GenericParams.NominalAt(p.Template, p.ExtendedTemplate && p.Template == 0);
            bool isNominal = at.Length >= nominal.Length && at.AsSpan(0, nominal.Length).SequenceEqual(nominal);

            // The nominal layouts are the reading order of the template's pixels; an extended template with its AT pixels at their
            // nominal places covers exactly template 0's pixels, so it reads the same.
            if (isNominal)
            {
                return p.Template switch
                {
                    0 => Windows((-2, 2, 11), (-3, 3, 4), (4, 0), []),
                    1 => Windows((-1, 2, 9), (-2, 3, 3), (3, 0), []),
                    2 => Windows((-1, 1, 7), (-2, 2, 2), (2, 0), []),
                    _ => Windows((0, -1, 0), (-3, 2, 4), (4, 0), []),
                };
            }

            (int, int, int)[] Ats(params int[] bits)
            {
                (int, int, int)[] list = new (int, int, int)[bits.Length];
                for (int i = 0; i < bits.Length; i++)
                {
                    list[i] = (at[2 * i], at[(2 * i) + 1], bits[i]);
                }

                return list;
            }

            if (p.Template == 0 && p.ExtendedTemplate)
            {
                return Windows((0, -1, 0), (-1, 1, 6), (1, 0), Ats(1, 13, 9, 14, 12, 5, 2, 3, 11, 4, 15, 10));
            }

            return p.Template switch
            {
                0 => Windows((-1, 1, 12), (-2, 2, 5), (4, 0), Ats(4, 10, 11, 15)),
                1 => Windows((-1, 2, 9), (-2, 2, 4), (3, 0), Ats(3)),
                2 => Windows((-1, 1, 7), (-2, 1, 3), (2, 0), Ats(2)),
                _ => Windows((0, -1, 0), (-3, 1, 5), (4, 0), Ats(4)),
            };
        }

        private static Layout Windows((int Lo, int Hi, int Shift) row2, (int Lo, int Hi, int Shift) row1, (int Count, int Shift) row0, (int, int, int)[] at)
        {
            return new Layout
            {
                Lo2 = row2.Lo,
                Hi2 = row2.Hi,
                Mask2 = row2.Hi < row2.Lo ? 0 : (1u << (row2.Hi - row2.Lo + 1)) - 1,
                Shift2 = row2.Shift,
                Lo1 = row1.Lo,
                Hi1 = row1.Hi,
                Mask1 = (1u << (row1.Hi - row1.Lo + 1)) - 1,
                Shift1 = row1.Shift,
                Mask0 = (1u << row0.Count) - 1,
                Shift0 = row0.Shift,
                At = at,
            };
        }
    }
}
