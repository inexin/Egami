namespace Egami.Formats.Av1;

/// <summary>
/// Inter prediction (AV1 specification sections 5.11.33 and 7.11.3): each plane predicted from one or two reference frames with
/// scaled 8-tap and 4-tap filters, or warped; the averaged, distance-weighted, wedge and difference-weighted compounds; inter-intra;
/// and overlapped prediction from the neighbours above and left. The warp follows libaom's warped_motion.c (BSD 2-clause, see
/// THIRD-PARTY-NOTICES).
/// </summary>
internal sealed partial class Av1Reconstructor
{
    private const int MaxIntermediate = ((((128 - 1) * 2048) + 1023) >> 10) + 8;
    private static byte[]?[]? s_wedgeMasks;

    private readonly int[] _pred0 = new int[128 * 128];
    private readonly int[] _pred1 = new int[128 * 128];
    private readonly int[] _intermediate = new int[MaxIntermediate * 128];
    private readonly byte[] _mask = new byte[128 * 128];
    private int _maskStride;

    /// <summary>compute_prediction for an inter block: every plane, from its own vectors or, for small chroma, its neighbours'.</summary>
    private void PredictInterBlock(Av1TileState tile)
    {
        Av1BlockInfo b = tile.Block;
        Av1Frame frame = _frame;
        int planes = tile.IsChromaRef ? frame.Header.NumPlanes : 1;
        for (int plane = 0; plane < planes; plane++)
        {
            int ssx = plane > 0 ? _ssx : 0, ssy = plane > 0 ? _ssy : 0;
            int planeSz = Av1Tables.PlaneBlockSize(b.Size, ssx, ssy);
            int n4w = Av1Tables.MiSizeWide[planeSz], n4h = Av1Tables.MiSizeHigh[planeSz];
            int baseX = (tile.MiCol >> ssx) * 4, baseY = (tile.MiRow >> ssy) * 4;
            int candRow = (tile.MiRow >> ssy) << ssy, candCol = (tile.MiCol >> ssx) << ssx;
            if (b.RefFrame1 == Ref.Intra)
            {
                int mode = b.InterintraMode switch { 0 => Pred.Dc, 1 => Pred.V, 2 => Pred.H, _ => Pred.Smooth };
                int stride = frame.Strides[plane];
                PredictIntra(tile, plane, 0, 0, Av1Tables.MaxTxSizeRectLookup[planeSz], mode, 0, false, -1, frame.Planes![plane], (baseY * stride) + baseX, stride);
            }

            int predW = Av1Tables.BlockSizeWide[b.Size] >> ssx, predH = Av1Tables.BlockSizeHigh[b.Size] >> ssy;
            bool someUseIntra = false;
            for (int r = 0; r < n4h << ssy && candRow + r < frame.MiRows; r++)
            {
                for (int c = 0; c < n4w << ssx && candCol + c < frame.MiCols; c++)
                {
                    someUseIntra |= frame.Grid[((candRow + r) * frame.MiCols) + candCol + c]!.RefFrame0 == Ref.Intra;
                }
            }

            if (someUseIntra)
            {
                predW = n4w * 4;
                predH = n4h * 4;
                candRow = tile.MiRow;
                candCol = tile.MiCol;
            }

            for (int y = 0, r = 0; y < n4h * 4; y += predH, r++)
            {
                for (int x = 0, c = 0; x < n4w * 4; x += predW, c++)
                {
                    PredictInter(tile, plane, baseX + x, baseY + y, predW, predH, candRow + r, candCol + c);
                }
            }
        }
    }

