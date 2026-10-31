using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Formats.Av1;

/// <summary>
/// AV1's inverse transforms, after libaom's av1_inv_txfm2d.c and idct.c (BSD 2-clause, see THIRD-PARTY-NOTICES): rows, then
/// columns, then the residual added to the prediction and clipped; the Walsh-Hadamard transform for lossless blocks.
/// </summary>
// Every stack buffer here is written before it is read (as in libaom, whose arrays are not initialized either), so skipping
// the zeroing is safe and saves it on each transform block.
[System.Runtime.CompilerServices.SkipLocalsInit]
internal static partial class Av1InverseTransform
{
    private const int CosBit = 12;
    private const int NewSqrt2 = 5793;
    private const int NewInvSqrt2 = 2896;
    private const int NewSqrt2Bits = 12;

    /// <summary>av1_inv_txfm_shift_ls: the rounding after the rows of each transform size (the columns always shift by 4).</summary>
    private static ReadOnlySpan<byte> RowShift => [0, 1, 2, 2, 2, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2];

    private static ReadOnlySpan<int> Cospi => Av1Tables.CospiData.AsSpan(128, 64);

    private static ReadOnlySpan<int> Sinpi => Av1Tables.SinpiData.AsSpan(10, 5);

    /// <summary>
    /// Adds the inverse transform of a block's coefficients to the prediction at <paramref name="offset"/>.
    /// </summary>
    /// <param name="coeffs">The coefficients, column by column at the coded size (64-point sizes code 32).</param>
    /// <param name="txSize">The transform size.</param>
    /// <param name="txType">The transform type.</param>
    /// <param name="lossless">Whether the block is lossless (the Walsh-Hadamard transform).</param>
    /// <param name="eob">The end of block.</param>
    /// <param name="dst">The plane.</param>
    /// <param name="offset">Where the block's top left sample is.</param>
    /// <param name="stride">The plane's stride.</param>
    /// <param name="bitDepth">The bit depth.</param>
    internal static void Add<TSample>(int[] coeffs, int txSize, int txType, bool lossless, int eob, TSample[] dst, int offset, int stride, int bitDepth)
        where TSample : unmanaged
    {
        if (lossless)
        {
            if (eob > 1)
            {
                Wht4x4(coeffs, dst, offset, stride, bitDepth);
            }
            else
            {
                Wht4x4Dc(coeffs, dst, offset, stride, bitDepth);
            }

            return;
        }

        int w = Av1Tables.TxSizeWide[txSize], h = Av1Tables.TxSizeHigh[txSize];
        int adjusted = Av1Tables.AdjustedTxSize(txSize);
        int codedW = Av1Tables.TxSizeWide[adjusted], codedH = Av1Tables.TxSizeHigh[adjusted];
        int rangeRow = bitDepth == 8 ? 16 : bitDepth == 10 ? 18 : 20;
        int rangeCol = bitDepth == 12 ? 18 : 16;
        int colType = Av1Tables.VtxTab[txType], rowType = Av1Tables.HtxTab[txType];
        bool udFlip = colType == 2, lrFlip = rowType == 2;
        int rowShift = RowShift[txSize];
        bool rect2 = w == 2 * h || h == 2 * w;
        int rowClamp = bitDepth + 8, colClamp = Math.Max(bitDepth + 6, 16);
        int max = (1 << bitDepth) - 1;
        if (Vector128.IsHardwareAccelerated && bitDepth == 8 && (w == 4 || h == 4) && w <= 16 && h <= 16)
        {
            AddNarrow(coeffs, w, h, rect2, rowShift, new Types(rowType, colType), dst, offset, stride);
            return;
        }

        if (Vector256.IsHardwareAccelerated && w >= 8 && h >= 8)
        {
            Types types = new(rowType, colType);
            if (bitDepth <= 10)
            {
                AddVector<Narrow, TSample>(coeffs, w, h, codedW, codedH, rect2, rowShift, types, (rowClamp, rangeRow), (colClamp, rangeCol), dst, offset, stride, max);
            }
            else
            {
                AddVector<Wide, TSample>(coeffs, w, h, codedW, codedH, rect2, rowShift, types, (rowClamp, rangeRow), (colClamp, rangeCol), dst, offset, stride, max);
            }

            return;
        }

        Span<int> buf = stackalloc int[w * h];
        Span<int> tempIn = stackalloc int[64];
        Span<int> tempOut = stackalloc int[64];
        for (int r = 0; r < h; r++)
        {
            Span<int> row = buf.Slice(r * w, w);
            if (r >= codedH)
            {
                row.Clear();
                continue;
            }

            bool any = false;
            for (int c = 0; c < w; c++)
            {
                int v = c < codedW ? coeffs[(c * codedH) + r] : 0;
                if (rect2)
                {
                    v = RoundShift((long)v * NewInvSqrt2, NewSqrt2Bits);
                }

                tempIn[c] = Clamp(v, rowClamp);
                any |= v != 0;
            }

            if (!any)
            {
                row.Clear();
                continue;
            }

            Transform(rowType, w, tempIn[..w], row, rangeRow);
            if (rowShift > 0)
            {
                for (int c = 0; c < w; c++)
                {
                    row[c] = RoundShift(row[c], rowShift);
                }
            }
        }

        // The columns, into the residual a row at a time. A column like the one before it (as every column of a block that codes
        // only vertical frequencies, the DC alone among them) has the same transform, so it is copied instead.
        Span<int> residuals = stackalloc int[w * h];
        Span<int> previous = stackalloc int[64];
        for (int c = 0; c < w; c++)
        {
            int source = lrFlip ? w - 1 - c : c;
            for (int r = 0; r < h; r++)
            {
                tempIn[r] = Clamp(buf[(r * w) + source], colClamp);
            }

            if (c == 0 || !tempIn[..h].SequenceEqual(previous[..h]))
            {
                Transform(colType, h, tempIn[..h], tempOut[..h], rangeCol);
                tempIn[..h].CopyTo(previous);
            }

            for (int r = 0; r < h; r++)
            {
                residuals[(r * w) + c] = RoundShift(tempOut[udFlip ? h - 1 - r : r], 4);
            }
        }

        for (int r = 0; r < h; r++)
        {
            Span<TSample> line = dst.AsSpan(offset + (r * stride), w);
            ReadOnlySpan<int> residual = residuals.Slice(r * w, w);
            for (int c = 0; c < line.Length; c++)
            {
                line[c] = Av1Samples.From<TSample>(Math.Clamp(Av1Samples.Get(line[c]) + residual[c], 0, max));
            }
        }
    }

