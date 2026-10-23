using System.Runtime.CompilerServices;

using Egami.Nal;

namespace Egami.Video.Codecs.H264;

/// <summary>The residual (7.3.5.3): coefficient levels read with CAVLC or CABAC, then scaled (8.5.9, 8.5.12.1).</summary>
internal sealed partial class H264SliceDecoder
{
    private static readonly byte[] IdentityScan = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    private readonly int[] _levels = new int[64];

    private void ReadResidual()
    {
        bool intra = H264MbState.IsIntra(_kind);
        int qpY = _qp + _qpBdOffsetY;
        int[] scale4 = _pps.Dequant.Scale4x4[intra ? 0 : 3];
        int left = LeftMb, top = TopMb;
        Array.Clear(_coef);
        if (_kind == H264MbKind.I16x16)
        {
            Array.Clear(_dcY);
            int count = _cabac
                ? ResidualCabac(0, CbfInc(DcCond(left, 1), DcCond(top, 1)), 16, _levels)
                : ResidualCavlc(LumaNc(0, 0), 16);
            if (count > 0)
            {
                _mb.CbfDc[_mbAddr] |= 1;
                for (int k = 0; k < 16; k++)
                {
                    _dcY[H264ScalingLists.Zigzag4x4[k]] = _levels[k];
                }
            }

            LumaDcTransform(qpY, scale4);
        }

        for (int i8 = 0; i8 < 4; i8++)
        {
            if ((_cbp & (1 << i8)) == 0)
            {
                continue;
            }

            if (_t8x8)
            {
                Read8x8(i8, qpY, intra);
                continue;
            }

            for (int i4 = 0; i4 < 4; i4++)
            {
                int blk = (i8 * 4) + i4;
                int xy = H264Tables.BlockXY[blk];
                int bx = xy & 3, by = xy >> 2;
                bool ac = _kind == H264MbKind.I16x16;
                int max = ac ? 15 : 16;
                int count = _cabac
                    ? ResidualCabac(ac ? 1 : 2, CbfInc(LumaCbfCond(bx - 1, by), LumaCbfCond(bx, by - 1)), max, _levels)
                    : ResidualCavlc(LumaNc(bx, by), max);
                int b = Block(bx, by);
                _mb.NzLuma[b] = (byte)count;
                _mb.Nonzero[b] = (byte)(count > 0 ? 1 : 0);
                if (count > 0)
                {
                    _lumaCoded |= 1 << blk;
                    Span<int> dest = _coef.AsSpan(blk * 16, 16);
                    int first = ac ? 1 : 0;
                    for (int k = 0; k < max; k++)
                    {
                        int level = _levels[k];
                        if (level != 0)
                        {
                            int pos = H264ScalingLists.Zigzag4x4[k + first];
                            dest[pos] = Scale4x4(level, qpY, scale4, pos);
                        }
                    }
                }
            }
        }

        if (_chromaArrayType is 1 or 2)
        {
            ReadChromaResidual(intra, left, top);
        }
    }

    private void Read8x8(int i8, int qpY, bool intra)
    {
        int[] scale8 = _pps.Dequant.Scale8x8[intra ? 0 : 1];
        Span<int> dest = _coef.AsSpan(i8 * 64, 64);
        int bx0 = (i8 & 1) * 2, by0 = (i8 >> 1) * 2;
        int total = 0;
        if (_cabac)
        {
            total = ResidualCabac(5, -1, 64, _levels);
            for (int k = 0; k < 64; k++)
            {
                if (_levels[k] != 0)
                {
                    int pos = H264ScalingLists.Zigzag8x8[k];
                    dest[pos] = Scale8x8(_levels[k], qpY, scale8, pos);
                }
            }

            for (int i4 = 0; i4 < 4; i4++)
            {
                _mb.NzLuma[Block(bx0 + (i4 & 1), by0 + (i4 >> 1))] = (byte)total;
            }
        }
        else
        {
            // CAVLC codes the 8x8 block as four interleaved 4x4 lists (7.3.5.3.2).
            for (int i4 = 0; i4 < 4; i4++)
            {
                int bx = bx0 + (i4 & 1), by = by0 + (i4 >> 1);
                int count = ResidualCavlc(LumaNc(bx, by), 16);
                _mb.NzLuma[Block(bx, by)] = (byte)count;
                total += count;
                for (int k = 0; k < 16; k++)
                {
                    if (_levels[k] != 0)
                    {
                        int pos = H264ScalingLists.Zigzag8x8[(4 * k) + i4];
                        dest[pos] = Scale8x8(_levels[k], qpY, scale8, pos);
                    }
                }
            }
        }

        byte nonzero = (byte)(total > 0 ? 1 : 0);
        for (int i4 = 0; i4 < 4; i4++)
        {
            _mb.Nonzero[Block(bx0 + (i4 & 1), by0 + (i4 >> 1))] = nonzero;
        }

        if (total > 0)
        {
            _lumaCoded |= 15 << (i8 * 4);
        }
    }

