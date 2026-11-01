using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Egami.Heic.Hevc;

/// <summary>
/// Writes the slice segment data syntax of an intra picture (H.265 7.3.8) from the encoder's decisions, the inverse of
/// <see cref="HevcSliceDecoder"/> step for step: SAO, the coding quadtree, coding units with their intra modes, transform trees,
/// cu_qp_delta and residual coding. The bins go to <see cref="HevcBins"/>, so the same code counts the cost of a candidate during
/// the search and writes the final bitstream. Contexts that depend on neighbours (split_cu_flag, the most probable modes) read the
/// picture's block maps, which hold every block decoded before the current one, as the decoder's do.
/// </summary>
internal sealed class HevcSyntaxWriter
{
    private readonly HevcPicture _picture;
    private readonly HevcSps _sps;
    private readonly HevcPps _pps;
    private readonly HevcSliceHeader _slice;
    private readonly int _log2MinCuQpDeltaSize;
    private bool _isCuQpDeltaCoded;
    private HevcCodingUnit _cu = null!;

    internal HevcSyntaxWriter(HevcPicture picture, HevcSliceHeader slice, HevcBins bins)
    {
        _picture = picture;
        _sps = picture.Sps;
        _pps = picture.Pps;
        _slice = slice;
        Bins = bins;
        _log2MinCuQpDeltaSize = _sps.CtbLog2Size - _pps.DiffCuQpDeltaDepth;
    }

    /// <summary>Gets where the bins go.</summary>
    internal HevcBins Bins { get; }

    /// <summary>Gets or sets IsCuQpDeltaCoded of the current quantization group.</summary>
    internal bool IsCuQpDeltaCoded
    {
        get => _isCuQpDeltaCoded;
        set => _isCuQpDeltaCoded = value;
    }

    // ---------------------------------------------------------------- SAO syntax (7.3.8.3)