    /// <summary>
    /// A butterfly (half_btf) on eight lanes: each weight times its input wrapping at 32 bits, as the scalar one multiplies, and
    /// their sum rounded and shifted as if it were taken in 64 bits.
    /// </summary>
    internal interface IButterfly
    {
        static abstract Vector256<int> HalfBtf(int w0, Vector256<int> in0, int w1, Vector256<int> in1, int bit);

        /// <summary>Gets round_shift(w * x, bit) as if taken in 64 bits.</summary>
        static abstract Vector256<int> Scale(Vector256<int> x, int w, int bit);
    }

    /// <summary>A block's one-dimensional transform types (0 DCT, 1 ADST, 2 flipped ADST, 3 identity), across its rows and down its columns.</summary>
    private readonly record struct Types(int Row, int Column);

    /// <summary>
    /// The blocks of 8 and more samples a side, eight rows and then eight columns at a time, with the butterfly
    /// <typeparamref name="T"/> that is exact at the bit depth; flipped ADSTs read their columns right to left and write their
    /// rows bottom to top, as the scalar path does.
    /// </summary>
    private static void AddVector<T, TSample>(int[] coeffs, int w, int h, int codedW, int codedH, bool rect2, int rowShift, Types types, (int Clamp, int Range) row,
        (int Clamp, int Range) column, TSample[] dst, int offset, int stride, int max)
        where T : IButterfly
        where TSample : unmanaged
    {
        Span<int> buf = stackalloc int[w * h];
        Span<Vector256<int>> input = stackalloc Vector256<int>[64];
        Span<Vector256<int>> output = stackalloc Vector256<int>[64];
        ref int c0 = ref MemoryMarshal.GetArrayDataReference(coeffs);

        // Rows, a lane each: the coefficients are stored column by column, so a column's eight rows are adjacent.
        (Vector256<int> lo, Vector256<int> hi) = Bounds(row.Clamp);
        (Vector256<int> rangeLo, Vector256<int> rangeHi) = Bounds(row.Range);
        buf[(codedH * w)..].Clear();
        for (int r0 = 0; r0 < codedH; r0 += 8)
        {
            Vector256<int> any = Vector256<int>.Zero;
            for (int c = 0; c < codedW; c++)
            {
                Vector256<int> v = Vector256.LoadUnsafe(ref c0, (nuint)((c * codedH) + r0));
                if (rect2)
                {
                    v = Vector256.ShiftRightArithmetic((v * NewInvSqrt2) + Vector256.Create(1 << (NewSqrt2Bits - 1)), NewSqrt2Bits);
                }

                input[c] = Vector256.Min(Vector256.Max(v, lo), hi);
                any |= v;
            }

            if (any == Vector256<int>.Zero)
            {
                for (int r = r0; r < r0 + 8; r++)
                {
                    buf.Slice(r * w, w).Clear();
                }

                continue;
            }

            input[codedW..w].Clear();
            Transform<T>(types.Row, w, input[..w], output[..w], rangeLo, rangeHi);
            if (rowShift > 0)
            {
                Vector256<int> half = Vector256.Create(1 << (rowShift - 1));
                for (int c = 0; c < w; c++)
                {
                    output[c] = Vector256.ShiftRightArithmetic(output[c] + half, rowShift);
                }
            }

            // Eight columns of eight rows at a time, turned into eight rows of eight columns.
            for (int c = 0; c < w; c += 8)
            {
                Transpose(output.Slice(c, 8), buf.Slice((r0 * w) + c), w);
            }
        }

        // Columns, a lane each, then the residual added eight samples at a time.
        (lo, hi) = Bounds(column.Clamp);
        (rangeLo, rangeHi) = Bounds(column.Range);
        ref int b0 = ref MemoryMarshal.GetReference(buf);
        ref TSample d0 = ref MemoryMarshal.GetArrayDataReference(dst);
        Vector256<int> round = Vector256.Create(8), top = Vector256.Create(max);
        bool lrFlip = types.Row == 2, udFlip = types.Column == 2;
        Vector256<int> reverse = Vector256.Create(7, 6, 5, 4, 3, 2, 1, 0);
        for (int col = 0; col < w; col += 8)
        {
            for (int r = 0; r < h; r++)
            {
                Vector256<int> v = lrFlip
                    ? Vector256.Shuffle(Vector256.LoadUnsafe(ref b0, (nuint)((r * w) + w - 8 - col)), reverse)
                    : Vector256.LoadUnsafe(ref b0, (nuint)((r * w) + col));
                input[r] = Vector256.Min(Vector256.Max(v, lo), hi);
            }

            Transform<T>(types.Column, h, input[..h], output[..h], rangeLo, rangeHi);
            for (int r = 0; r < h; r++)
            {
                int at = offset + (r * stride) + col;
                Vector256<int> prediction = Vector256.WidenLower(Av1Samples.Load8(ref d0, at).AsUInt16().ToVector256()).AsInt32();
                Vector256<int> sum = prediction + Vector256.ShiftRightArithmetic(output[udFlip ? h - 1 - r : r] + round, 4);
                sum = Vector256.Min(Vector256.Max(sum, Vector256<int>.Zero), top);
                Av1Samples.Store8(ref d0, at, Av1Samples.Pack(sum));
            }
        }

        static (Vector256<int> Lo, Vector256<int> Hi) Bounds(int bits) => (Vector256.Create(-(1 << (bits - 1))), Vector256.Create((1 << (bits - 1)) - 1));
    }

