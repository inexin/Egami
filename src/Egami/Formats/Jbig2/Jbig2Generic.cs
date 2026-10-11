using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
        (0, true) => NominalExtended,
        (0, false) => Nominal0,
        (1, _) => Nominal1,
        _ => Nominal2,
    };

    private static readonly int[] NominalExtended = [-2, 0, 0, -2, -2, -1, -1, -2, 1, -2, 2, -1, -3, 0, -4, 0, 2, -2, 3, -1, -2, -2, -3, -1];
    private static readonly int[] Nominal0 = [3, -1, -3, -1, 2, -2, -2, -2];
    private static readonly int[] Nominal1 = [3, -1];
    private static readonly int[] Nominal2 = [2, -1];
}

/// <summary>
/// The generic region decoding procedure (T.88 6.2): template-based arithmetic coding with typical prediction and skipped pixels,
/// or MMR. Contexts gather the template's pixels in reading order with the AT pixels at their nominal places, the order of the
/// T.88 examples, so the SLTP contexts of Figures 8 to 11 are fixed numbers. Fixed pixels are kept in sliding windows, one per
/// row, and AT pixels away from their nominal places are fetched one by one.
/// </summary>
internal static class Jbig2Generic
{
    /// <summary>The bytes an MQ decoder may read past its data before a region stops decoding.</summary>
    internal const int MqDecoderExhausted = 64;

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
        if (skip is null && layout.At.Length == 0)
        {
            FastRegion(mq, contexts, layout, bitmap, p.TypicalPrediction, sltp);
            return bitmap;
        }