    private void ReadChromaResidual(bool intra, int left, int top)
    {
        int numC8x8 = _chromaArrayType == 1 ? 1 : 2;
        int dcCount = 4 * numC8x8;
        Array.Clear(_dcC);
        Array.Clear(_coefC);
        if ((_cbp & 0x30) != 0)
        {
            for (int c = 0; c < 2; c++)
            {
                int count = _cabac
                    ? ResidualCabac(3, CbfInc(DcCond(left, 2 << c), DcCond(top, 2 << c)), dcCount, _levels)
                    : ResidualCavlc(_chromaArrayType == 1 ? -1 : -2, dcCount);
                if (count > 0)
                {
                    _mb.CbfDc[_mbAddr] |= (byte)(2 << c);
                    _chromaDcCoded |= 1 << c;
                    for (int k = 0; k < dcCount; k++)
                    {
                        _dcC[(c * 8) + (_chromaArrayType == 1 ? k : H264Tables.ChromaDc422Scan[k])] = _levels[k];
                    }
                }
            }
        }

        if ((_cbp & 0x20) != 0)
        {
            for (int c = 0; c < 2; c++)
            {
                int[] scale = _pps.Dequant.Scale4x4[(intra ? 1 : 4) + c];
                int qpc = ChromaQp(_qp, c == 0 ? _pps.ChromaQpIndexOffset : _pps.SecondChromaQpIndexOffset) + _qpBdOffsetC;
                byte[] nz = c == 0 ? _mb.NzCb : _mb.NzCr;
                for (int blk = 0; blk < dcCount; blk++)
                {
                    int bx = blk & 1, by = blk >> 1;
                    int count = _cabac
                        ? ResidualCabac(4, CbfInc(ChromaCbfCond(nz, bx - 1, by), ChromaCbfCond(nz, bx, by - 1)), 15, _levels)
                        : ResidualCavlc(ChromaNc(nz, bx, by), 15);
                    nz[ChromaBlock(bx, by)] = (byte)count;
                    if (count > 0)
                    {
                        _chromaAcCoded |= 1 << ((c * 8) + blk);
                        Span<int> dest = _coefC.AsSpan(((c * 8) + blk) * 16, 16);
                        for (int k = 0; k < 15; k++)
                        {
                            if (_levels[k] != 0)
                            {
                                int pos = H264ScalingLists.Zigzag4x4[k + 1];
                                dest[pos] = Scale4x4(_levels[k], qpc, scale, pos);
                            }
                        }
                    }
                }
            }
        }

        if (_chromaDcCoded != 0)
        {
            for (int c = 0; c < 2; c++)
            {
                if ((_chromaDcCoded & (1 << c)) != 0)
                {
                    int[] scale = _pps.Dequant.Scale4x4[(intra ? 1 : 4) + c];
                    int qpc = ChromaQp(_qp, c == 0 ? _pps.ChromaQpIndexOffset : _pps.SecondChromaQpIndexOffset) + _qpBdOffsetC;
                    ChromaDcTransform(c, qpc, scale);
                }
            }
        }
    }