    /// <summary>
    /// The 8-bit blocks four samples on a side (4x4 to 16x4 and 4x16), four rows and then four columns at a time. At 8 bits
    /// every input is clamped to 16 bits and every weight is 12, so even the ADST4's sums of three products fit 32 bits.
    /// </summary>
    private static void AddNarrow<TSample>(int[] coeffs, int w, int h, bool rect2, int rowShift, Types types, TSample[] dst, int offset, int stride)
        where TSample : unmanaged
    {
        Span<int> buf = stackalloc int[w * h];
        Span<Vector128<int>> input = stackalloc Vector128<int>[16];
        Span<Vector128<int>> output = stackalloc Vector128<int>[16];
        ref int c0 = ref MemoryMarshal.GetArrayDataReference(coeffs);
        Vector128<int> lo = Vector128.Create(-(1 << 15)), hi = Vector128.Create((1 << 15) - 1);
        for (int r0 = 0; r0 < h; r0 += 4)
        {
            Vector128<int> any = Vector128<int>.Zero;
            for (int c = 0; c < w; c++)
            {
                Vector128<int> v = Vector128.LoadUnsafe(ref c0, (nuint)((c * h) + r0));
                if (rect2)
                {
                    v = Vector128.ShiftRightArithmetic((v * NewInvSqrt2) + Vector128.Create(1 << (NewSqrt2Bits - 1)), NewSqrt2Bits);
                }

                input[c] = Vector128.Min(Vector128.Max(v, lo), hi);
                any |= v;
            }

            if (any == Vector128<int>.Zero)
            {
                buf.Slice(r0 * w, 4 * w).Clear();
                continue;
            }

            Transform(types.Row, w, input[..w], output[..w], lo, hi);
            for (int c = 0; c < w; c += 4)
            {
                Transpose(output.Slice(c, 4), buf.Slice((r0 * w) + c), w, rowShift);
            }
        }

        bool lrFlip = types.Row == 2, udFlip = types.Column == 2;
        ref int b0 = ref MemoryMarshal.GetReference(buf);
        ref TSample d0 = ref MemoryMarshal.GetArrayDataReference(dst);
        Vector128<int> round = Vector128.Create(8), top = Vector128.Create(255);
        for (int col = 0; col < w; col += 4)
        {
            for (int r = 0; r < h; r++)
            {
                Vector128<int> v = lrFlip
                    ? Vector128.Shuffle(Vector128.LoadUnsafe(ref b0, (nuint)((r * w) + w - 4 - col)), Vector128.Create(3, 2, 1, 0))
                    : Vector128.LoadUnsafe(ref b0, (nuint)((r * w) + col));
                input[r] = Vector128.Min(Vector128.Max(v, lo), hi);
            }

            Transform(types.Column, h, input[..h], output[..h], lo, hi);
            for (int r = 0; r < h; r++)
            {
                ref TSample at = ref Unsafe.Add(ref d0, offset + (r * stride) + col);
                Vector128<int> prediction = Vector128.WidenLower(Av1Samples.Load4(ref at, 0).AsUInt16()).AsInt32();
                Vector128<int> sum = prediction + Vector128.ShiftRightArithmetic(output[udFlip ? h - 1 - r : r] + round, 4);
                sum = Vector128.Min(Vector128.Max(sum, Vector128<int>.Zero), top);
                Vector128<ushort> samples = Av1Samples.Pack(sum);
                if (Av1Samples.IsByte<TSample>())
                {
                    Unsafe.WriteUnaligned(ref Unsafe.As<TSample, byte>(ref at), Av1Samples.PackBytes(samples).AsUInt32().ToScalar());
                }
                else
                {
                    Unsafe.WriteUnaligned(ref Unsafe.As<TSample, byte>(ref at), samples.AsUInt64().ToScalar());
                }
            }
        }
    }

