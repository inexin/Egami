using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Video.Codecs.Vp9;

/// <summary>Coefficient tokens and the reconstruction of each transform block (vp9_detokenize.c and vp9_decodeframe.c).</summary>
internal sealed partial class Vp9Decoder<T>
{
    internal sealed partial class Tile
    {
        private static ReadOnlySpan<byte> Cat1Prob => [159];

        private static ReadOnlySpan<byte> Cat2Prob => [165, 145];

        private static ReadOnlySpan<byte> Cat3Prob => [173, 148, 140];

        private static ReadOnlySpan<byte> Cat4Prob => [176, 155, 140, 135];

        private static ReadOnlySpan<byte> Cat5Prob => [180, 157, 141, 134, 130];

        private void PredictAndReconstructIntra(ref Vp9ModeInfo mi, int plane, int row, int col, int txSize)
        {
            Vp9Decoder<T> d = _d;
            Vp9Frame<T> frame = d._current!;
            int mode = plane == 0 ? mi.Mode : mi.UVMode;
            if (mi.SbType < Vp9Tables.Block8x8 && plane == 0)
            {
                mode = mi.Bmi[(row << 1) + col].Mode;
            }

            int stride = frame.Strides[plane];
            int x0 = ((_miCol * 8) >> _ssX[plane]) + (4 * col);
            int y0 = ((_miRow * 8) >> _ssY[plane]) + (4 * row);
            int dst = (y0 * stride) + x0;
            Span<T> pixels = frame.Plane(plane);
            bool haveTop = row != 0 || _aboveMi >= 0;
            bool haveLeft = col != 0 || _leftMi >= 0;
            bool haveRight = col + (1 << txSize) < (1 << _n4Wl[plane]);
            Vp9Intra<T>.Predict(pixels, dst, stride, mode, txSize, haveTop, haveLeft, haveRight, _toRightEdge < 0, _toBottomEdge < 0,
                x0, y0, frame.AlignedWidth(plane), frame.AlignedHeight(plane), d._bitDepth);

            if (mi.Skip == 0)
            {
                int txType = plane != 0 || d._lossless ? Vp9Tables.DctDct : Vp9Tables.IntraModeToTxTypeLookup[mode];
                Vp9Scan scan = plane != 0 || d._lossless ? Vp9Scan.Default(txSize) : Vp9Scan.For(txSize, txType);
                int eob = DecodeBlockTokens(plane, scan, col, row, txSize, mi.SegmentId, ref mi);
                if (eob > 0)
                {
                    InverseTransform(pixels.Slice(dst), stride, txSize, txType, eob, scan);
                }
            }
        }

        private int ReconstructInter(ref Vp9ModeInfo mi, int plane, int row, int col, int txSize)
        {
            Vp9Frame<T> frame = _d._current!;
            Vp9Scan scan = Vp9Scan.Default(txSize);
            int eob = DecodeBlockTokens(plane, scan, col, row, txSize, mi.SegmentId, ref mi);
            if (eob > 0)
            {
                int stride = frame.Strides[plane];
                int x0 = ((_miCol * 8) >> _ssX[plane]) + (4 * col);
                int y0 = ((_miRow * 8) >> _ssY[plane]) + (4 * row);
                InverseTransform(frame.Plane(plane).Slice((y0 * stride) + x0), stride, txSize, Vp9Tables.DctDct, eob, scan);
            }

            return eob;
        }

        private void InverseTransform(Span<T> dst, int stride, int txSize, int txType, int eob, Vp9Scan scan)
        {
            int[] dq = _dq;
            int bitDepth = _d._bitDepth;
            if (_d._lossless)
            {
                Vp9Transform.Iwht4x4Add(dq, dst, stride, eob, bitDepth);
            }
            else
            {
                Vp9Transform.InverseAdd(dq, dst, stride, txSize, txType, eob, bitDepth);
            }

            // Clear the coefficients the block used, for the next block.
            ReadOnlySpan<short> order = scan.Order;
            for (int i = 0; i < eob; i++)
            {
                dq[order[i]] = 0;
            }
        }