    /// <summary>Writes a CTB's SAO syntax: a merge with the CTB to the left or above, or each component's parameters.</summary>
    internal void Sao(int ctbAddrRs, in HevcSaoParameters sao, bool mergeLeft, bool mergeUp)
    {
        int widthCtbs = _sps.WidthInCtbs;
        int rx = ctbAddrRs % widthCtbs, ry = ctbAddrRs / widthCtbs;
        if (rx > 0)
        {
            Bins.Encode(HevcContexts.SaoMerge, mergeLeft ? 1 : 0);
            if (mergeLeft)
            {
                return;
            }
        }

        if (ry > 0)
        {
            Bins.Encode(HevcContexts.SaoMerge, mergeUp ? 1 : 0);
            if (mergeUp)
            {
                return;
            }
        }

        int components = _sps.ChromaArrayType != 0 ? 3 : 1;
        for (int c = 0; c < components; c++)
        {
            if ((c == 0 && !_slice.SaoLuma) || (c > 0 && !_slice.SaoChroma))
            {
                continue;
            }

            int type = sao.Type(c);
            if (c != 2)
            {
                Bins.Encode(HevcContexts.SaoTypeIdx, type == 0 ? 0 : 1);
                if (type != 0)
                {
                    Bins.Bypass(type == 1 ? 0 : 1);
                }
            }

            if (type == 0)
            {
                continue;
            }

            int bitDepth = c == 0 ? _sps.BitDepthLuma : _sps.BitDepthChroma;
            int cMax = (1 << (Math.Min(bitDepth, 10) - 5)) - 1;
            HevcSaoParameters.OffsetSet offsets = sao.Offsets(c);
            for (int i = 0; i < 4; i++)
            {
                TruncatedUnaryBypass(Math.Abs(offsets[i]), cMax);
            }

            if (type == 1)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (offsets[i] != 0)
                    {
                        Bins.Bypass(offsets[i] < 0 ? 1 : 0);
                    }
                }

                Bins.Bypass((uint)sao.Class(c), 5);
            }
            else if (c != 2)
            {
                Bins.Bypass((uint)sao.Class(c), 2);
            }
        }
    }

    private void TruncatedUnaryBypass(int value, int cMax)
    {
        for (int i = 0; i < value; i++)
        {
            Bins.Bypass(1);
        }

        if (value < cMax)
        {
            Bins.Bypass(0);
        }
    }

    // ---------------------------------------------------------------- coding quadtree and coding unit (7.3.8.4, 7.3.8.5)

    /// <summary>Writes a CTB's coding quadtree from its coding units in decoding order.</summary>
    internal void CodingTree(List<HevcCodingUnit> cus, int xCtb, int yCtb)
    {
        int index = 0;
        CodingQuadtree(cus, ref index, xCtb, yCtb, _sps.CtbLog2Size, 0);
        Debug.Assert(index == cus.Count, "Every coding unit of the CTB is written.");
    }

    private void CodingQuadtree(List<HevcCodingUnit> cus, ref int index, int x0, int y0, int log2CbSize, int depth)
    {
        int size = 1 << log2CbSize;
        bool split = cus[index].Log2 < log2CbSize;
        if (x0 + size <= _sps.Width && y0 + size <= _sps.Height && log2CbSize > _sps.MinCbLog2Size)
        {
            SplitFlag(x0, y0, depth, split);
        }

        if (_pps.CuQpDeltaEnabled && log2CbSize >= _log2MinCuQpDeltaSize)
        {
            _isCuQpDeltaCoded = false;
        }

        if (split)
        {
            int half = size >> 1;
            for (int i = 0; i < 4; i++)
            {
                int x = x0 + ((i & 1) * half), y = y0 + ((i >> 1) * half);
                if (x < _sps.Width && y < _sps.Height)
                {
                    CodingQuadtree(cus, ref index, x, y, log2CbSize - 1, depth + 1);
                }
            }
        }
        else
        {
            CodingUnit(cus[index++]);
        }
    }

    /// <summary>Writes split_cu_flag, its context from the depths of the coding units to the left and above.</summary>
    internal void SplitFlag(int x0, int y0, int depth, bool split)
    {
        int ctxInc = 0;
        if (_picture.Available(x0, y0, x0 - 1, y0) && _picture.CtDepth[((y0 >> 2) * _picture.Width4) + ((x0 - 1) >> 2)] > depth)
        {
            ctxInc++;
        }

        if (_picture.Available(x0, y0, x0, y0 - 1) && _picture.CtDepth[(((y0 - 1) >> 2) * _picture.Width4) + (x0 >> 2)] > depth)
        {
            ctxInc++;
        }

        Bins.Encode(HevcContexts.SplitCuFlag + ctxInc, split ? 1 : 0);
    }

    /// <summary>Writes a coding unit: transquant bypass, part_mode, the intra modes and the transform tree.</summary>
    internal void CodingUnit(HevcCodingUnit cu)
    {
        CodingUnitHeader(cu);
        int maxTrafoDepth = _sps.MaxTransformHierarchyDepthIntra + (cu.NxN ? 1 : 0);
        TransformTree(cu, cu.Tree, cu.X, cu.Y, cu.X, cu.Y, cu.Log2, 0, 0, maxTrafoDepth, true, true, true, true);
    }

    /// <summary>Writes a coding unit's syntax before its transform tree: transquant bypass, part_mode and the intra modes.</summary>
    internal void CodingUnitHeader(HevcCodingUnit cu)
    {
        _cu = cu;
        if (_pps.TransquantBypassEnabled)
        {
            Bins.Encode(HevcContexts.CuTransquantBypass, cu.Bypass ? 1 : 0);
        }

        if (cu.Log2 == _sps.MinCbLog2Size)
        {
            Bins.Encode(HevcContexts.PartMode, cu.NxN ? 0 : 1);
        }

        LumaModes(cu);
        ChromaModes(cu);
    }

    /// <summary>prev_intra_luma_pred_flag, then mpm_idx or rem_intra_luma_pred_mode, of each prediction unit (8.4.2).</summary>
    internal void LumaModes(HevcCodingUnit cu)
    {
        int parts = cu.NxN ? 4 : 1;
        Span<int> mpm = stackalloc int[3 * 4];
        for (int i = 0; i < parts; i++)
        {
            int pbSize = cu.NxN ? 1 << (cu.Log2 - 1) : 1 << cu.Log2;
            int xPb = cu.X + ((i & 1) * pbSize), yPb = cu.Y + ((i >> 1) * pbSize);
            MostProbableModes(xPb, yPb, i > 0 ? cu.LumaModes.AsSpan(0, i) : default, cu, mpm.Slice(3 * i, 3));
        }

        for (int i = 0; i < parts; i++)
        {
            Bins.Encode(HevcContexts.PrevIntraLumaPred, mpm.Slice(3 * i, 3).Contains(cu.LumaModes[i]) ? 1 : 0);
        }

        for (int i = 0; i < parts; i++)
        {
            LumaModeIndex(cu.LumaModes[i], mpm.Slice(3 * i, 3));
        }
    }

    /// <summary>Writes mpm_idx or rem_intra_luma_pred_mode for a mode, given the prediction unit's most probable modes.</summary>
    internal void LumaModeIndex(int mode, ReadOnlySpan<int> mpm)
    {
        int index = mpm.IndexOf(mode);
        if (index >= 0)
        {
            Bins.Bypass(index == 0 ? 0 : 1);
            if (index > 0)
            {
                Bins.Bypass(index == 1 ? 0 : 1);
            }

            return;
        }

        Span<int> sorted = stackalloc int[3];
        mpm.CopyTo(sorted);
        sorted.Sort();
        int rem = mode;
        for (int k = 2; k >= 0; k--)
        {
            if (mode > sorted[k])
            {
                rem--;
            }
        }

        Bins.Bypass((uint)rem, 5);
    }

    /// <summary>
    /// candModeList (8.4.2) of the prediction unit at (xPb, yPb). <paramref name="earlier"/> are the modes of the coding unit's
    /// earlier prediction units, which the picture's block map may not hold yet.
    /// </summary>
    internal void MostProbableModes(int xPb, int yPb, ReadOnlySpan<int> earlier, HevcCodingUnit cu, Span<int> mpm)
    {
        int a = NeighbourMode(xPb, yPb, xPb - 1, yPb, earlier, cu);

        // The neighbour above in another CTB row counts as DC, so line buffers stay one CTB row tall.
        int b = yPb - 1 < ((yPb >> _sps.CtbLog2Size) << _sps.CtbLog2Size) ? 1 : NeighbourMode(xPb, yPb, xPb, yPb - 1, earlier, cu);
        if (a == b)
        {
            if (a < 2)
            {
                (mpm[0], mpm[1], mpm[2]) = (0, 1, 26);
            }
            else
            {
                (mpm[0], mpm[1], mpm[2]) = (a, 2 + ((a + 29) % 32), 2 + ((a - 2 + 1) % 32));
            }
        }
        else
        {
            (mpm[0], mpm[1]) = (a, b);
            mpm[2] = a != 0 && b != 0 ? 0 : a != 1 && b != 1 ? 1 : 26;
        }
    }

    private int NeighbourMode(int xCurr, int yCurr, int xN, int yN, ReadOnlySpan<int> earlier, HevcCodingUnit cu)
    {
        if (!_picture.Available(xCurr, yCurr, xN, yN))
        {
            return 1;
        }

        // A neighbour inside the same NxN coding unit is one of its earlier prediction units.
        if (cu.NxN && xN >= cu.X && yN >= cu.Y && xN < cu.X + (1 << cu.Log2) && yN < cu.Y + (1 << cu.Log2))
        {
            int half = 1 << (cu.Log2 - 1);
            int part = (xN - cu.X >= half ? 1 : 0) + (yN - cu.Y >= half ? 2 : 0);
            if (part < earlier.Length)
            {
                return earlier[part];
            }
        }

        return _picture.IntraMode[((yN >> 2) * _picture.Width4) + (xN >> 2)];
    }

    /// <summary>Writes intra_chroma_pred_mode of each chroma prediction block.</summary>
    internal void ChromaModes(HevcCodingUnit cu)
    {
        if (_sps.ChromaArrayType == 0)
        {
            return;
        }

        int chromaParts = _sps.ChromaArrayType == 3 && cu.NxN ? 4 : 1;
        for (int i = 0; i < chromaParts; i++)
        {
            ChromaMode(cu.ChromaSyntax[i]);
        }
    }

    /// <summary>Writes one intra_chroma_pred_mode: 4 (the luma mode) as one context-coded bin, 0 to 3 as 1 and two bypass bins.</summary>
    internal void ChromaMode(int syntax)
    {
        Bins.Encode(HevcContexts.IntraChromaPredMode, syntax == 4 ? 0 : 1);
        if (syntax != 4)
        {
            Bins.Bypass((uint)syntax, 2);
        }
    }

    /// <summary>Gets the chroma prediction mode intra_chroma_pred_mode gives with a luma mode (8.4.3), mapped for 4:2:2.</summary>
    internal static int ChromaPredictionMode(int syntax, int luma, int chromaArrayType)
    {
        int mode = syntax == 4 ? luma : syntax switch { 0 => 0, 1 => 26, 2 => 10, _ => 1 };
        if (syntax != 4 && mode == luma)
        {
            mode = 34;
        }

        return chromaArrayType == 2 ? HevcSliceDecoder.Mode422[mode] : mode;
    }

    // ---------------------------------------------------------------- transform tree and unit (7.3.8.8, 7.3.8.10)

    /// <summary>Writes a transform tree, as <see cref="HevcSliceDecoder"/> reads it.</summary>
    internal void TransformTree(HevcCodingUnit cu, HevcTransformNode node, int x0, int y0, int xBase, int yBase, int log2TrafoSize, int trafoDepth,
        int blkIdx, int maxTrafoDepth, bool parentCb0, bool parentCb1, bool parentCr0, bool parentCr1)
    {
        _cu = cu;
        bool split = node.Split;
        if (log2TrafoSize <= _sps.MaxTbLog2Size && log2TrafoSize > _sps.MinTbLog2Size && trafoDepth < maxTrafoDepth && !(cu.NxN && trafoDepth == 0))
        {
            Bins.Encode(HevcContexts.SplitTransform + 5 - log2TrafoSize, split ? 1 : 0);
        }
        else
        {
            Debug.Assert(split == (log2TrafoSize > _sps.MaxTbLog2Size || (cu.NxN && trafoDepth == 0)), "An inferred split_transform_flag is kept.");
        }

        int chroma = _sps.ChromaArrayType;
        if ((log2TrafoSize > 2 && chroma != 0) || chroma == 3)
        {
            if (trafoDepth == 0 || parentCb0 || parentCb1)
            {
                Cbf(trafoDepth, node.Cb0);
                if (chroma == 2 && (!split || log2TrafoSize == 3))
                {
                    Cbf(trafoDepth, node.Cb1);
                }
            }

            if (trafoDepth == 0 || parentCr0 || parentCr1)
            {
                Cbf(trafoDepth, node.Cr0);
                if (chroma == 2 && (!split || log2TrafoSize == 3))
                {
                    Cbf(trafoDepth, node.Cr1);
                }
            }
        }

        if (split)
        {
            int half = 1 << (log2TrafoSize - 1);
            for (int i = 0; i < 4; i++)
            {
                TransformTree(cu, node.Children![i], x0 + ((i & 1) * half), y0 + ((i >> 1) * half), x0, y0, log2TrafoSize - 1, trafoDepth + 1, i,
                    maxTrafoDepth, node.Cb0, node.Cb1, node.Cr0, node.Cr1);
            }
        }
        else
        {
            Bins.Encode(HevcContexts.CbfLuma + (trafoDepth == 0 ? 1 : 0), node.CbfLuma ? 1 : 0);
            TransformUnit(cu, node, x0, y0, log2TrafoSize, blkIdx);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Cbf(int trafoDepth, bool value) => Bins.Encode(HevcContexts.CbfChroma + trafoDepth, value ? 1 : 0);

    /// <summary>Writes a transform unit: cu_qp_delta when it is the first with coefficients in its group, then the residuals.</summary>
    internal void TransformUnit(HevcCodingUnit cu, HevcTransformNode node, int x0, int y0, int log2TrafoSize, int blkIdx)
    {
        int chroma = _sps.ChromaArrayType;
        bool cbfChroma = node.Cb0 || node.Cb1 || node.Cr0 || node.Cr1;
        if ((node.CbfLuma || cbfChroma) && _pps.CuQpDeltaEnabled && !_isCuQpDeltaCoded)
        {
            QpDelta(cu.QpDelta);
            _isCuQpDeltaCoded = true;
        }

        int lumaMode = cu.LumaModes[PartIndex(cu, x0, y0)];
        if (node.CbfLuma)
        {
            Residual(node.Levels[0]!, log2TrafoSize, 0, lumaMode, node.TransformSkip[0], cu.Bypass);
        }

        if (chroma == 0)
        {
            return;
        }

        if (log2TrafoSize > 2 || chroma == 3)
        {
            int log2C = chroma == 3 ? log2TrafoSize : log2TrafoSize - 1;
            int part = chroma == 3 ? PartIndex(cu, x0, y0) : 0;
            ChromaResiduals(cu, node, log2C, part);
        }
        else if (blkIdx == 3)
        {
            ChromaResiduals(cu, node, log2TrafoSize, 0);
        }
    }

    private void ChromaResiduals(HevcCodingUnit cu, HevcTransformNode node, int log2C, int part)
    {
        int chroma = _sps.ChromaArrayType;
        int mode = ChromaPredictionMode(cu.ChromaSyntax[part], cu.LumaModes[part], chroma);
        for (int c = 1; c <= 2; c++)
        {
            for (int t = 0; t < (chroma == 2 ? 2 : 1); t++)
            {
                bool cbf = c == 1 ? (t == 0 ? node.Cb0 : node.Cb1) : (t == 0 ? node.Cr0 : node.Cr1);
                if (cbf)
                {
                    int block = HevcTransformNode.Block(c, t);
                    Residual(node.Levels[block]!, log2C, c, mode, node.TransformSkip[block], cu.Bypass);
                }
            }
        }
    }

    /// <summary>The prediction unit of the coding unit that (x, y) lies in: 0 for 2Nx2N, 0 to 3 for NxN.</summary>
    internal static int PartIndex(HevcCodingUnit cu, int x, int y)
    {
        if (!cu.NxN)
        {
            return 0;
        }

        int half = 1 << (cu.Log2 - 1);
        return (x - cu.X >= half ? 1 : 0) + (y - cu.Y >= half ? 2 : 0);
    }

    /// <summary>Writes cu_qp_delta_abs (a truncated unary prefix of 5 context-coded bins, then Exp-Golomb) and its sign.</summary>
    internal void QpDelta(int delta)
    {
        int abs = Math.Abs(delta);
        int prefix = Math.Min(abs, 5);
        for (int i = 0; i < prefix; i++)
        {
            Bins.Encode(HevcContexts.CuQpDeltaAbs + (i == 0 ? 0 : 1), 1);
        }

        if (prefix < 5)
        {
            Bins.Encode(HevcContexts.CuQpDeltaAbs + (prefix == 0 ? 0 : 1), 0);
        }
        else
        {
            ExpGolombBypass((uint)(abs - 5), 0);
        }

        if (abs != 0)
        {
            Bins.Bypass(delta < 0 ? 1 : 0);
        }
    }

    /// <summary>A k-th order Exp-Golomb code in bypass bins (9.3.3.3): a unary prefix of ones and a 0, then the suffix.</summary>
    private void ExpGolombBypass(uint value, int k)
    {
        while (value >= 1u << k)
        {
            Bins.Bypass(1);
            value -= 1u << k;
            k++;
        }

        Bins.Bypass(0);
        Bins.Bypass(value, k);
    }

    // ---------------------------------------------------------------- residual coding (7.3.8.11)

    /// <summary>Gets scanIdx (7.4.9.11): horizontal or vertical for 4x4 and 8x8 blocks of near-vertical or near-horizontal modes.</summary>
    internal static int ScanIndex(int log2, int c, int predMode, int chromaArrayType)
    {
        if (log2 == 2 || (log2 == 3 && (c == 0 || chromaArrayType == 3)))
        {
            return predMode is >= 6 and <= 14 ? 2 : predMode is >= 22 and <= 30 ? 1 : 0;
        }

        return 0;
    }

    /// <summary>
    /// Writes residual_coding of a block's levels (raster order, at least one not zero). With sign data hiding the levels must
    /// already give the hidden sign: the first level in scan order of a sub-block whose levels span more than 3 positions is
    /// negative exactly when the sum of the sub-block's absolute levels is odd.
    /// </summary>
    [SkipLocalsInit]
    internal void Residual(short[] levels, int log2, int c, int predMode, bool transformSkip, bool bypass)
    {
        int n = 1 << log2;
        if (_pps.TransformSkipEnabled && !bypass && log2 <= _pps.Log2MaxTransformSkipSize)
        {
            Bins.Encode(HevcContexts.TransformSkip + (c == 0 ? 0 : 1), transformSkip ? 1 : 0);
        }

        bool signHidingAllowed = _pps.SignDataHiding && !bypass;
        int scanIdx = ScanIndex(log2, c, predMode, _sps.ChromaArrayType);
        byte[] inner = HevcScan.Get(scanIdx, 2);
        int log2Sb = log2 - 2;
        byte[] outer = HevcScan.Get(scanIdx, log2Sb);
        int sbWidth = 1 << log2Sb;

        // The last significant coefficient in scan order.
        int lastSubBlock = -1, lastScanPos = -1;
        for (int i = (1 << (2 * log2Sb)) - 1; i >= 0 && lastSubBlock < 0; i--)
        {
            int xS = outer[2 * i], yS = outer[(2 * i) + 1];
            for (int p = 15; p >= 0; p--)
            {
                if (levels[((((yS << 2) + inner[(2 * p) + 1])) * n) + (xS << 2) + inner[2 * p]] != 0)
                {
                    (lastSubBlock, lastScanPos) = (i, p);
                    break;
                }
            }
        }

        Debug.Assert(lastSubBlock >= 0, "A coded block has a level.");
        int lastX = (outer[2 * lastSubBlock] << 2) + inner[2 * lastScanPos];
        int lastY = (outer[(2 * lastSubBlock) + 1] << 2) + inner[(2 * lastScanPos) + 1];
        if (scanIdx == 2)
        {
            (lastX, lastY) = (lastY, lastX);
        }

        LastPosition(lastX, lastY, log2, c);
        Levels(levels, n, log2, c, lastSubBlock, lastScanPos, inner, outer, sbWidth, scanIdx, transformSkip || bypass, signHidingAllowed);
    }

    /// <summary>last_sig_coeff_x_prefix, last_sig_coeff_y_prefix, then their suffixes.</summary>
    private void LastPosition(int lastX, int lastY, int log2, int c)
    {
        int ctxOffset, ctxShift;
        if (c == 0)
        {
            ctxOffset = (3 * (log2 - 2)) + ((log2 - 1) >> 2);
            ctxShift = (log2 + 1) >> 2;
        }
        else
        {
            ctxOffset = 15;
            ctxShift = log2 - 2;
        }

        int maxPrefix = (log2 << 1) - 1;
        int prefixX = Prefix(lastX, out int suffixX, out int bitsX), prefixY = Prefix(lastY, out int suffixY, out int bitsY);
        for (int i = 0; i < prefixX; i++)
        {
            Bins.Encode(HevcContexts.LastXPrefix + ctxOffset + (i >> ctxShift), 1);
        }

        if (prefixX < maxPrefix)
        {
            Bins.Encode(HevcContexts.LastXPrefix + ctxOffset + (prefixX >> ctxShift), 0);
        }

        for (int i = 0; i < prefixY; i++)
        {
            Bins.Encode(HevcContexts.LastYPrefix + ctxOffset + (i >> ctxShift), 1);
        }

        if (prefixY < maxPrefix)
        {
            Bins.Encode(HevcContexts.LastYPrefix + ctxOffset + (prefixY >> ctxShift), 0);
        }

        Bins.Bypass((uint)suffixX, bitsX);
        Bins.Bypass((uint)suffixY, bitsY);
    }

    /// <summary>The prefix of a last position (its group, Table 9-?), and the suffix and its length when the prefix is above 3.</summary>
    internal static int Prefix(int position, out int suffix, out int bits)
    {
        if (position < 4)
        {
            (suffix, bits) = (0, 0);
            return position;
        }

        int k = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)position);
        int prefix = (2 * k) + ((position >> (k - 1)) & 1);
        bits = (prefix >> 1) - 1;
        suffix = position - ((1 << bits) * (2 + (prefix & 1)));
        return prefix;
    }

    [SkipLocalsInit]
    private void Levels(short[] levels, int n, int log2, int c, int lastSubBlock, int lastScanPos, byte[] inner, byte[] outer, int sbWidth, int scanIdx,
        bool transformSkipContext, bool signHidingAllowed)
    {
        transformSkipContext &= _sps.TransformSkipContext;
        ulong codedSubBlock = 0;
        int greater1State = 1;
        Span<int> positions = stackalloc int[16];
        Span<int> values = stackalloc int[16];
        for (int i = lastSubBlock; i >= 0; i--)
        {
            int xS = outer[2 * i], yS = outer[(2 * i) + 1];
            int origin = (yS << 2) * n + (xS << 2);
            bool right = xS + 1 < sbWidth && (codedSubBlock & (1UL << ((yS * sbWidth) + xS + 1))) != 0;
            bool below = yS + 1 < sbWidth && (codedSubBlock & (1UL << (((yS + 1) * sbWidth) + xS))) != 0;
            bool inferSbDcSig = false, coded = true;
            if (i < lastSubBlock && i > 0)
            {
                coded = false;
                for (int p = 0; p < 16 && !coded; p++)
                {
                    coded = At(levels, origin, n, inner, p) != 0;
                }

                Bins.Encode(HevcContexts.CodedSubBlock + Math.Min((right ? 1 : 0) + (below ? 1 : 0), 1) + (c == 0 ? 0 : 2), coded ? 1 : 0);
                inferSbDcSig = true;
            }

            if (coded)
            {
                codedSubBlock |= 1UL << ((yS * sbWidth) + xS);
            }

            int count = 0;
            int startPos = i == lastSubBlock ? lastScanPos - 1 : 15;
            if (i == lastSubBlock)
            {
                positions[count++] = lastScanPos;
            }

            if (coded)
            {
                int table = (((((c == 0 ? 0 : 1) * 4) + log2 - 2) * 3) + scanIdx) * 4;
                byte[] sigTable = HevcSliceDecoder.SigContexts[table + (right ? 1 : 0) + (below ? 2 : 0)];
                int corner = (yS << (2 + log2)) + (xS << 2);
                byte[] offsets = HevcScan.Offsets(scanIdx, log2);
                int fixedCtx = c == 0 ? 42 : 43;
                for (int p = startPos; p > 0; p--)
                {
                    bool sig = At(levels, origin, n, inner, p) != 0;
                    Bins.Encode(HevcContexts.SigCoeff + (transformSkipContext ? fixedCtx : sigTable[corner + offsets[p]]), sig ? 1 : 0);
                    if (sig)
                    {
                        positions[count++] = p;
                    }
                }

                if (startPos >= 0)
                {
                    bool sig = At(levels, origin, n, inner, 0) != 0;
                    if (!(inferSbDcSig && count == 0))
                    {
                        Bins.Encode(HevcContexts.SigCoeff + (transformSkipContext ? fixedCtx : sigTable[corner]), sig ? 1 : 0);
                    }
                    else
                    {
                        Debug.Assert(sig, "An inferred DC level is not zero.");
                    }

                    if (sig)
                    {
                        positions[count++] = 0;
                    }
                }
            }

            if (count == 0)
            {
                continue;
            }

            for (int k = 0; k < count; k++)
            {
                values[k] = At(levels, origin, n, inner, positions[k]);
            }

            // coeff_abs_level_greater1_flag (at most 8) and greater2_flag (the first greater than 1).
            int ctxSet = (i == 0 || c > 0 ? 0 : 2) + (greater1State == 0 ? 1 : 0);
            greater1State = 1;
            int firstGreater1 = -1;
            for (int k = 0; k < Math.Min(count, 8); k++)
            {
                int bin = Math.Abs(values[k]) > 1 ? 1 : 0;
                Bins.Encode(HevcContexts.Greater1 + (ctxSet * 4) + greater1State + (c > 0 ? 16 : 0), bin);
                if (bin == 1)
                {
                    greater1State = 0;
                    if (firstGreater1 < 0)
                    {
                        firstGreater1 = k;
                    }
                }
                else if (greater1State is > 0 and < 3)
                {
                    greater1State++;
                }
            }

            if (firstGreater1 >= 0)
            {
                Bins.Encode(HevcContexts.Greater2 + ctxSet + (c > 0 ? 4 : 0), Math.Abs(values[firstGreater1]) > 2 ? 1 : 0);
            }

            // coeff_sign_flag, but the hidden sign (7.4.9.11).
            bool signHidden = signHidingAllowed && positions[0] - positions[count - 1] > 3;
            int signCount = signHidden ? count - 1 : count;
            uint signs = 0;
            for (int k = 0; k < signCount; k++)
            {
                signs = (signs << 1) | (values[k] < 0 ? 1u : 0u);
            }

            Bins.Bypass(signs, signCount);
#if DEBUG
            if (signHidden)
            {
                int sum = 0;
                for (int k = 0; k < count; k++)
                {
                    sum += Math.Abs(values[k]);
                }

                Debug.Assert((sum & 1) == (values[count - 1] < 0 ? 1 : 0), "The hidden sign matches the parity of the levels.");
            }
#endif

            // coeff_abs_level_remaining.
            int rice = 0;
            bool firstCoeff2 = true;
            for (int k = 0; k < count; k++)
            {
                int abs = Math.Abs(values[k]);
                int baseLevel = k < 8 ? (firstCoeff2 ? 3 : 2) : 1;
                if (abs >= baseLevel)
                {
                    Remaining(abs - baseLevel, rice);
                    if (abs > 3 << rice)
                    {
                        rice = Math.Min(rice + 1, 4);
                    }
                }

                if (abs >= 2)
                {
                    firstCoeff2 = false;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int At(short[] levels, int origin, int n, byte[] inner, int p) => levels[origin + (inner[(2 * p) + 1] * n) + inner[2 * p]];

    /// <summary>coeff_abs_level_remaining (9.3.3.11): a Rice prefix of up to four, then Exp-Golomb of order rice + 1.</summary>
    internal void Remaining(int value, int rice)
    {
        if (value < 4 << rice)
        {
            int prefix = value >> rice;
            Bins.Bypass((1u << (prefix + 1)) - 2, prefix + 1);
            Bins.Bypass((uint)(value & ((1 << rice) - 1)), rice);
            return;
        }

        int w = (value >> rice) - 2;
        int e = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)w);
        int suffix = value - (((1 << e) + 2) << rice);
        int ones = e + 3;
        Bins.Bypass((1u << (ones + 1)) - 2, ones + 1);
        Bins.Bypass((uint)suffix, e + rice);
    }

    /// <summary>Gets the cost in bins of coeff_abs_level_remaining for a value, as <see cref="Remaining"/> codes it.</summary>
    internal static int RemainingLength(int value, int rice)
    {
        if (value < 4 << rice)
        {
            return (value >> rice) + 1 + rice;
        }

        int w = (value >> rice) - 2;
        int e = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)w);
        return e + 4 + e + rice;
    }
}