    /// <summary>Writes the 4x4 block whose columns are <paramref name="columns"/> as four rows, <paramref name="stride"/> apart, rounded by <paramref name="shift"/>.</summary>
    private static void Transpose(ReadOnlySpan<Vector128<int>> columns, Span<int> rows, int stride, int shift)
    {
        Vector128<int> a0 = columns[0], a1 = columns[1], a2 = columns[2], a3 = columns[3];
        if (shift > 0)
        {
            Vector128<int> half = Vector128.Create(1 << (shift - 1));
            a0 = Vector128.ShiftRightArithmetic(a0 + half, shift);
            a1 = Vector128.ShiftRightArithmetic(a1 + half, shift);
            a2 = Vector128.ShiftRightArithmetic(a2 + half, shift);
            a3 = Vector128.ShiftRightArithmetic(a3 + half, shift);
        }

        ref int r = ref MemoryMarshal.GetReference(rows);
        if (Sse2.IsSupported)
        {
            Vector128<long> t0 = Sse2.UnpackLow(a0, a1).AsInt64(), t1 = Sse2.UnpackHigh(a0, a1).AsInt64();
            Vector128<long> t2 = Sse2.UnpackLow(a2, a3).AsInt64(), t3 = Sse2.UnpackHigh(a2, a3).AsInt64();
            Sse2.UnpackLow(t0, t2).AsInt32().StoreUnsafe(ref r, 0);
            Sse2.UnpackHigh(t0, t2).AsInt32().StoreUnsafe(ref r, (nuint)stride);
            Sse2.UnpackLow(t1, t3).AsInt32().StoreUnsafe(ref r, (nuint)(2 * stride));
            Sse2.UnpackHigh(t1, t3).AsInt32().StoreUnsafe(ref r, (nuint)(3 * stride));
            return;
        }

        for (int lane = 0; lane < 4; lane++)
        {
            Vector128.Create(a0.GetElement(lane), a1.GetElement(lane), a2.GetElement(lane), a3.GetElement(lane)).StoreUnsafe(ref r, (nuint)(lane * stride));
        }
    }