        private int DecodeBlockTokens(int plane, Vp9Scan scan, int x, int y, int txSize, int segmentId, ref Vp9ModeInfo mi)
        {
            Vp9Decoder<T> d = _d;
            short[] dequant = plane == 0 ? d._yDequant : d._uvDequant;
            int dqDc = dequant[segmentId * 2], dqAc = dequant[(segmentId * 2) + 1];
            byte[] above = d._aboveContext[plane];
            byte[] left = _leftContext[plane];
            int a = AboveContextIndex(plane) + x, l = LeftContextIndex(plane) + y;
            int n = 1 << txSize;
            int ctxA = 0, ctxL = 0;
            for (int i = 0; i < n; i++)
            {
                ctxA |= above[a + i];
                ctxL |= left[l + i];
            }

            int ctx = (ctxA != 0 ? 1 : 0) + (ctxL != 0 ? 1 : 0);
            int eob = DecodeCoefs(plane > 0 ? 1 : 0, txSize, dqDc, dqAc, ctx, scan, mi.IsInter ? 1 : 0);
            byte has = (byte)(eob > 0 ? 1 : 0);

            // The contexts past the frame's edge are cleared (libvpx's shifted multi-byte store).
            int aboveCount = n, leftCount = n;
            if (_maxBlocksWide != 0 && n + x > _maxBlocksWide)
            {
                aboveCount = Math.Max(0, _maxBlocksWide - x);
            }

            if (_maxBlocksHigh != 0 && n + y > _maxBlocksHigh)
            {
                leftCount = Math.Max(0, _maxBlocksHigh - y);
            }

            for (int i = 0; i < n; i++)
            {
                above[a + i] = i < aboveCount ? has : (byte)0;
                left[l + i] = i < leftCount ? has : (byte)0;
            }

            return eob;
        }

        private int DecodeCoefs(int type, int txSize, int dqDc, int dqAc, int ctx, Vp9Scan scan, int reference)
        {
            int maxEob = 16 << (txSize << 1);
            int countBase = ((((txSize * 2) + type) * 2) + reference) * 36;
            bool counts = CountsEnabled;
            int dqShift = txSize == Vp9Tables.Tx32x32 ? 1 : 0;

            // The hot loop indexes through references: every index is in range by construction (scan positions below 1024, the
            // context at most 5, so a probability within the block's 6 bands of 6 contexts of 3), which keeps the reader's state
            // in registers instead of array lengths.
            ref byte probs = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_d._fc.P), Vp9FrameContext.CoefOffset(txSize, type, reference));
            ref int coefCounts = ref MemoryMarshal.GetArrayDataReference(Counts.Coef);
            ref int eobBranch = ref MemoryMarshal.GetArrayDataReference(Counts.EobBranch);
            ref byte tokenCache = ref MemoryMarshal.GetArrayDataReference(_tokenCache);
            ref byte bandTranslate = ref MemoryMarshal.GetReference(txSize == Vp9Tables.Tx4x4 ? Vp9Tables.CoefbandTrans4x4 : Vp9Tables.CoefbandTrans8x8Plus);
            ref short order = ref MemoryMarshal.GetReference(scan.Order);
            ref short neighbors = ref MemoryMarshal.GetReference(scan.Neighbors);
            ref int dq = ref MemoryMarshal.GetArrayDataReference(_dq);
            int dqv = dqDc;
            int c = 0;