    /// <summary>predict_inter: one region of a plane from the vectors of the block at (candRow, candCol).</summary>
    private void PredictInter(Av1TileState tile, int plane, int x, int y, int w, int h, int candRow, int candCol)
    {
        Av1BlockInfo b = tile.Block;
        Av1FrameHeader header = _frame.Header;
        Av1BlockInfo cand = _frame.Grid[(candRow * _frame.MiCols) + candCol]!;
        bool compound = cand.RefFrame1 > Ref.Intra;
        int round0 = _bitDepth == 12 ? 5 : 3;
        int round1 = compound ? 7 : _bitDepth == 12 ? 9 : 11;
        int postRound = 14 - round0 - round1;
        int ssx = plane > 0 ? _ssx : 0, ssy = plane > 0 ? _ssy : 0;
        for (int list = 0; list < (compound ? 2 : 1); list++)
        {
            int rf = list == 0 ? cand.RefFrame0 : cand.RefFrame1;
            Av1Frame reference = header.RefFrames[rf]!;
            int[] pred = list == 0 ? _pred0 : _pred1;
            int[]? warp = null;
            if (w >= 8 && h >= 8 && !header.ForceIntegerMv)
            {
                if (b.MotionMode == 2 && b.WarpValid)
                {
                    warp = b.WarpParams;
                }
                else if (b.YMode is InterMode.Global or InterMode.GlobalGlobal && header.GmType[rf] > GlobalMotion.Translation
                    && !IsScaled(reference) && Av1Warp.ShearValid(header.GmParams[rf]))
                {
                    warp = header.GmParams[rf];
                }
            }

            int refPlaneW = (reference.Header.UpscaledWidth + ssx) >> ssx, refPlaneH = (reference.Header.FrameHeight + ssy) >> ssy;
            if (warp is not null)
            {
                Av1Warp.Shear(warp, out int alpha, out int beta, out int gamma, out int delta);
                for (int i8 = 0; i8 <= (h - 1) >> 3; i8++)
                {
                    for (int j8 = 0; j8 <= (w - 1) >> 3; j8++)
                    {
                        Av1Warp.Block(warp, alpha, beta, gamma, delta, reference.Planes![plane], reference.Strides[plane], refPlaneW, refPlaneH,
                            x + (j8 * 8), y + (i8 * 8), ssx, ssy, round0, round1, pred, (i8 * 8 * w) + (j8 * 8), w, w - (j8 * 8), h - (i8 * 8));
                    }
                }
            }
            else
            {
                (int mvRow, int mvCol) = list == 0 ? (cand.MvRow, cand.MvCol) : (cand.Mv1Row, cand.Mv1Col);
                (int startX, int startY, int stepX, int stepY) = Scale(reference, plane, x, y, mvRow, mvCol);
                BlockInterPrediction(reference, plane, startX, startY, stepX, stepY, w, h, cand.FilterX, cand.FilterY, round0, round1, pred);
            }
        }

        ushort[] dst = _frame.Planes![plane];
        int stride = _frame.Strides[plane];
        int at = (y * stride) + x;
        int max = (1 << _bitDepth) - 1;
        bool interintra = b.RefFrame1 == Ref.Intra;
        if (!compound && !interintra)
        {
            for (int i = 0; i < h; i++)
            {
                for (int j = 0; j < w; j++)
                {
                    dst[at + (i * stride) + j] = (ushort)Math.Clamp(_pred0[(i * w) + j], 0, max);
                }
            }
        }
        else if (compound && b.CompoundType == Compound.Average)
        {
            int shift = 1 + postRound;
            for (int i = 0; i < h; i++)
            {
                for (int j = 0; j < w; j++)
                {
                    int v = _pred0[(i * w) + j] + _pred1[(i * w) + j];
                    dst[at + (i * stride) + j] = (ushort)Math.Clamp((v + (1 << (shift - 1))) >> shift, 0, max);
                }
            }
        }
        else if (compound && b.CompoundType == Compound.Distance)
        {
            (int fwd, int bck) = DistanceWeights(cand);
            int shift = 4 + postRound;
            for (int i = 0; i < h; i++)
            {
                for (int j = 0; j < w; j++)
                {
                    int v = (fwd * _pred0[(i * w) + j]) + (bck * _pred1[(i * w) + j]);
                    dst[at + (i * stride) + j] = (ushort)Math.Clamp((v + (1 << (shift - 1))) >> shift, 0, max);
                }
            }
        }
        else
        {
            bool wedgeInterintra = interintra && b.UseWedgeInterintra;
            if (plane == 0 && (b.CompoundType == Compound.Wedge || wedgeInterintra))
            {
                WedgeMask(b.Size, wedgeInterintra ? 0 : b.WedgeSign, wedgeInterintra ? b.InterintraWedgeIndex : b.WedgeIndex, w, h);
            }
            else if (interintra && !wedgeInterintra)
            {
                IntraVariantMask(b.InterintraMode, w, h);
            }
            else if (plane == 0 && b.CompoundType == Compound.DiffWeighted)
            {
                DifferenceMask(b.MaskType, w, h, postRound);
            }

            MaskBlend(interintra, wedgeInterintra, plane, dst, at, stride, w, h, postRound, max);
        }

        if (b.MotionMode == 1)
        {
            Overlap(tile, plane, w, h);
        }
    }