    private static void Transform(int type1d, int n, ReadOnlySpan<Vector128<int>> input, Span<Vector128<int>> output, Vector128<int> lo, Vector128<int> hi)
    {
        switch (type1d)
        {
            case 0:
                switch (n)
                {
                    case 4: Dct4(input, output, lo, hi); break;
                    case 8: Dct8(input, output, lo, hi); break;
                    default: Dct16(input, output, lo, hi); break;
                }

                break;
            case 3:
                // av1_iidentity4_c to 16_c: times sqrt(2), 2 and 2 sqrt(2).
                for (int i = 0; i < n; i++)
                {
                    output[i] = n switch
                    {
                        4 => Vector128.ShiftRightArithmetic((input[i] * NewSqrt2) + Vector128.Create(1 << (NewSqrt2Bits - 1)), NewSqrt2Bits),
                        8 => input[i] * 2,
                        _ => Vector128.ShiftRightArithmetic((input[i] * (NewSqrt2 * 2)) + Vector128.Create(1 << (NewSqrt2Bits - 1)), NewSqrt2Bits),
                    };
                }

                break;
            default:
                switch (n)
                {
                    case 4: Adst4(input, output); break;
                    case 8: Adst8(input, output, lo, hi); break;
                    default: Adst16(input, output, lo, hi); break;
                }

                break;
        }
    }