    private static int Scale4x4(int level, int qp, int[] scale, int pos)
    {
        int ls = scale[((qp % 6) * 16) + pos];
        int shift = qp / 6;
        return shift >= 4 ? (level * ls) << (shift - 4) : ((level * ls) + (1 << (3 - shift))) >> (4 - shift);
    }

    private static int Scale8x8(int level, int qp, int[] scale, int pos)
    {
        int ls = scale[((qp % 6) * 64) + pos];
        int shift = qp / 6;
        return shift >= 6 ? (level * ls) << (shift - 6) : ((level * ls) + (1 << (5 - shift))) >> (6 - shift);
    }

    /// <summary>The Intra16x16 DC: inverse Hadamard and scaling (8.5.10), into each block's coefficient 0.</summary>
    private void LumaDcTransform(int qp, int[] scale)
    {
        if ((_mb.CbfDc[_mbAddr] & 1) == 0)
        {
            return;
        }

        Span<int> c = _dcY;
        Span<int> t = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int a = c[(i * 4) + 0], b = c[(i * 4) + 1], d = c[(i * 4) + 2], e = c[(i * 4) + 3];
            t[(i * 4) + 0] = a + b + d + e;
            t[(i * 4) + 1] = a + b - d - e;
            t[(i * 4) + 2] = a - b - d + e;
            t[(i * 4) + 3] = a - b + d - e;
        }