    /// <summary>Gets whether a reference frame's size differs from the frame's (is_scaled).</summary>
    private bool IsScaled(Av1Frame reference)
    {
        Av1FrameHeader h = _frame.Header;
        int xScale = ((reference.Header.UpscaledWidth << 14) + (h.FrameWidth / 2)) / h.FrameWidth;
        int yScale = ((reference.Header.FrameHeight << 14) + (h.FrameHeight / 2)) / h.FrameHeight;
        return xScale != 1 << 14 || yScale != 1 << 14;
    }

    /// <summary>The motion vector scaling process: the block's position in the reference and its steps, in 1/1024 samples.</summary>
    private (int StartX, int StartY, int StepX, int StepY) Scale(Av1Frame reference, int plane, int x, int y, int mvRow, int mvCol)
    {
        Av1FrameHeader h = _frame.Header;
        int ssx = plane > 0 ? _ssx : 0, ssy = plane > 0 ? _ssy : 0;
        int xScale = ((reference.Header.UpscaledWidth << 14) + (h.FrameWidth / 2)) / h.FrameWidth;
        int yScale = ((reference.Header.FrameHeight << 14) + (h.FrameHeight / 2)) / h.FrameHeight;
        const int halfSample = 1 << 3;
        int origX = (x << 4) + ((2 * mvCol) >> ssx) + halfSample;
        int origY = (y << 4) + ((2 * mvRow) >> ssy) + halfSample;
        long baseX = ((long)origX * xScale) - (halfSample << 14);
        long baseY = ((long)origY * yScale) - (halfSample << 14);
        const int off = (1 << (10 - 4)) / 2;
        int startX = (int)Round2Signed(baseX, 14 + 4 - 10) + off;
        int startY = (int)Round2Signed(baseY, 14 + 4 - 10) + off;
        return (startX, startY, (int)Round2Signed(xScale, 14 - 10), (int)Round2Signed(yScale, 14 - 10));

        static long Round2Signed(long v, int n) => v < 0 ? -((-v + (1L << (n - 1))) >> n) : (v + (1L << (n - 1))) >> n;
    }

    private static short[] Filters(int filter, int size) => filter switch
    {
        Interp.Smooth => size <= 4 ? Av1Tables.SubpelFilters4Smooth : Av1Tables.SubpelFilters8Smooth,
        Interp.Sharp => size <= 4 ? Av1Tables.SubpelFilters4 : Av1Tables.SubpelFilters8Sharp,
        Interp.Bilinear => Av1Tables.SubpelFiltersBilinear,
        _ => size <= 4 ? Av1Tables.SubpelFilters4 : Av1Tables.SubpelFilters8,
    };