    /// <summary>av1_iadst4 on four lanes at 8 bits, its 64-bit sums in 32.</summary>
    private static void Adst4(ReadOnlySpan<Vector128<int>> input, Span<Vector128<int>> output)
    {
        ReadOnlySpan<int> sinpi = Sinpi;
        Vector128<int> x0 = input[0], x1 = input[1], x2 = input[2], x3 = input[3];
        Vector128<int> s0 = x0 * sinpi[1], s1 = x0 * sinpi[2], s2 = x1 * sinpi[3], s3 = x2 * sinpi[4];
        Vector128<int> s4 = x2 * sinpi[1], s5 = x3 * sinpi[2], s6 = x3 * sinpi[4], s7 = (x0 - x2) + x3;
        s0 += s3;
        s1 -= s4;
        s3 = s2;
        s2 = s7 * sinpi[3];
        s0 += s5;
        s1 -= s6;
        Vector128<int> round = Vector128.Create(1 << (CosBit - 1));
        output[0] = Vector128.ShiftRightArithmetic(s0 + s3 + round, CosBit);
        output[1] = Vector128.ShiftRightArithmetic(s1 + s3 + round, CosBit);
        output[2] = Vector128.ShiftRightArithmetic(s2 + round, CosBit);
        output[3] = Vector128.ShiftRightArithmetic(s0 + s1 - s3 + round, CosBit);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> HalfBtf(int w0, Vector128<int> in0, int w1, Vector128<int> in1, int bit) =>
        Vector128.ShiftRightArithmetic((in0 * w0) + (in1 * w1) + Vector128.Create(1 << (bit - 1)), bit);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Clamp(Vector128<int> value, Vector128<int> lo, Vector128<int> hi) => Vector128.Min(Vector128.Max(value, lo), hi);

    /// <summary>Writes the 8x8 block whose columns are <paramref name="columns"/> as eight rows, <paramref name="stride"/> apart.</summary>
    private static void Transpose(ReadOnlySpan<Vector256<int>> columns, Span<int> rows, int stride)
    {
        if (Avx2.IsSupported)
        {
            Vector256<int> t0 = Avx2.UnpackLow(columns[0], columns[1]), t1 = Avx2.UnpackHigh(columns[0], columns[1]);
            Vector256<int> t2 = Avx2.UnpackLow(columns[2], columns[3]), t3 = Avx2.UnpackHigh(columns[2], columns[3]);
            Vector256<int> t4 = Avx2.UnpackLow(columns[4], columns[5]), t5 = Avx2.UnpackHigh(columns[4], columns[5]);
            Vector256<int> t6 = Avx2.UnpackLow(columns[6], columns[7]), t7 = Avx2.UnpackHigh(columns[6], columns[7]);
            Vector256<int> s0 = Avx2.UnpackLow(t0.AsInt64(), t2.AsInt64()).AsInt32(), s1 = Avx2.UnpackHigh(t0.AsInt64(), t2.AsInt64()).AsInt32();
            Vector256<int> s2 = Avx2.UnpackLow(t1.AsInt64(), t3.AsInt64()).AsInt32(), s3 = Avx2.UnpackHigh(t1.AsInt64(), t3.AsInt64()).AsInt32();
            Vector256<int> s4 = Avx2.UnpackLow(t4.AsInt64(), t6.AsInt64()).AsInt32(), s5 = Avx2.UnpackHigh(t4.AsInt64(), t6.AsInt64()).AsInt32();
            Vector256<int> s6 = Avx2.UnpackLow(t5.AsInt64(), t7.AsInt64()).AsInt32(), s7 = Avx2.UnpackHigh(t5.AsInt64(), t7.AsInt64()).AsInt32();
            ref int r = ref MemoryMarshal.GetReference(rows);
            Avx2.Permute2x128(s0, s4, 0x20).StoreUnsafe(ref r, 0);
            Avx2.Permute2x128(s1, s5, 0x20).StoreUnsafe(ref r, (nuint)stride);
            Avx2.Permute2x128(s2, s6, 0x20).StoreUnsafe(ref r, (nuint)(2 * stride));
            Avx2.Permute2x128(s3, s7, 0x20).StoreUnsafe(ref r, (nuint)(3 * stride));
            Avx2.Permute2x128(s0, s4, 0x31).StoreUnsafe(ref r, (nuint)(4 * stride));
            Avx2.Permute2x128(s1, s5, 0x31).StoreUnsafe(ref r, (nuint)(5 * stride));
            Avx2.Permute2x128(s2, s6, 0x31).StoreUnsafe(ref r, (nuint)(6 * stride));
            Avx2.Permute2x128(s3, s7, 0x31).StoreUnsafe(ref r, (nuint)(7 * stride));
            return;
        }

        for (int c = 0; c < 8; c++)
        {
            for (int lane = 0; lane < 8; lane++)
            {
                rows[(lane * stride) + c] = columns[c].GetElement(lane);
            }
        }
    }

    private static void Transform<T>(int type1d, int n, ReadOnlySpan<Vector256<int>> input, Span<Vector256<int>> output, Vector256<int> lo, Vector256<int> hi)
        where T : IButterfly
    {
        switch (type1d)
        {
            case 0:
                switch (n)
                {
                    case 8: Dct8<T>(input, output, lo, hi); break;
                    case 16: Dct16<T>(input, output, lo, hi); break;
                    case 32: Dct32<T>(input, output, lo, hi); break;
                    default: Dct64<T>(input, output, lo, hi); break;
                }

                break;
            case 3:
                // av1_iidentity8_c to 32_c: times 2, 2 sqrt(2) and 4.
                for (int i = 0; i < n; i++)
                {
                    output[i] = n switch
                    {
                        8 => input[i] * 2,
                        16 => T.Scale(input[i], NewSqrt2 * 2, NewSqrt2Bits),
                        _ => input[i] * 4,
                    };
                }

                break;
            default:
                if (n == 8)
                {
                    Adst8<T>(input, output, lo, hi);
                }
                else
                {
                    Adst16<T>(input, output, lo, hi);
                }

                break;
        }
    }

    /// <summary>
    /// The butterfly at 8 and 10 bits: its inputs are clamped to the stage's range (18 bits at most) and its weights are 12
    /// bits, so the products and their sum fit 32 bits.
    /// </summary>
    internal readonly struct Narrow : IButterfly
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<int> HalfBtf(int w0, Vector256<int> in0, int w1, Vector256<int> in1, int bit) =>
            Vector256.ShiftRightArithmetic((in0 * w0) + (in1 * w1) + Vector256.Create(1 << (bit - 1)), bit);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<int> Scale(Vector256<int> x, int w, int bit) =>
            Vector256.ShiftRightArithmetic((x * w) + Vector256.Create(1 << (bit - 1)), bit);
    }