        int ls = scale[(qp % 6) * 16];
        int shift = qp / 6;
        for (int j = 0; j < 4; j++)
        {
            int a = t[j], b = t[4 + j], d = t[8 + j], e = t[12 + j];
            Span<int> f = [a + b + d + e, a + b - d - e, a - b - d + e, a - b + d - e];
            for (int i = 0; i < 4; i++)
            {
                int dc = shift >= 6 ? (f[i] * ls) << (shift - 6) : ((f[i] * ls) + (1 << (5 - shift))) >> (6 - shift);

                // dcY(i, j) is the DC of the block in row i, column j.
                int blk = BlockIndex(j, i);
                _coef[blk * 16] = dc;
                if (dc != 0)
                {
                    _lumaCoded |= 1 << blk;
                }
            }
        }
    }

    /// <summary>The chroma DC: inverse transform and scaling (8.5.11.2) into each chroma block's coefficient 0.</summary>
    private void ChromaDcTransform(int c, int qp, int[] scale)
    {
        Span<int> dc = _dcC.AsSpan(c * 8, 8);
        if (_chromaArrayType == 1)
        {
            int a = dc[0], b = dc[1], d = dc[2], e = dc[3];
            Span<int> f = [a + b + d + e, a - b + d - e, a + b - d - e, a - b - d + e];
            int ls = scale[(qp % 6) * 16];
            for (int i = 0; i < 4; i++)
            {
                int value = ((f[i] * ls) << (qp / 6)) >> 5;
                _coefC[((c * 8) + i) * 16] = value;
                if (value != 0)
                {
                    _chromaAcCoded |= 1 << ((c * 8) + i);
                }
            }

            return;
        }

        // 4:2:2: c is 4 rows by 2 columns; f = A c B with the 4-point and 2-point Hadamards.
        Span<int> t = stackalloc int[8];
        for (int j = 0; j < 2; j++)
        {
            int a = dc[j], b = dc[2 + j], d = dc[4 + j], e = dc[6 + j];
            t[j] = a + b + d + e;
            t[2 + j] = a + b - d - e;
            t[4 + j] = a - b - d + e;
            t[6 + j] = a - b + d - e;
        }

        int qpDc = qp + 3;
        int lsDc = scale[(qpDc % 6) * 16];
        int shift = qpDc / 6;
        for (int i = 0; i < 4; i++)
        {
            int p = t[i * 2], q = t[(i * 2) + 1];
            Span<int> f = [p + q, p - q];
            for (int j = 0; j < 2; j++)
            {
                int value = shift >= 6 ? (f[j] * lsDc) << (shift - 6) : ((f[j] * lsDc) + (1 << (5 - shift))) >> (6 - shift);
                int blk = (i * 2) + j;
                _coefC[((c * 8) + blk) * 16] = value;
                if (value != 0)
                {
                    _chromaAcCoded |= 1 << ((c * 8) + blk);
                }
            }
        }
    }

    /// <summary>Gets luma4x4BlkIdx of the 4x4 block at (bx, by) in the macroblock.</summary>
    private static int BlockIndex(int bx, int by) => ((by >> 1) * 8) + ((bx >> 1) * 4) + ((by & 1) * 2) + (bx & 1);

    private int ChromaBlock(int bx, int by)
    {
        int rows = _chromaArrayType == 1 ? 2 : 4;
        return (((_mbY * rows) + by) * _mb.ChromaW4) + (_mbX * 2) + bx;
    }

    private static int CbfInc(int condA, int condB) => condA + (2 * condB);

    /// <summary>condTermFlagN of a DC block's coded_block_flag (bit 1 luma, 2 Cb, 4 Cr), 9.3.3.1.1.9.</summary>
    private int DcCond(int addr, int bit)
    {
        if (!Available(addr))
        {
            return H264MbState.IsIntra(_kind) ? 1 : 0;
        }

        H264MbKind kind = _mb.Kind[addr];
        return kind == H264MbKind.IPcm ? 1 : (_mb.CbfDc[addr] & bit) != 0 ? 1 : 0;
    }

    private int LumaCbfCond(int bx, int by)
    {
        int addr = bx < 0 ? LeftMb : by < 0 ? TopMb : _mbAddr;
        if (addr != _mbAddr && !Available(addr))
        {
            return H264MbState.IsIntra(_kind) ? 1 : 0;
        }

        return _mb.NzLuma[Block(bx, by)] != 0 ? 1 : 0;
    }

    private int ChromaCbfCond(byte[] nz, int bx, int by)
    {
        int addr = bx < 0 ? LeftMb : by < 0 ? TopMb : _mbAddr;
        if (addr != _mbAddr && !Available(addr))
        {
            return H264MbState.IsIntra(_kind) ? 1 : 0;
        }

        return nz[ChromaBlock(bx, by)] != 0 ? 1 : 0;
    }

    /// <summary>nC of a luma block (9.2.1) from the blocks left of and above it.</summary>
    private int LumaNc(int bx, int by)
    {
        int addrA = bx > 0 ? _mbAddr : LeftMb, addrB = by > 0 ? _mbAddr : TopMb;
        bool availableA = addrA == _mbAddr || Available(addrA), availableB = addrB == _mbAddr || Available(addrB);
        int nA = availableA ? _mb.NzLuma[Block(bx - 1, by)] : 0, nB = availableB ? _mb.NzLuma[Block(bx, by - 1)] : 0;
        return availableA && availableB ? (nA + nB + 1) >> 1 : nA + nB;
    }

    private int ChromaNc(byte[] nz, int bx, int by)
    {
        int addrA = bx > 0 ? _mbAddr : LeftMb, addrB = by > 0 ? _mbAddr : TopMb;
        bool availableA = addrA == _mbAddr || Available(addrA), availableB = addrB == _mbAddr || Available(addrB);
        int nA = availableA ? nz[ChromaBlock(bx - 1, by)] : 0, nB = availableB ? nz[ChromaBlock(bx, by - 1)] : 0;
        return availableA && availableB ? (nA + nB + 1) >> 1 : nA + nB;
    }

    /// <summary>Reads residual_block_cavlc (9.2) into <see cref="_levels"/> in scan order; returns TotalCoeff.</summary>
    private int ResidualCavlc(int nC, int maxNumCoeff)
    {
        Span<int> levels = _levels.AsSpan(0, maxNumCoeff);
        levels.Clear();
        int token;
        if (nC >= 8)
        {
            int code = _bits.Read(6);
            token = code == 3 ? 0 : ((code >> 2) + 1) * 4 + (code & 3);
            if ((token & 3) > (token >> 2))
            {
                throw H264Errors.Corrupt("A coeff_token has more trailing ones than coefficients.");
            }
        }
        else
        {
            int table = nC switch { -1 => 3, -2 => 4, < 2 => 0, < 4 => 1, _ => 2 };
            token = H264Tables.CoeffTokenVlc[table].Read(_bits);
        }

        int total = token >> 2, trailingOnes = token & 3;
        if (total == 0)
        {
            return 0;
        }

        if (total > maxNumCoeff)
        {
            throw H264Errors.Corrupt($"A block has {total} coefficients, more than {maxNumCoeff}.");
        }

        Span<int> levelVal = stackalloc int[16];
        int suffixLength = total > 10 && trailingOnes < 3 ? 1 : 0;
        for (int i = 0; i < total; i++)
        {
            if (i < trailingOnes)
            {
                levelVal[i] = 1 - (2 * _bits.Read(1));
                continue;
            }

            int prefix = 0;
            while (_bits.Read(1) == 0)
            {
                if (++prefix > 32)
                {
                    throw H264Errors.Corrupt("A level_prefix is too long.");
                }
            }

            int levelCode = Math.Min(15, prefix) << suffixLength;
            if (suffixLength > 0 || prefix >= 14)
            {
                int size = prefix == 14 && suffixLength == 0 ? 4 : prefix >= 15 ? prefix - 3 : suffixLength;
                if (size > 0)
                {
                    levelCode += _bits.Read(size);
                }
            }

            if (prefix >= 15 && suffixLength == 0)
            {
                levelCode += 15;
            }

            if (prefix >= 16)
            {
                levelCode += (1 << (prefix - 3)) - 4096;
            }

            if (i == trailingOnes && trailingOnes < 3)
            {
                levelCode += 2;
            }

            int level = (levelCode & 1) == 0 ? (levelCode + 2) >> 1 : (-levelCode - 1) >> 1;
            levelVal[i] = level;
            if (suffixLength == 0)
            {
                suffixLength = 1;
            }

            if (Math.Abs(level) > (3 << (suffixLength - 1)) && suffixLength < 6)
            {
                suffixLength++;
            }
        }

        int zerosLeft = 0;
        if (total < maxNumCoeff)
        {
            zerosLeft = maxNumCoeff == 4 && nC == -1 ? H264Tables.TotalZerosChromaDc420Vlc[total].Read(_bits)
                : maxNumCoeff == 8 && nC == -2 ? H264Tables.TotalZerosChromaDc422Vlc[total].Read(_bits)
                : H264Tables.TotalZerosVlc[total].Read(_bits);
            if (zerosLeft + total > maxNumCoeff)
            {
                throw H264Errors.Corrupt("total_zeros is too large.");
            }
        }

        // Levels come from the highest frequency down; runs of zeros sit before each.
        int coeffNum = total + zerosLeft - 1;
        for (int i = 0; i < total; i++)
        {
            levels[coeffNum] = levelVal[i];
            int run = 0;
            if (i < total - 1 && zerosLeft > 0)
            {
                run = H264Tables.RunBeforeVlc[Math.Min(zerosLeft, 7)].Read(_bits);
                if (run > zerosLeft)
                {
                    throw H264Errors.Corrupt("run_before is larger than the zeros left.");
                }

                zerosLeft -= run;
            }

            coeffNum -= run + 1;
        }

        return total;
    }

    /// <summary>
    /// Reads residual_block_cabac (7.3.5.3.3, 9.3.2.7) for a block category into <see cref="_levels"/> in scan order; returns the
    /// number of non-zero levels. cbfInc is -1 when coded_block_flag is not coded (8x8 blocks outside 4:4:4).
    /// </summary>
    private int ResidualCabac(int cat, int cbfInc, int maxNumCoeff, int[] levels)
    {
        Array.Clear(levels, 0, maxNumCoeff);
        ReadOnlySpan<byte> cbfOffset = [0, 4, 8, 12, 16];
        ReadOnlySpan<byte> sigOffset = [0, 15, 29, 44, 47];
        ReadOnlySpan<byte> absOffset = [0, 10, 20, 30, 39];
        if (cbfInc >= 0 && Bin(85 + cbfOffset[cat] + cbfInc) == 0)
        {
            return 0;
        }

        int sigBase, lastBase, absBase;
        if (cat == 5)
        {
            sigBase = 402;
            lastBase = 417;
            absBase = 426;
        }
        else
        {
            sigBase = 105 + sigOffset[cat];
            lastBase = 166 + sigOffset[cat];
            absBase = 227 + absOffset[cat];
        }

        int numC8x8 = _chromaArrayType == 1 ? 1 : 2;
        Span<byte> significant = stackalloc byte[64];
        int numCoeff = maxNumCoeff;
        int count = 0;
        bool last = false;
        ref byte sig = ref _ctx[sigBase];
        ref byte lastCtx = ref _ctx[lastBase];
        CabacEngine engine = _engine;
        if (cat == 5)
        {
            ReadOnlySpan<byte> sigInc = H264Tables.Significant8x8, lastInc = H264Tables.Last8x8;
            for (int i = 0; i < 63; i++)
            {
                if (engine.Decode(ref Unsafe.Add(ref sig, sigInc[i])) != 0)
                {
                    significant[count++] = (byte)i;
                    if (engine.Decode(ref Unsafe.Add(ref lastCtx, lastInc[i])) != 0)
                    {
                        last = true;
                        break;
                    }
                }
            }
        }
        else if (cat == 3)
        {
            for (int i = 0; i < numCoeff - 1; i++)
            {
                int inc = Math.Min(i / numC8x8, 2);
                if (engine.Decode(ref Unsafe.Add(ref sig, inc)) != 0)
                {
                    significant[count++] = (byte)i;
                    if (engine.Decode(ref Unsafe.Add(ref lastCtx, inc)) != 0)
                    {
                        last = true;
                        break;
                    }
                }
            }
        }
        else
        {
            for (int i = 0; i < numCoeff - 1; i++)
            {
                if (engine.Decode(ref Unsafe.Add(ref sig, i)) != 0)
                {
                    significant[count++] = (byte)i;
                    if (engine.Decode(ref Unsafe.Add(ref lastCtx, i)) != 0)
                    {
                        last = true;
                        break;
                    }
                }
            }
        }

        if (!last)
        {
            // The last position is significant when no earlier one was the last.
            significant[count++] = (byte)(numCoeff - 1);
        }

        int eq1 = 0, gt1 = 0;
        int maxGt1Inc = cat == 3 ? 3 : 4;
        ref byte abs = ref _ctx[absBase];
        for (int k = count - 1; k >= 0; k--)
        {
            int inc = gt1 != 0 ? 0 : Math.Min(4, 1 + eq1);
            int value;
            if (engine.Decode(ref Unsafe.Add(ref abs, inc)) == 0)
            {
                value = 1;
            }
            else
            {
                ref byte ctx = ref Unsafe.Add(ref abs, 5 + Math.Min(maxGt1Inc, gt1));
                int prefix = 1;
                while (prefix < 14 && engine.Decode(ref ctx) != 0)
                {
                    prefix++;
                }

                value = prefix + 1;
                if (prefix == 14)
                {
                    // Exp-Golomb suffix, k = 0.
                    int k0 = 0;
                    int suffix = 0;
                    while (_engine.Bypass() != 0)
                    {
                        suffix += 1 << k0;
                        if (++k0 > 24)
                        {
                            throw H264Errors.Corrupt("A coefficient level is too long.");
                        }
                    }

                    suffix += (int)_engine.Bypass(k0);
                    value += suffix;
                }
            }

            if (value == 1)
            {
                eq1++;
            }
            else
            {
                gt1++;
            }

            levels[significant[k]] = _engine.Bypass() != 0 ? -value : value;
        }

        return count;
    }
}