    /// <summary>The block inter prediction process: two one-dimensional 8-tap passes over the reference, clamped at its edges.</summary>
    private void BlockInterPrediction(Av1Frame reference, int plane, int x, int y, int stepX, int stepY, int w, int h, int filterX, int filterY,
        int round0, int round1, int[] pred)
    {
        int ssx = plane > 0 ? _ssx : 0, ssy = plane > 0 ? _ssy : 0;
        int lastX = ((reference.Header.UpscaledWidth + ssx) >> ssx) - 1;
        int lastY = ((reference.Header.FrameHeight + ssy) >> ssy) - 1;
        ushort[] src = reference.Planes![plane];
        int stride = reference.Strides[plane];
        int interH = ((((h - 1) * stepY) + (1 << 10) - 1) >> 10) + 8;
        int[] inter = _intermediate;
        short[] fx = Filters(filterX, w), fy = Filters(filterY, h);
        int half0 = 1 << (round0 - 1), half1 = 1 << (round1 - 1);
        for (int r = 0; r < interH; r++)
        {
            int row = Math.Clamp((y >> 10) + r - 3, 0, lastY) * stride;
            for (int c = 0; c < w; c++)
            {
                int p = x + (stepX * c);
                int f = ((p >> 6) & 15) * 8;
                int col = (p >> 10) - 3;
                int s = 0;
                for (int t = 0; t < 8; t++)
                {
                    s += fx[f + t] * src[row + Math.Clamp(col + t, 0, lastX)];
                }

                inter[(r * w) + c] = (s + half0) >> round0;
            }
        }

        for (int r = 0; r < h; r++)
        {
            int p = (y & 1023) + (stepY * r);
            int f = ((p >> 6) & 15) * 8;
            int baseRow = (p >> 10) * w;
            for (int c = 0; c < w; c++)
            {
                int s = 0;
                for (int t = 0; t < 8; t++)
                {
                    s += fy[f + t] * inter[baseRow + (t * w) + c];
                }

                pred[(r * w) + c] = (s + half1) >> round1;
            }
        }
    }

    /// <summary>The distance weights process: the two predictions' weights from the references' distances.</summary>
    private (int Fwd, int Bck) DistanceWeights(Av1BlockInfo cand)
    {
        Av1FrameHeader h = _frame.Header;
        int d1 = Math.Clamp(Math.Abs(h.RelativeDist(h.OrderHints[cand.RefFrame0], h.OrderHint)), 0, 31);
        int d0 = Math.Clamp(Math.Abs(h.RelativeDist(h.OrderHints[cand.RefFrame1], h.OrderHint)), 0, 31);
        int order = d0 <= d1 ? 1 : 0;
        byte[] weight = Av1Tables.QuantDistWeight, lookup = Av1Tables.QuantDistLookupTable;
        if (d0 == 0 || d1 == 0)
        {
            return (lookup[(3 * 2) + order], lookup[(3 * 2) + 1 - order]);
        }

        int i = 0;
        for (; i < 3; i++)
        {
            int c0 = weight[(i * 2) + order], c1 = weight[(i * 2) + 1 - order];
            if (order != 0 ? d0 * c0 > d1 * c1 : d0 * c0 < d1 * c1)
            {
                break;
            }
        }

        return (lookup[(i * 2) + order], lookup[(i * 2) + 1 - order]);
    }

    private void WedgeMask(int bsize, int sign, int index, int w, int h)
    {
        byte[] mask = WedgeMasks()[(((bsize * 2) + sign) * 16) + index]!;
        mask.AsSpan(0, w * h).CopyTo(_mask);
        _maskStride = w;
    }

    private void DifferenceMask(int maskType, int w, int h, int postRound)
    {
        int shift = _bitDepth - 8 + postRound;
        for (int i = 0; i < h; i++)
        {
            for (int j = 0; j < w; j++)
            {
                int diff = Math.Abs(_pred0[(i * w) + j] - _pred1[(i * w) + j]);
                diff = shift == 0 ? diff : (diff + (1 << (shift - 1))) >> shift;
                int m = Math.Clamp(38 + (diff / 16), 0, 64);
                _mask[(i * w) + j] = (byte)(maskType != 0 ? 64 - m : m);
            }
        }

        _maskStride = w;
    }