    /// <summary>
    /// The butterfly at 12 bits, whose 20-bit range lets the sum of two products pass 32 bits: each product split at the shift,
    /// the high parts added and the low parts' carry, which is floor((p0 + p1 + round) / 2^bit) without overflow.
    /// </summary>
    internal readonly struct Wide : IButterfly
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<int> HalfBtf(int w0, Vector256<int> in0, int w1, Vector256<int> in1, int bit)
        {
            Vector256<int> p0 = in0 * w0, p1 = in1 * w1, low = Vector256.Create((1 << bit) - 1);
            Vector256<int> carry = Vector256.ShiftRightArithmetic((p0 & low) + (p1 & low) + Vector256.Create(1 << (bit - 1)), bit);
            return Vector256.ShiftRightArithmetic(p0, bit) + Vector256.ShiftRightArithmetic(p1, bit) + carry;
        }

        /// <summary>The product split as x is: w times its high part, and the rounded product of its low part shifted.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<int> Scale(Vector256<int> x, int w, int bit) =>
            (Vector256.ShiftRightArithmetic(x, bit) * w) + Vector256.ShiftRightArithmetic(((x & Vector256.Create((1 << bit) - 1)) * w) + Vector256.Create(1 << (bit - 1)), bit);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Clamp(Vector256<int> value, Vector256<int> lo, Vector256<int> hi) => Vector256.Min(Vector256.Max(value, lo), hi);

    private static void Transform(int type1d, int n, ReadOnlySpan<int> input, Span<int> output, int range)
    {
        switch (type1d)
        {
            case 0:
                switch (n)
                {
                    case 4: Dct4(input, output, range); break;
                    case 8: Dct8(input, output, range); break;
                    case 16: Dct16(input, output, range); break;
                    case 32: Dct32(input, output, range); break;
                    default: Dct64(input, output, range); break;
                }

                break;
            case 3:
                switch (n)
                {
                    case 4: Identity4(input, output, range); break;
                    case 8: Identity8(input, output, range); break;
                    case 16: Identity16(input, output, range); break;
                    default: Identity32(input, output, range); break;
                }

                break;
            default:
                switch (n)
                {
                    case 4: Adst4(input, output, range); break;
                    case 8: Adst8(input, output, range); break;
                    default: Adst16(input, output, range); break;
                }

                break;
        }
    }