        int ltp = 0;
        for (int y = 0; y < p.Height; y++)
        {
            if (mq.Overrun > MqDecoderExhausted)
            {
                // The data ran out long ago: leave the rest of the region as it is rather than decode 1-bits for ever.
                break;
            }

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

    /// <summary>
    /// Decodes a region whose template pixels all lie in the row windows (the AT pixels at their nominal places) with nothing
    /// skipped. The context is one register: each step shifts it left a pixel, the mask drops the bits that would cross from one
    /// row's group into the next, and the new pixels of the rows above and the pixel just decoded come in at the bottom of their
    /// groups. The rows above are read 64 pixels at a time, and the MQ decoder runs inline on locals for the whole region
    /// (T.88 E.3: DECODE, RENORMD and BYTEIN), so a small symbol costs little more than its pixels. A long run of white pixels
    /// whose context stays 0 is taken in one step, since every decision in it is the MPS without renormalization.
    /// </summary>
    private static void FastRegion(MqDecoder mq, byte[] contexts, Layout layout, Jbig2Bitmap bitmap, bool typicalPrediction, int sltp)
    {
        ulong[] words = bitmap.Words;
        int stride = bitmap.Stride;
        int width = bitmap.Width;
        ref byte context0 = ref MemoryMarshal.GetArrayDataReference(contexts);
        ref uint states = ref MemoryMarshal.GetArrayDataReference(MqDecoder.StateTable);
        byte[] data = mq.Data;
        int end = mq.End;
        int overrun = 0;
        uint a = mq.Registers.A;
        uint c = mq.Registers.C;
        int ct = mq.Registers.Ct;
        int position = mq.Registers.Position;
        uint keep = layout.Keep;
        int shift1 = layout.Shift1;
        int shift2 = layout.Shift2;
        int lo1 = layout.Lo1;
        int lo2 = layout.Lo2;
        int width1 = layout.Hi1 - layout.Lo1 + 1;
        int width2 = layout.Hi2 - layout.Lo2 + 1;
        int start1 = layout.Hi1 + 1;
        int start2 = layout.Hi2 + 1;
        int ltp = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            if (mq.Overrun + overrun > MqDecoderExhausted)
            {
                // The data ran out long ago: leave the rest of the region as it is rather than decode 1-bits for ever.
                break;
            }

            int row = y * stride;
            if (typicalPrediction)
            {
                mq.Registers = new MqRegisters { A = a, C = c, Ct = ct, Position = position };
                ltp ^= mq.Decode(ref contexts[sltp]);
                (a, c, ct, position) = (mq.Registers.A, mq.Registers.C, mq.Registers.Ct, mq.Registers.Position);
                if (ltp != 0)
                {
                    if (y > 0)
                    {
                        bitmap.CopyRow(y - 1, y);
                    }

                    continue;
                }
            }

            ReadOnlySpan<ulong> above1 = y >= 1 ? words.AsSpan(row - stride, stride) : default;
            ReadOnlySpan<ulong> above2 = y >= 2 && width2 > 0 ? words.AsSpan(row - (2 * stride), stride) : default;
            uint context = (uint)(Jbig2Bitmap.Fetch(above1, lo1) >> (64 - width1)) << shift1;
            if (width2 > 0)
            {
                context |= (uint)(Jbig2Bitmap.Fetch(above2, lo2) >> (64 - width2)) << shift2;
            }

            for (int x0 = 0; x0 < width; x0 += 64)
            {
                ulong bits1 = Jbig2Bitmap.Fetch(above1, x0 + start1);
                ulong bits2 = Jbig2Bitmap.Fetch(above2, x0 + start2);
                int count = Math.Min(64, width - x0);
                ulong gathered = 0;
                for (int i = 0; i < count; i++)
                {
                    if (context == 0 && (context0 & 1) == 0)
                    {
                        // White on white: while the rows above stay white the context stays 0, and while A and C allow, every
                        // decision is the MPS without renormalization, which changes nothing but A and C.
                        int window = 1 + Math.Min(BitOperations.LeadingZeroCount(bits1), BitOperations.LeadingZeroCount(bits2));
                        int run = Math.Min(window, count - i);
                        if (run > 4)
                        {
                            uint qe0 = Unsafe.Add(ref states, (nint)context0) & 0xFFFF;
                            run = (int)Math.Min((uint)run, Math.Min((c >> 16) / qe0, (a - 0x8000) / qe0));
                            if (run > 1)
                            {
                                uint total = (uint)run * qe0;
                                a -= total;
                                c -= total << 16;
                                gathered = run == 64 ? 0 : gathered << run;

                                // The next context holds what came in after the last white pixel: the run's last incoming bits.
                                ulong last1 = bits1 << (run - 1);
                                ulong last2 = bits2 << (run - 1);
                                context = ((uint)(last1 >> 63) << shift1) | ((uint)(last2 >> 63) << shift2);
                                bits1 = last1 << 1;
                                bits2 = last2 << 1;
                                i += run - 1;
                                continue;
                            }
                        }
                    }

                    ref byte cx = ref Unsafe.Add(ref context0, (nint)context);
                    uint s = cx;
                    uint entry = Unsafe.Add(ref states, (nint)s);
                    uint qe = entry & 0xFFFF;
                    a -= qe;
                    uint d;
                    if ((c >> 16) >= qe)
                    {
                        c -= qe << 16;
                        if ((a & 0x8000) != 0)
                        {
                            d = s & 1;
                            goto Decoded;
                        }

                        // MPS_EXCHANGE.
                        if (a < qe)
                        {
                            d = (s & 1) ^ 1;
                            cx = (byte)(entry >> 24);
                        }
                        else
                        {
                            d = s & 1;
                            cx = (byte)(entry >> 16);
                        }
                    }
                    else
                    {
                        // LPS_EXCHANGE.
                        if (a < qe)
                        {
                            d = s & 1;
                            cx = (byte)(entry >> 16);
                        }
                        else
                        {
                            d = (s & 1) ^ 1;
                            cx = (byte)(entry >> 24);
                        }

                        a = qe;
                    }

                    // RENORMD with BYTEIN: when C holds enough bits, all the doublings at once.
                    int shift = BitOperations.LeadingZeroCount(a) - 16;
                    if (shift <= ct)
                    {
                        a <<= shift;
                        c <<= shift;
                        ct -= shift;
                        goto Decoded;
                    }

                    do
                    {
                        if (ct == 0)
                        {
                            byte b = (uint)position < (uint)end ? data[position] : (byte)0xFF;
                            byte next = (uint)(position + 1) < (uint)end ? data[position + 1] : (byte)0xFF;
                            if (b != 0xFF)
                            {
                                position++;
                                c += (uint)next << 8;
                                ct = 8;
                            }
                            else if (next <= 0x8F)
                            {
                                position++;
                                c += (uint)next << 9;
                                ct = 7;
                            }
                            else
                            {
                                c += 0xFF00;
                                ct = 8;
                                overrun += position >= end ? 1 : 0;
                            }
                        }

                        a <<= 1;
                        c <<= 1;
                        ct--;
                    }
                    while ((a & 0x8000) == 0);

                Decoded:
                    gathered = (gathered << 1) | d;
                    context = ((context << 1) & keep) | d | ((uint)(bits1 >> 63) << shift1) | ((uint)(bits2 >> 63) << shift2);
                    bits1 <<= 1;
                    bits2 <<= 1;
                }

                words[row + (x0 >> 6)] = gathered << (64 - count);
            }
        }

        mq.Registers = new MqRegisters { A = a, C = c, Ct = ct, Position = position };
        mq.Overrun += overrun;
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
            // Rows below the current one are not decoded yet, so read as 0, as rows above the region do.
            int ay = y + dy;
            if (ay >= 0 && ay <= y)
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

        /// <summary>
        /// Gets the context bits that survive a shift of one pixel within their row's group: every group's bits but its top one,
        /// moved up a place (the fast loop's mask).
        /// </summary>
        internal uint Keep { get; private init; }

        internal (int Dx, int Dy, int Bit)[] At { get; private init; } = [];

        /// <summary>The layouts of the four templates with their AT pixels at the nominal places, made once.</summary>
        private static readonly Layout[] Nominal =
        [
            Windows((-2, 2, 11), (-3, 3, 4), (4, 0), []),
            Windows((-1, 2, 9), (-2, 3, 3), (3, 0), []),
            Windows((-1, 1, 7), (-2, 2, 2), (2, 0), []),
            Windows((0, -1, 0), (-3, 2, 4), (4, 0), []),
        ];

        internal static Layout For(Jbig2GenericParams p)
        {
            int[] at = p.At;
            int[] nominal = Jbig2GenericParams.NominalAt(p.Template, p.ExtendedTemplate && p.Template == 0);
            bool isNominal = at.Length >= nominal.Length && at.AsSpan(0, nominal.Length).SequenceEqual(nominal);

            // The nominal layouts are the reading order of the template's pixels; an extended template with its AT pixels at their
            // nominal places covers exactly template 0's pixels, so it reads the same.
            if (isNominal)
            {
                return Nominal[p.Template];
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

        private static uint Group(int bits, int shift) => bits <= 1 ? 0 : ((1u << (bits - 1)) - 1) << (shift + 1);

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
                Keep = Group(row0.Count, row0.Shift) | Group(row1.Hi - row1.Lo + 1, row1.Shift) | Group(row2.Hi - row2.Lo + 1, row2.Shift),
                At = at,
            };
        }
    }
}