    private void IntraVariantMask(int mode, int w, int h)
    {
        int sizeScale = 128 / Math.Max(h, w);
        byte[] weights = Av1Tables.IiWeights1d;
        for (int i = 0; i < h; i++)
        {
            for (int j = 0; j < w; j++)
            {
                _mask[(i * w) + j] = mode switch
                {
                    1 => weights[i * sizeScale],
                    2 => weights[j * sizeScale],
                    3 => weights[Math.Min(i, j) * sizeScale],
                    _ => 32,
                };
            }
        }

        _maskStride = w;
    }

    /// <summary>The mask blend process: the two predictions, or the inter and intra ones, by the mask (subsampled for chroma).</summary>
    private void MaskBlend(bool interintra, bool wedgeInterintra, int plane, ushort[] dst, int at, int stride, int w, int h, int postRound, int max)
    {
        int ssx = plane > 0 ? _ssx : 0, ssy = plane > 0 ? _ssy : 0;
        int ms = _maskStride;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int m;
                if ((ssx == 0 && ssy == 0) || (interintra && !wedgeInterintra))
                {
                    m = _mask[(y * ms) + x];
                }
                else if (ssx != 0 && ssy == 0)
                {
                    m = (_mask[(y * ms) + (2 * x)] + _mask[(y * ms) + (2 * x) + 1] + 1) >> 1;
                }
                else
                {
                    m = (_mask[(2 * y * ms) + (2 * x)] + _mask[(2 * y * ms) + (2 * x) + 1] + _mask[(((2 * y) + 1) * ms) + (2 * x)]
                        + _mask[(((2 * y) + 1) * ms) + (2 * x) + 1] + 2) >> 2;
                }

                int o = at + (y * stride) + x;
                if (interintra)
                {
                    int p0 = postRound == 0 ? _pred0[(y * w) + x] : (_pred0[(y * w) + x] + (1 << (postRound - 1))) >> postRound;
                    p0 = Math.Clamp(p0, 0, max);
                    dst[o] = (ushort)(((m * dst[o]) + ((64 - m) * p0) + 32) >> 6);
                }
                else
                {
                    int v = (m * _pred0[(y * w) + x]) + ((64 - m) * _pred1[(y * w) + x]);
                    int shift = 6 + postRound;
                    dst[o] = (ushort)Math.Clamp((v + (1 << (shift - 1))) >> shift, 0, max);
                }
            }
        }
    }

    /// <summary>The overlapped motion compensation process: blends predictions from the vectors of the blocks above and left.</summary>
    private void Overlap(Av1TileState tile, int plane, int w, int h)
    {
        Av1BlockInfo b = tile.Block;
        Av1Frame frame = _frame;
        int ssx = plane > 0 ? _ssx : 0, ssy = plane > 0 ? _ssy : 0;
        int round0 = _bitDepth == 12 ? 5 : 3, round1 = _bitDepth == 12 ? 9 : 11;
        int max = (1 << _bitDepth) - 1;
        ushort[] dst = frame.Planes![plane];
        int stride = frame.Strides[plane];
        if (tile.UpAvailable(0) && Av1Tables.PlaneBlockSize(b.Size, ssx, ssy) >= Bs.B8x8)
        {
            int w4 = Av1Tables.MiSizeWide[b.Size];
            int x4 = tile.MiCol, count = 0;
            int limit = Math.Min(4, (int)Av1Tables.MiSizeWideLog2[b.Size]);
            while (count < limit && x4 < Math.Min(frame.MiCols, tile.MiCol + w4))
            {
                int candRow = tile.MiRow - 1, candCol = x4 | 1;
                Av1BlockInfo cand = frame.Grid[(candRow * frame.MiCols) + candCol]!;
                int step4 = Math.Clamp((int)Av1Tables.MiSizeWide[cand.Size], 2, 16);
                if (cand.RefFrame0 > Ref.Intra)
                {
                    count++;
                    int predW = Math.Min(w, (step4 * 4) >> ssx), predH = Math.Min(h >> 1, 32 >> ssy);
                    OverlapPredict(cand, plane, (x4 * 4) >> ssx, (tile.MiRow * 4) >> ssy, predW, predH, round0, round1);
                    byte[] mask = ObmcMask(predH);
                    int at = ((((tile.MiRow * 4) >> ssy) * stride) + ((x4 * 4) >> ssx));
                    for (int i = 0; i < predH; i++)
                    {
                        int m = mask[i];
                        for (int j = 0; j < predW; j++)
                        {
                            int o = at + (i * stride) + j;
                            int p = Math.Clamp(_pred0[(i * predW) + j], 0, max);
                            dst[o] = (ushort)(((m * dst[o]) + ((64 - m) * p) + 32) >> 6);
                        }
                    }
                }

                x4 += step4;
            }
        }

        if (tile.LeftAvailable(0))
        {
            int h4 = Av1Tables.MiSizeHigh[b.Size];
            int y4 = tile.MiRow, count = 0;
            int limit = Math.Min(4, (int)Av1Tables.MiSizeHighLog2[b.Size]);
            while (count < limit && y4 < Math.Min(frame.MiRows, tile.MiRow + h4))
            {
                int candCol = tile.MiCol - 1, candRow = y4 | 1;
                Av1BlockInfo cand = frame.Grid[(candRow * frame.MiCols) + candCol]!;
                int step4 = Math.Clamp((int)Av1Tables.MiSizeHigh[cand.Size], 2, 16);
                if (cand.RefFrame0 > Ref.Intra)
                {
                    count++;
                    int predW = Math.Min(w >> 1, 32 >> ssx), predH = Math.Min(h, (step4 * 4) >> ssy);
                    OverlapPredict(cand, plane, (tile.MiCol * 4) >> ssx, (y4 * 4) >> ssy, predW, predH, round0, round1);
                    byte[] mask = ObmcMask(predW);
                    int at = ((((y4 * 4) >> ssy) * stride) + ((tile.MiCol * 4) >> ssx));
                    for (int i = 0; i < predH; i++)
                    {
                        for (int j = 0; j < predW; j++)
                        {
                            int m = mask[j];
                            int o = at + (i * stride) + j;
                            int p = Math.Clamp(_pred0[(i * predW) + j], 0, max);
                            dst[o] = (ushort)(((m * dst[o]) + ((64 - m) * p) + 32) >> 6);
                        }
                    }
                }

                y4 += step4;
            }
        }
    }

    private void OverlapPredict(Av1BlockInfo cand, int plane, int predX, int predY, int predW, int predH, int round0, int round1)
    {
        Av1Frame reference = _frame.Header.RefFrames[cand.RefFrame0]!;
        (int startX, int startY, int stepX, int stepY) = Scale(reference, plane, predX, predY, cand.MvRow, cand.MvCol);
        BlockInterPrediction(reference, plane, startX, startY, stepX, stepY, predW, predH, cand.FilterX, cand.FilterY, round0, round1, _pred0);
    }

    private static byte[] ObmcMask(int length) => length switch
    {
        2 => Av1Tables.ObmcMask2,
        4 => Av1Tables.ObmcMask4,
        8 => Av1Tables.ObmcMask8,
        16 => Av1Tables.ObmcMask16,
        _ => Av1Tables.ObmcMask32,
    };

    /// <summary>The wedge masks of every block size, sign and wedge, from the master templates (initialise_wedge_mask_table).</summary>
    private static byte[]?[] WedgeMasks()
    {
        byte[]?[]? masks = Volatile.Read(ref s_wedgeMasks);
        if (masks is not null)
        {
            return masks;
        }

        ReadOnlySpan<byte> obliqueOdd =
        [
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 6, 18,
            37, 53, 60, 63, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64,
        ];
        ReadOnlySpan<byte> obliqueEven =
        [
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 4, 11, 27,
            46, 58, 62, 63, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64,
        ];
        ReadOnlySpan<byte> vertical =
        [
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 7, 21,
            43, 57, 62, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64,
        ];
        const int size = 64;
        const int horizontal = 0, verticalDir = 1, oblique27 = 2, oblique63 = 3, oblique117 = 4, oblique153 = 5;
        byte[][] master = [.. Enumerable.Range(0, 6).Select(_ => new byte[size * size])];
        for (int j = 0; j < size; j++)
        {
            int shift = size / 4;
            for (int i = 0; i < size; i += 2)
            {
                master[oblique63][(i * size) + j] = obliqueEven[Math.Clamp(j - shift, 0, size - 1)];
                shift--;
                master[oblique63][((i + 1) * size) + j] = obliqueOdd[Math.Clamp(j - shift, 0, size - 1)];
                master[verticalDir][(i * size) + j] = vertical[j];
                master[verticalDir][((i + 1) * size) + j] = vertical[j];
            }
        }

        for (int i = 0; i < size; i++)
        {
            for (int j = 0; j < size; j++)
            {
                byte msk = master[oblique63][(i * size) + j];
                master[oblique27][(j * size) + i] = msk;
                master[oblique117][(i * size) + size - 1 - j] = (byte)(64 - msk);
                master[oblique153][((size - 1 - j) * size) + i] = (byte)(64 - msk);
                master[horizontal][(j * size) + i] = master[verticalDir][(i * size) + j];
            }
        }

        masks = new byte[]?[22 * 2 * 16];
        for (int bsize = Bs.B8x8; bsize < 22; bsize++)
        {
            if (!(bsize is Bs.B8x8 or Bs.B8x16 or Bs.B16x8 or Bs.B16x16 or Bs.B16x32 or Bs.B32x16 or Bs.B32x32 or Bs.B8x32 or Bs.B32x8))
            {
                continue;
            }

            int w = Av1Tables.BlockSizeWide[bsize], h = Av1Tables.BlockSizeHigh[bsize];
            int w4 = Av1Tables.MiSizeWide[bsize], h4 = Av1Tables.MiSizeHigh[bsize];
            byte[] codebook = h4 > w4 ? Av1Tables.WedgeCodebook16Hgtw : h4 < w4 ? Av1Tables.WedgeCodebook16Hltw : Av1Tables.WedgeCodebook16Heqw;
            for (int wedge = 0; wedge < 16; wedge++)
            {
                int dir = codebook[wedge * 3];
                int xoff = (size / 2) - ((codebook[(wedge * 3) + 1] * w) >> 3);
                int yoff = (size / 2) - ((codebook[(wedge * 3) + 2] * h) >> 3);
                int sum = 0;
                for (int i = 0; i < w; i++)
                {
                    sum += master[dir][(yoff * size) + xoff + i];
                }

                for (int i = 1; i < h; i++)
                {
                    sum += master[dir][((yoff + i) * size) + xoff];
                }

                int avg = (sum + ((w + h - 1) / 2)) / (w + h - 1);
                int flipSign = avg < 32 ? 1 : 0;
                byte[] a = new byte[w * h], b = new byte[w * h];
                for (int i = 0; i < h; i++)
                {
                    for (int j = 0; j < w; j++)
                    {
                        a[(i * w) + j] = master[dir][((yoff + i) * size) + xoff + j];
                        b[(i * w) + j] = (byte)(64 - a[(i * w) + j]);
                    }
                }

                masks[(((bsize * 2) + flipSign) * 16) + wedge] = a;
                masks[(((bsize * 2) + (1 - flipSign)) * 16) + wedge] = b;
            }
        }

        Volatile.Write(ref s_wedgeMasks, masks);
        return masks;
    }
}