    private static int HalfBtf(int w0, int in0, int w1, int in1, int bit)
    {
        long result = (long)unchecked(w0 * in0) + unchecked(w1 * in1);
        return (int)((result + (1L << (bit - 1))) >> bit);
    }

    private static int RoundShift(long value, int bit) => (int)((value + (1L << (bit - 1))) >> bit);

    private static int Clamp(int value, int bit) => Math.Clamp(value, -(1 << (bit - 1)), (1 << (bit - 1)) - 1);

    /// <summary>av1_highbd_iwht4x4_16_add.</summary>
    private static void Wht4x4<TSample>(int[] input, TSample[] dst, int offset, int stride, int bitDepth)
        where TSample : unmanaged
    {
        Span<int> output = stackalloc int[16];
        int max = (1 << bitDepth) - 1;
        for (int i = 0; i < 4; i++)
        {
            int a1 = input[i] >> 2, c1 = input[4 + i] >> 2, d1 = input[8 + i] >> 2, b1 = input[12 + i] >> 2;
            a1 += c1;
            d1 -= b1;
            int e1 = (a1 - d1) >> 1;
            b1 = e1 - b1;
            c1 = e1 - c1;
            a1 -= b1;
            d1 += c1;
            output[i] = a1;
            output[4 + i] = b1;
            output[8 + i] = c1;
            output[12 + i] = d1;
        }

        for (int i = 0; i < 4; i++)
        {
            int a1 = output[(4 * i) + 0], c1 = output[(4 * i) + 1], d1 = output[(4 * i) + 2], b1 = output[(4 * i) + 3];
            a1 += c1;
            d1 -= b1;
            int e1 = (a1 - d1) >> 1;
            b1 = e1 - b1;
            c1 = e1 - c1;
            a1 -= b1;
            d1 += c1;
            int at = offset + i;
            dst[at] = Av1Samples.From<TSample>(Math.Clamp(Av1Samples.Get(dst[at]) + a1, 0, max));
            dst[at + stride] = Av1Samples.From<TSample>(Math.Clamp(Av1Samples.Get(dst[at + stride]) + b1, 0, max));
            dst[at + (2 * stride)] = Av1Samples.From<TSample>(Math.Clamp(Av1Samples.Get(dst[at + (2 * stride)]) + c1, 0, max));
            dst[at + (3 * stride)] = Av1Samples.From<TSample>(Math.Clamp(Av1Samples.Get(dst[at + (3 * stride)]) + d1, 0, max));
        }
    }

    /// <summary>av1_highbd_iwht4x4_1_add: only the DC coefficient.</summary>
    private static void Wht4x4Dc<TSample>(int[] input, TSample[] dst, int offset, int stride, int bitDepth)
        where TSample : unmanaged
    {
        Span<int> tmp = stackalloc int[4];
        int max = (1 << bitDepth) - 1;
        int a1 = input[0] >> 2;
        int e1 = a1 >> 1;
        a1 -= e1;
        tmp[0] = a1;
        tmp[1] = tmp[2] = tmp[3] = e1;
        for (int i = 0; i < 4; i++)
        {
            e1 = tmp[i] >> 1;
            a1 = tmp[i] - e1;
            int at = offset + i;
            dst[at] = Av1Samples.From<TSample>(Math.Clamp(Av1Samples.Get(dst[at]) + a1, 0, max));
            dst[at + stride] = Av1Samples.From<TSample>(Math.Clamp(Av1Samples.Get(dst[at + stride]) + e1, 0, max));
            dst[at + (2 * stride)] = Av1Samples.From<TSample>(Math.Clamp(Av1Samples.Get(dst[at + (2 * stride)]) + e1, 0, max));
            dst[at + (3 * stride)] = Av1Samples.From<TSample>(Math.Clamp(Av1Samples.Get(dst[at + (3 * stride)]) + e1, 0, max));
        }
    }
}