            // A copy of the reader, which the JIT can keep in registers; written back on the way out.
            Vp9BoolDecoder r = _r;
            while (c < maxEob)
            {
                int band = Unsafe.Add(ref bandTranslate, c);
                int p = (band * 18) + (ctx * 3);
                int cb = countBase + (band * 6) + ctx;
                if (counts)
                {
                    Unsafe.Add(ref eobBranch, cb)++;
                }

                if (r.Read(Unsafe.Add(ref probs, p)) == 0)
                {
                    if (counts)
                    {
                        Unsafe.Add(ref coefCounts, (cb * 4) + 3)++;
                    }

                    break;
                }

                while (r.Read(Unsafe.Add(ref probs, p + 1)) == 0)
                {
                    if (counts)
                    {
                        Unsafe.Add(ref coefCounts, cb * 4)++;
                    }

                    dqv = dqAc;
                    Unsafe.Add(ref tokenCache, Unsafe.Add(ref order, c)) = 0;
                    c++;
                    if (c >= maxEob)
                    {
                        _r = r;
                        return c;
                    }

                    ctx = (1 + Unsafe.Add(ref tokenCache, Unsafe.Add(ref neighbors, c * 2)) + Unsafe.Add(ref tokenCache, Unsafe.Add(ref neighbors, (c * 2) + 1))) >> 1;
                    band = Unsafe.Add(ref bandTranslate, c);
                    p = (band * 18) + (ctx * 3);
                    cb = countBase + (band * 6) + ctx;
                }

                int v;
                int position = Unsafe.Add(ref order, c);
                if (r.Read(Unsafe.Add(ref probs, p + 2)) != 0)
                {
                    if (counts)
                    {
                        Unsafe.Add(ref coefCounts, (cb * 4) + 2)++;
                    }

                    // Two and above are rarer: decoded out of line, through the reader field, so this loop keeps its registers.
                    _r = r;
                    v = ReadLargeToken(Unsafe.Add(ref probs, p + 2), dqv, dqShift, out byte token);
                    r = _r;
                    Unsafe.Add(ref tokenCache, position) = token;
                }
                else
                {
                    if (counts)
                    {
                        Unsafe.Add(ref coefCounts, (cb * 4) + 1)++;
                    }

                    Unsafe.Add(ref tokenCache, position) = 1;
                    v = dqv >> dqShift;
                }

                Unsafe.Add(ref dq, position) = r.Read(128) != 0 ? -v : v;
                c++;
                if (c < maxEob)
                {
                    ctx = (1 + Unsafe.Add(ref tokenCache, Unsafe.Add(ref neighbors, c * 2)) + Unsafe.Add(ref tokenCache, Unsafe.Add(ref neighbors, (c * 2) + 1))) >> 1;
                }

                dqv = dqAc;
            }

            _r = r;
            return c;
        }

        /// <summary>A token of two or more (its Pareto tail and extra bits), dequantized; <paramref name="token"/> is its token cache class.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private int ReadLargeToken(byte probability, int dqv, int dqShift, out byte token)
        {
            ReadOnlySpan<byte> pareto = Vp9Tables.Pareto8Full.AsSpan((probability - 1) * 8, 8);
            ref Vp9BoolDecoder r = ref _r;
            if (r.Read(pareto[0]) != 0)
            {
                int val;
                if (r.Read(pareto[3]) != 0)
                {
                    token = 5;
                    if (r.Read(pareto[5]) != 0)
                    {
                        int bitDepth = _d._bitDepth;
                        ReadOnlySpan<byte> cat6Prob = bitDepth == 12 ? Vp9Tables.Cat6ProbHigh12 : bitDepth == 10 ? Vp9Tables.Cat6ProbHigh12.AsSpan(2) : Vp9Tables.Cat6Prob;
                        int cat6Bits = bitDepth == 12 ? 18 : bitDepth == 10 ? 16 : 14;
                        val = r.Read(pareto[7]) != 0 ? 67 + ReadCoeff(ref r, cat6Prob, cat6Bits) : 35 + ReadCoeff(ref r, Cat5Prob, 5);
                    }
                    else if (r.Read(pareto[6]) != 0)
                    {
                        val = 19 + ReadCoeff(ref r, Cat4Prob, 4);
                    }
                    else
                    {
                        val = 11 + ReadCoeff(ref r, Cat3Prob, 3);
                    }
                }
                else
                {
                    token = 4;
                    val = r.Read(pareto[4]) != 0 ? 7 + ReadCoeff(ref r, Cat2Prob, 2) : 5 + ReadCoeff(ref r, Cat1Prob, 1);
                }

                return (int)(((long)val * dqv) >> dqShift);
            }

            if (r.Read(pareto[1]) != 0)
            {
                token = 3;
                return ((3 + r.Read(pareto[2])) * dqv) >> dqShift;
            }

            token = 2;
            return (2 * dqv) >> dqShift;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ReadCoeff(ref Vp9BoolDecoder r, ReadOnlySpan<byte> probs, int n)
        {
            int val = 0;
            for (int i = 0; i < n; i++)
            {
                val = (val << 1) | r.Read(probs[i]);
            }

            return val;
        }
    }
}
