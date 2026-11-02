using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Formats.Av1;

/// <summary>Intra prediction: av1_predict_intra_block and the high bit depth predictors of reconintra.c and intrapred.c.</summary>
internal sealed partial class Av1Reconstructor<T>
{
    private const int NeedLeft = 1, NeedAbove = 2, NeedAboveRight = 4, NeedAboveLeft = 8, NeedBottomLeft = 16;
    private const int EdgeBase = 16;

    private static ReadOnlySpan<byte> ExtendModes =>
    [
        NeedAbove | NeedLeft, NeedAbove, NeedLeft, NeedAbove | NeedAboveRight, NeedLeft | NeedAbove | NeedAboveLeft,
        NeedLeft | NeedAbove | NeedAboveLeft, NeedLeft | NeedAbove | NeedAboveLeft, NeedLeft | NeedBottomLeft,
        NeedAbove | NeedAboveRight, NeedLeft | NeedAbove, NeedLeft | NeedAbove, NeedLeft | NeedAbove, NeedLeft | NeedAbove | NeedAboveLeft,
    ];

    private static ReadOnlySpan<byte> ModeToAngle => [0, 90, 180, 45, 135, 113, 157, 203, 67, 0, 0, 0, 0];

    private readonly T[] _aboveData = new T[(64 * 2) + 32 + EdgeBase];
    private readonly T[] _leftData = new T[(64 * 2) + 32 + EdgeBase];
    private readonly int[] _filterIntra = new int[33 * 33];

    private void PredictIntra(Av1TileState tile, int plane, int colOff, int rowOff, int txSize, int mode, int angleDelta, bool palette,
        int filterMode, T[] buf, int offset, int stride)
    {
        Av1BlockInfo b = tile.Block;
        int txw = Av1Tables.TxSizeWide[txSize], txh = Av1Tables.TxSizeHigh[txSize];
        int x = colOff << 2, y = rowOff << 2;
        int ssx = plane > 0 ? _ssx : 0, ssy = plane > 0 ? _ssy : 0;
        int wpx = Math.Max((tile.BlockWidth4 * 4) >> ssx, 4), hpx = Math.Max((tile.BlockHeight4 * 4) >> ssy, 4);
        if (palette)
        {
            byte[] map = tile.ColorMaps[plane > 0 ? 1 : 0];
            int colors = plane * 8;
            for (int r = 0; r < txh; r++)
            {
                for (int c = 0; c < txw; c++)
                {
                    buf[offset + (r * stride) + c] = Av1Samples.From<T>(b.PaletteColors[colors + map[((r + y) * wpx) + c + x]]);
                }
            }

            return;
        }

        bool haveTop = rowOff != 0 || tile.UpAvailable(plane);
        bool haveLeft = colOff != 0 || tile.LeftAvailable(plane);
        int xr = (tile.ToRightEdge >> (3 + ssx)) + wpx - x - txw;
        int yd = (tile.ToBottomEdge >> (3 + ssy)) + hpx - y - txh;
        bool useFilter = filterMode >= 0;
        bool directional = Pred.IsDirectional(mode);
        int nTop = haveTop ? Math.Min(txw, xr + txw) : 0;
        int nLeft = haveLeft ? Math.Min(txh, yd + txh) : 0;
        if (!useFilter && !directional)
        {
            NonDirectional(buf, offset, stride, mode, txw, txh, nTop, nLeft);
            return;
        }

        int txw4 = Av1Tables.TxSizeWideUnit[txSize], txh4 = Av1Tables.TxSizeHighUnit[txSize];
        int miRow = tile.MiRow, miCol = tile.MiCol;
        bool rightAvailable = miCol + ((colOff + txw4) << ssx) < tile.MiColEnd;
        bool bottomAvailable = yd > 0 && miRow + ((rowOff + txh4) << ssy) < tile.MiRowEnd;
        int bsize = ssx != 0 || ssy != 0 ? ScaleChromaBlockSize(b.Size, ssx, ssy) : b.Size;
        int angle = 0;
        bool needTopRight = (ExtendModes[mode] & NeedAboveRight) != 0, needBottomLeft = (ExtendModes[mode] & NeedBottomLeft) != 0;
        if (useFilter)
        {
            needTopRight = needBottomLeft = false;
        }

        if (directional)
        {
            angle = ModeToAngle[mode] + angleDelta;
            needTopRight = angle < 90;
            needBottomLeft = angle > 180;
        }

        int haveTopRight = needTopRight ? (HasTopRight(bsize, miRow, miCol, haveTop, rightAvailable, b.Partition, txSize, rowOff, colOff, ssx, ssy) ? 1 : 0) : -1;
        int haveBottomLeft = needBottomLeft ? (HasBottomLeft(bsize, miRow, miCol, bottomAvailable, haveLeft, b.Partition, txSize, rowOff, colOff, ssx, ssy) ? 1 : 0) : -1;
        int nTopRight = haveTopRight > 0 ? Math.Min(txw, xr) : haveTopRight;
        int nBottomLeft = haveBottomLeft > 0 ? Math.Min(txh, yd) : haveBottomLeft;
        bool smoothNeighbour = IsSmooth(tile.AboveBlock(plane), plane) || IsSmooth(tile.LeftBlock(plane), plane);
        DirectionalAndFilter(buf, offset, stride, mode, angle, filterMode, txw, txh, !_frame.Sequence.EnableIntraEdgeFilter, nTop, nTopRight,
            nLeft, nBottomLeft, smoothNeighbour ? 1 : 0);
    }

    private static bool IsSmooth(Av1BlockInfo? b, int plane)
    {
        if (b is null)
        {
            return false;
        }

        if (plane == 0)
        {
            return b.YMode is Pred.Smooth or Pred.SmoothV or Pred.SmoothH;
        }

        return !b.IsInter && b.UvMode is Pred.Smooth or Pred.SmoothV or Pred.SmoothH;
    }

    private static int ScaleChromaBlockSize(int bsize, int ssx, int ssy) => bsize switch
    {
        Bs.B4x4 => ssx == 1 && ssy == 1 ? Bs.B8x8 : ssx == 1 ? Bs.B8x4 : ssy == 1 ? Bs.B4x8 : bsize,
        Bs.B4x8 => ssx == 1 ? Bs.B8x8 : ssy == 1 ? Bs.B4x8 : bsize,
        Bs.B8x4 => ssy == 1 ? Bs.B8x8 : ssx == 1 ? Bs.B8x4 : bsize,
        Bs.B4x16 => ssx == 1 ? Bs.B8x16 : ssy == 1 ? Bs.B4x16 : bsize,
        Bs.B16x4 => ssy == 1 ? Bs.B16x8 : ssx == 1 ? Bs.B16x4 : bsize,
        _ => bsize,
    };

    private bool HasTopRight(int bsize, int miRow, int miCol, bool topAvailable, bool rightAvailable, int partition, int txSize,
        int rowOff, int colOff, int ssx, int ssy)
    {
        if (!topAvailable || !rightAvailable)
        {
            return false;
        }

        int bwUnit = Av1Tables.MiSizeWide[bsize];
        int planeBwUnit = Math.Max(bwUnit >> ssx, 1);
        int topRightCount = Av1Tables.TxSizeWideUnit[txSize];
        if (rowOff > 0)
        {
            if (Av1Tables.BlockSizeWide[bsize] > 64)
            {
                // The transform unit whose top right corner is at the centre of a 128-wide block has its top right.
                if (rowOff == 16 >> ssy && colOff + topRightCount == 16 >> ssx)
                {
                    return true;
                }

                int planeBwUnit64 = 16 >> ssx;
                return (colOff % planeBwUnit64) + topRightCount < planeBwUnit64;
            }

            return colOff + topRightCount < planeBwUnit;
        }

        if (colOff + topRightCount < planeBwUnit)
        {
            return true;
        }

        int bwLog2 = Av1Tables.MiSizeWideLog2[bsize], bhLog2 = Av1Tables.MiSizeHighLog2[bsize];
        int sbMi = 1 << _frame.SbMiLog2;
        int blkRowInSb = (miRow & (sbMi - 1)) >> bhLog2, blkColInSb = (miCol & (sbMi - 1)) >> bwLog2;
        if (blkRowInSb == 0)
        {
            return true;
        }

        if (((blkColInSb + 1) << bwLog2) >= sbMi)
        {
            return false;
        }

        int index = (blkRowInSb << (5 - bwLog2)) + blkColInSb;
        byte[] table = (partition is Partition.VertA or Partition.VertB ? Av1Tables.HasTrVertTables[bsize] : Av1Tables.HasTrTables[bsize])!;
        return ((table[index / 8] >> (index % 8)) & 1) != 0;
    }

    private bool HasBottomLeft(int bsize, int miRow, int miCol, bool bottomAvailable, bool leftAvailable, int partition, int txSize,
        int rowOff, int colOff, int ssx, int ssy)
    {
        if (!bottomAvailable || !leftAvailable)
        {
            return false;
        }

        if (Av1Tables.BlockSizeWide[bsize] > 64 && colOff > 0)
        {
            int planeBwUnit64 = 16 >> ssx;
            if (colOff % planeBwUnit64 == 0)
            {
                int planeBhUnit64 = 16 >> ssy;
                int planeBhUnit = Math.Min(Av1Tables.MiSizeHigh[bsize] >> ssy, planeBhUnit64);
                return (rowOff % planeBhUnit64) + Av1Tables.TxSizeHighUnit[txSize] < planeBhUnit;
            }
        }

        if (colOff > 0)
        {
            return false;
        }

        int planeBh = Math.Max(Av1Tables.MiSizeHigh[bsize] >> ssy, 1);
        int bottomLeftCount = Av1Tables.TxSizeHighUnit[txSize];
        if (rowOff + bottomLeftCount < planeBh)
        {
            return true;
        }

        int bwLog2 = Av1Tables.MiSizeWideLog2[bsize], bhLog2 = Av1Tables.MiSizeHighLog2[bsize];
        int sbMi = 1 << _frame.SbMiLog2;
        int blkRowInSb = (miRow & (sbMi - 1)) >> bhLog2, blkColInSb = (miCol & (sbMi - 1)) >> bwLog2;
        if (blkColInSb == 0)
        {
            int blkStartRowOff = (blkRowInSb << bhLog2) >> ssy;
            return blkStartRowOff + rowOff + bottomLeftCount < sbMi >> ssy;
        }

        if (((blkRowInSb + 1) << bhLog2) >= sbMi)
        {
            return false;
        }

        int index = (blkRowInSb << (5 - bwLog2)) + blkColInSb;
        byte[] table = (partition is Partition.VertA or Partition.VertB ? Av1Tables.HasBlVertTables[bsize] : Av1Tables.HasBlTables[bsize])!;
        return ((table[index / 8] >> (index % 8)) & 1) != 0;
    }

    private void NonDirectional(T[] buf, int offset, int stride, int mode, int txw, int txh, int nTop, int nLeft)
    {
        int baseValue = 128 << (_bitDepth - 8);
        int extend = ExtendModes[mode];
        bool needLeft = (extend & NeedLeft) != 0, needAbove = (extend & NeedAbove) != 0, needAboveLeft = (extend & NeedAboveLeft) != 0;
        int aboveRef = offset - stride, leftRef = offset - 1;
        if ((!needAbove && nLeft == 0) || (!needLeft && nTop == 0))
        {
            int value = needLeft ? (nTop > 0 ? Av1Samples.Get(buf[aboveRef]) : baseValue + 1) : (nLeft > 0 ? Av1Samples.Get(buf[leftRef]) : baseValue - 1);
            Fill(buf, offset, stride, txw, txh, value);
            return;
        }

        T[] above = _aboveData, left = _leftData;
        if (needLeft)
        {
            // These modes read the edges' first txh (or txw) samples only.
            left.AsSpan(EdgeBase, txh).Fill(Av1Samples.From<T>(baseValue + 1));
            if (nLeft > 0)
            {
                int i = 0;
                for (; i < nLeft; i++)
                {
                    left[EdgeBase + i] = buf[leftRef + (i * stride)];
                }

                if (i < txh)
                {
                    left.AsSpan(EdgeBase + i, txh - i).Fill(left[EdgeBase + i - 1]);
                }
            }
            else if (nTop > 0)
            {
                left.AsSpan(EdgeBase, txh).Fill(buf[aboveRef]);
            }
        }

        if (needAbove)
        {
            above.AsSpan(EdgeBase, txw).Fill(Av1Samples.From<T>(baseValue - 1));
            if (nTop > 0)
            {
                buf.AsSpan(aboveRef, nTop).CopyTo(above.AsSpan(EdgeBase));
                if (nTop < txw)
                {
                    above.AsSpan(EdgeBase + nTop, txw - nTop).Fill(above[EdgeBase + nTop - 1]);
                }
            }
            else if (nLeft > 0)
            {
                above.AsSpan(EdgeBase, txw).Fill(buf[leftRef]);
            }
        }

        if (needAboveLeft)
        {
            above[EdgeBase - 1] = nTop > 0 && nLeft > 0 ? buf[aboveRef - 1] : nTop > 0 ? buf[aboveRef] : nLeft > 0 ? buf[leftRef] : Av1Samples.From<T>(baseValue);
            left[EdgeBase - 1] = above[EdgeBase - 1];
        }

        switch (mode)
        {
            case Pred.Dc:
                PredictDc(buf, offset, stride, txw, txh, nTop > 0, nLeft > 0);
                break;
            case Pred.Smooth:
                PredictSmooth(buf, offset, stride, txw, txh);
                break;
            case Pred.SmoothV:
                PredictSmoothV(buf, offset, stride, txw, txh);
                break;
            case Pred.SmoothH:
                PredictSmoothH(buf, offset, stride, txw, txh);
                break;
            default:
                PredictPaeth(buf, offset, stride, txw, txh);
                break;
        }
    }

    private static void Fill(T[] buf, int offset, int stride, int width, int height, int value)
    {
        for (int r = 0; r < height; r++)
        {
            buf.AsSpan(offset + (r * stride), width).Fill(Av1Samples.From<T>(value));
        }
    }

    private void PredictDc(T[] buf, int offset, int stride, int w, int h, bool haveAbove, bool haveLeft)
    {
        int value;
        if (!haveAbove && !haveLeft)
        {
            value = 128 << (_bitDepth - 8);
        }
        else
        {
            int sum = 0, count = 0;
            if (haveAbove)
            {
                for (int i = 0; i < w; i++)
                {
                    sum += Av1Samples.Get(_aboveData[EdgeBase + i]);
                }

                count += w;
            }

            if (haveLeft)
            {
                for (int i = 0; i < h; i++)
                {
                    sum += Av1Samples.Get(_leftData[EdgeBase + i]);
                }

                count += h;
            }

            value = (sum + (count >> 1)) / count;
        }

        Fill(buf, offset, stride, w, h, value);
    }

    private void PredictSmooth(T[] buf, int offset, int stride, int w, int h)
    {
        int below = Av1Samples.Get(_leftData[EdgeBase + h - 1]), right = Av1Samples.Get(_aboveData[EdgeBase + w - 1]);
        ReadOnlySpan<byte> weightsW = Av1Tables.SmoothWeights.AsSpan(w - 4, w), weightsH = Av1Tables.SmoothWeights.AsSpan(h - 4, h);
        for (int r = 0; r < h; r++)
        {
            for (int c = 0; c < w; c++)
            {
                int pred = (weightsH[r] * Av1Samples.Get(_aboveData[EdgeBase + c])) + ((256 - weightsH[r]) * below)
                    + (weightsW[c] * Av1Samples.Get(_leftData[EdgeBase + r])) + ((256 - weightsW[c]) * right);
                buf[offset + (r * stride) + c] = Av1Samples.From<T>((pred + 256) >> 9);
            }
        }
    }

    private void PredictSmoothV(T[] buf, int offset, int stride, int w, int h)
    {
        int below = Av1Samples.Get(_leftData[EdgeBase + h - 1]);
        ReadOnlySpan<byte> weights = Av1Tables.SmoothWeights.AsSpan(h - 4, h);
        for (int r = 0; r < h; r++)
        {
            for (int c = 0; c < w; c++)
            {
                int pred = (weights[r] * Av1Samples.Get(_aboveData[EdgeBase + c])) + ((256 - weights[r]) * below);
                buf[offset + (r * stride) + c] = Av1Samples.From<T>((pred + 128) >> 8);
            }
        }
    }

    private void PredictSmoothH(T[] buf, int offset, int stride, int w, int h)
    {
        int right = Av1Samples.Get(_aboveData[EdgeBase + w - 1]);
        ReadOnlySpan<byte> weights = Av1Tables.SmoothWeights.AsSpan(w - 4, w);
        for (int r = 0; r < h; r++)
        {
            for (int c = 0; c < w; c++)
            {
                int pred = (weights[c] * Av1Samples.Get(_leftData[EdgeBase + r])) + ((256 - weights[c]) * right);
                buf[offset + (r * stride) + c] = Av1Samples.From<T>((pred + 128) >> 8);
            }
        }
    }

    private void PredictPaeth(T[] buf, int offset, int stride, int w, int h)
    {
        int topLeft = Av1Samples.Get(_aboveData[EdgeBase - 1]);
        for (int r = 0; r < h; r++)
        {
            int left = Av1Samples.Get(_leftData[EdgeBase + r]);
            for (int c = 0; c < w; c++)
            {
                int top = Av1Samples.Get(_aboveData[EdgeBase + c]);
                int baseValue = top + left - topLeft;
                int pLeft = Math.Abs(baseValue - left), pTop = Math.Abs(baseValue - top), pTopLeft = Math.Abs(baseValue - topLeft);
                buf[offset + (r * stride) + c] = Av1Samples.From<T>(pLeft <= pTop && pLeft <= pTopLeft ? left : pTop <= pTopLeft ? top : topLeft);
            }
        }
    }

    private void DirectionalAndFilter(T[] buf, int offset, int stride, int mode, int angle, int filterMode, int txw, int txh,
        bool disableEdgeFilter, int nTop, int nTopRight, int nLeft, int nBottomLeft, int edgeFilterType)
    {
        int baseValue = 128 << (_bitDepth - 8);
        int extend = ExtendModes[mode];
        bool needLeft = (extend & NeedLeft) != 0, needAbove = (extend & NeedAbove) != 0, needAboveLeft = (extend & NeedAboveLeft) != 0;
        bool directional = Pred.IsDirectional(mode);
        bool useFilter = filterMode >= 0;
        int aboveRef = offset - stride, leftRef = offset - 1;
        T[] above = _aboveData, left = _leftData;
        left.AsSpan().Fill(Av1Samples.From<T>(baseValue + 1));
        above.AsSpan().Fill(Av1Samples.From<T>(baseValue - 1));
        if (directional)
        {
            needAbove = angle < 180;
            needLeft = angle > 90;
            needAboveLeft = true;
        }

        if (useFilter)
        {
            needLeft = needAbove = needAboveLeft = true;
        }

        if ((!needAbove && nLeft == 0) || (!needLeft && nTop == 0))
        {
            int value = needLeft ? (nTop > 0 ? Av1Samples.Get(buf[aboveRef]) : baseValue + 1) : (nLeft > 0 ? Av1Samples.Get(buf[leftRef]) : baseValue - 1);
            Fill(buf, offset, stride, txw, txh, value);
            return;
        }

        if (needLeft)
        {
            int needed = txh + (nBottomLeft >= 0 ? txw : 0);
            int i = 0;
            if (nLeft > 0)
            {
                for (; i < nLeft; i++)
                {
                    left[EdgeBase + i] = buf[leftRef + (i * stride)];
                }

                if (nBottomLeft > 0)
                {
                    for (; i < txh + nBottomLeft; i++)
                    {
                        left[EdgeBase + i] = buf[leftRef + (i * stride)];
                    }
                }

                if (i < needed)
                {
                    left.AsSpan(EdgeBase + i, needed - i).Fill(left[EdgeBase + i - 1]);
                }
            }
            else if (nTop > 0)
            {
                left.AsSpan(EdgeBase, needed).Fill(buf[aboveRef]);
            }
        }

        if (needAbove)
        {
            int needed = txw + (nTopRight >= 0 ? txh : 0);
            if (nTop > 0)
            {
                buf.AsSpan(aboveRef, nTop).CopyTo(above.AsSpan(EdgeBase));
                int i = nTop;
                if (nTopRight > 0)
                {
                    buf.AsSpan(aboveRef + txw, nTopRight).CopyTo(above.AsSpan(EdgeBase + txw));
                    i += nTopRight;
                }

                if (i < needed)
                {
                    above.AsSpan(EdgeBase + i, needed - i).Fill(above[EdgeBase + i - 1]);
                }
            }
            else if (nLeft > 0)
            {
                above.AsSpan(EdgeBase, needed).Fill(buf[leftRef]);
            }
        }

        if (needAboveLeft)
        {
            above[EdgeBase - 1] = nTop > 0 && nLeft > 0 ? buf[aboveRef - 1] : nTop > 0 ? buf[aboveRef] : nLeft > 0 ? buf[leftRef] : Av1Samples.From<T>(baseValue);
            left[EdgeBase - 1] = above[EdgeBase - 1];
        }

        if (useFilter)
        {
            FilterIntra(buf, offset, stride, txw, txh, filterMode);
            return;
        }

        int upsampleAbove = 0, upsampleLeft = 0;
        if (!disableEdgeFilter)
        {
            bool needRight = angle < 90, needBottom = angle > 180;
            if (angle != 90 && angle != 180)
            {
                if (needAbove && needLeft && txw + txh >= 24)
                {
                    int s = (Av1Samples.Get(left[EdgeBase]) * 5) + (Av1Samples.Get(above[EdgeBase - 1]) * 6) + (Av1Samples.Get(above[EdgeBase]) * 5);
                    above[EdgeBase - 1] = left[EdgeBase - 1] = Av1Samples.From<T>((s + 8) >> 4);
                }

                if (needAbove && nTop > 0)
                {
                    int strength = EdgeFilterStrength(txw, txh, angle - 90, edgeFilterType);
                    FilterEdge(above, EdgeBase - 1, nTop + 1 + (needRight ? txh : 0), strength);
                }

                if (needLeft && nLeft > 0)
                {
                    int strength = EdgeFilterStrength(txh, txw, angle - 180, edgeFilterType);
                    FilterEdge(left, EdgeBase - 1, nLeft + 1 + (needBottom ? txw : 0), strength);
                }
            }

            upsampleAbove = UseUpsample(txw, txh, angle - 90, edgeFilterType) ? 1 : 0;
            if (needAbove && upsampleAbove != 0)
            {
                UpsampleEdge(above, txw + (needRight ? txh : 0));
            }

            upsampleLeft = UseUpsample(txh, txw, angle - 180, edgeFilterType) ? 1 : 0;
            if (needLeft && upsampleLeft != 0)
            {
                UpsampleEdge(left, txh + (needBottom ? txw : 0));
            }
        }

        if (angle == 90)
        {
            for (int r = 0; r < txh; r++)
            {
                above.AsSpan(EdgeBase, txw).CopyTo(buf.AsSpan(offset + (r * stride)));
            }
        }
        else if (angle == 180)
        {
            for (int r = 0; r < txh; r++)
            {
                buf.AsSpan(offset + (r * stride), txw).Fill(left[EdgeBase + r]);
            }
        }
        else if (angle < 90)
        {
            DirectionalZone1(buf, offset, stride, txw, txh, upsampleAbove, Av1Tables.DrIntraDerivative[angle]);
        }
        else if (angle < 180)
        {
            DirectionalZone2(buf, offset, stride, txw, txh, upsampleAbove, upsampleLeft, Av1Tables.DrIntraDerivative[180 - angle], Av1Tables.DrIntraDerivative[angle - 90]);
        }
        else
        {
            DirectionalZone3(buf, offset, stride, txw, txh, upsampleLeft, Av1Tables.DrIntraDerivative[270 - angle]);
        }
    }

    private static int EdgeFilterStrength(int bs0, int bs1, int delta, int type)
    {
        int d = Math.Abs(delta);
        int wh = bs0 + bs1;
        if (type == 0)
        {
            return wh <= 8 ? (d >= 56 ? 1 : 0)
                : wh <= 16 ? (d >= 40 ? 1 : 0)
                : wh <= 24 ? (d >= 32 ? 3 : d >= 16 ? 2 : d >= 8 ? 1 : 0)
                : wh <= 32 ? (d >= 32 ? 3 : d >= 4 ? 2 : d >= 1 ? 1 : 0)
                : (d >= 1 ? 3 : 0);
        }

        return wh <= 8 ? (d >= 64 ? 2 : d >= 40 ? 1 : 0)
            : wh <= 16 ? (d >= 48 ? 2 : d >= 20 ? 1 : 0)
            : wh <= 24 ? (d >= 4 ? 3 : 0)
            : (d >= 1 ? 3 : 0);
    }

    private static bool UseUpsample(int bs0, int bs1, int delta, int type)
    {
        int d = Math.Abs(delta);
        int wh = bs0 + bs1;
        return d != 0 && d < 40 && (type != 0 ? wh <= 8 : wh <= 16);
    }

    private static void FilterEdge(T[] p, int start, int size, int strength)
    {
        if (strength == 0)
        {
            return;
        }

        ReadOnlySpan<int> kernel = strength switch
        {
            1 => [0, 4, 8, 4, 0],
            2 => [0, 5, 6, 5, 0],
            _ => [2, 4, 4, 4, 2],
        };
        Span<T> edge = stackalloc T[129 + 32];
        p.AsSpan(start, size).CopyTo(edge);
        for (int i = 1; i < size; i++)
        {
            int s = 0;
            for (int j = 0; j < 5; j++)
            {
                int k = Math.Clamp(i - 2 + j, 0, size - 1);
                s += Av1Samples.Get(edge[k]) * kernel[j];
            }

            p[start + i] = Av1Samples.From<T>((s + 8) >> 4);
        }
    }

    private void UpsampleEdge(T[] p, int size)
    {
        Span<int> input = stackalloc int[16 + 3];
        int max = (1 << _bitDepth) - 1;
        input[0] = input[1] = Av1Samples.Get(p[EdgeBase - 1]);
        for (int i = 0; i < size; i++)
        {
            input[i + 2] = Av1Samples.Get(p[EdgeBase + i]);
        }

        input[size + 2] = Av1Samples.Get(p[EdgeBase + size - 1]);
        p[EdgeBase - 2] = Av1Samples.From<T>(input[0]);
        for (int i = 0; i < size; i++)
        {
            int s = -input[i] + (9 * input[i + 1]) + (9 * input[i + 2]) - input[i + 3];
            p[EdgeBase + (2 * i) - 1] = Av1Samples.From<T>(Math.Clamp((s + 8) >> 4, 0, max));
            p[EdgeBase + (2 * i)] = Av1Samples.From<T>(input[i + 2]);
        }
    }

    private void DirectionalZone1(T[] buf, int offset, int stride, int bw, int bh, int upsample, int dx)
    {
        T[] above = _aboveData;
        int maxBaseX = (bw + bh - 1) << upsample;
        int fracBits = 6 - upsample, baseInc = 1 << upsample;
        int x = dx;
        for (int r = 0; r < bh; r++, x += dx)
        {
            int baseX = x >> fracBits;
            int shift = ((x << upsample) & 0x3F) >> 1;
            int row = offset + (r * stride);
            if (baseX >= maxBaseX)
            {
                for (int i = r; i < bh; i++)
                {
                    buf.AsSpan(offset + (i * stride), bw).Fill(above[EdgeBase + maxBaseX]);
                }

                return;
            }

            for (int c = 0; c < bw; c++, baseX += baseInc)
            {
                buf[row + c] = baseX < maxBaseX
                    ? Av1Samples.From<T>(((Av1Samples.Get(above[EdgeBase + baseX]) * (32 - shift)) + (Av1Samples.Get(above[EdgeBase + baseX + 1]) * shift) + 16) >> 5)
                    : above[EdgeBase + maxBaseX];
            }
        }
    }

    private void DirectionalZone2(T[] buf, int offset, int stride, int bw, int bh, int upsampleAbove, int upsampleLeft, int dx, int dy)
    {
        T[] above = _aboveData, left = _leftData;
        int minBaseX = -(1 << upsampleAbove);
        int fracBitsX = 6 - upsampleAbove, fracBitsY = 6 - upsampleLeft;
        for (int r = 0; r < bh; r++)
        {
            for (int c = 0; c < bw; c++)
            {
                int value;
                int y = r + 1;
                int x = (c << 6) - (y * dx);
                int baseX = x >> fracBitsX;
                if (baseX >= minBaseX)
                {
                    int shift = ((x * (1 << upsampleAbove)) & 0x3F) >> 1;
                    value = ((Av1Samples.Get(above[EdgeBase + baseX]) * (32 - shift)) + (Av1Samples.Get(above[EdgeBase + baseX + 1]) * shift) + 16) >> 5;
                }
                else
                {
                    x = c + 1;
                    y = (r << 6) - (x * dy);
                    int baseY = y >> fracBitsY;
                    int shift = ((y * (1 << upsampleLeft)) & 0x3F) >> 1;
                    value = ((Av1Samples.Get(left[EdgeBase + baseY]) * (32 - shift)) + (Av1Samples.Get(left[EdgeBase + baseY + 1]) * shift) + 16) >> 5;
                }

                buf[offset + (r * stride) + c] = Av1Samples.From<T>(value);
            }
        }
    }

    private void DirectionalZone3(T[] buf, int offset, int stride, int bw, int bh, int upsample, int dy)
    {
        T[] left = _leftData;
        int maxBaseY = (bw + bh - 1) << upsample;
        int fracBits = 6 - upsample, baseInc = 1 << upsample;
        int y = dy;
        for (int c = 0; c < bw; c++, y += dy)
        {
            int baseY = y >> fracBits;
            int shift = ((y << upsample) & 0x3F) >> 1;
            for (int r = 0; r < bh; r++, baseY += baseInc)
            {
                if (baseY < maxBaseY)
                {
                    buf[offset + (r * stride) + c] = Av1Samples.From<T>(((Av1Samples.Get(left[EdgeBase + baseY]) * (32 - shift)) + (Av1Samples.Get(left[EdgeBase + baseY + 1]) * shift) + 16) >> 5);
                }
                else
                {
                    for (; r < bh; r++)
                    {
                        buf[offset + (r * stride) + c] = left[EdgeBase + maxBaseY];
                    }

                    break;
                }
            }
        }
    }

    private void FilterIntra(T[] buf, int offset, int stride, int bw, int bh, int mode)
    {
        int[] buffer = _filterIntra;
        int max = (1 << _bitDepth) - 1;
        for (int r = 0; r < bh; r++)
        {
            buffer[(r + 1) * 33] = Av1Samples.Get(_leftData[EdgeBase + r]);
        }

        for (int c = 0; c <= bw; c++)
        {
            buffer[c] = Av1Samples.Get(_aboveData[EdgeBase - 1 + c]);
        }

        if (Avx2.IsSupported)
        {
            FilterIntraVector(buffer, bw, bh, mode, max);
        }
        else
        {
            ReadOnlySpan<sbyte> taps = Av1Tables.FilterIntraTaps.AsSpan(mode * 64, 64);
            for (int r = 1; r < bh + 1; r += 2)
            {
                for (int c = 1; c < bw + 1; c += 4)
                {
                    int p0 = buffer[((r - 1) * 33) + c - 1], p1 = buffer[((r - 1) * 33) + c], p2 = buffer[((r - 1) * 33) + c + 1];
                    int p3 = buffer[((r - 1) * 33) + c + 2], p4 = buffer[((r - 1) * 33) + c + 3];
                    int p5 = buffer[(r * 33) + c - 1], p6 = buffer[((r + 1) * 33) + c - 1];
                    for (int k = 0; k < 8; k++)
                    {
                        ReadOnlySpan<sbyte> t = taps.Slice(k * 8, 8);
                        int pr = (t[0] * p0) + (t[1] * p1) + (t[2] * p2) + (t[3] * p3) + (t[4] * p4) + (t[5] * p5) + (t[6] * p6);
                        int value = pr < 0 ? -((-pr + 8) >> 4) : (pr + 8) >> 4;
                        buffer[((r + (k >> 2)) * 33) + c + (k & 3)] = Math.Clamp(value, 0, max);
                    }
                }
            }
        }

        for (int r = 0; r < bh; r++)
        {
            int from = ((r + 1) * 33) + 1, to = offset + (r * stride);
            for (int c = 0; c < bw; c++)
            {
                buf[to + c] = Av1Samples.From<T>(buffer[from + c]);
            }
        }
    }

    /// <summary>
    /// The recursive filter's 4x2 cells with every output at once: each of the seven neighbours, paired for vpmaddwd, times its
    /// taps for the eight outputs, then the rounding away from zero and the clamp.
    /// </summary>
    private static void FilterIntraVector(int[] buffer, int bw, int bh, int mode, int max)
    {
        ReadOnlySpan<Vector256<short>> taps = FilterIntraPairs.AsSpan(mode * 4, 4);
        Vector256<short> t01 = taps[0], t23 = taps[1], t45 = taps[2], t6 = taps[3];
        Vector256<int> eight = Vector256.Create(8), maxV = Vector256.Create(max);
        ref int b0 = ref MemoryMarshal.GetArrayDataReference(buffer);
        for (int r = 1; r < bh + 1; r += 2)
        {
            for (int c = 1; c < bw + 1; c += 4)
            {
                ref int above = ref Unsafe.Add(ref b0, ((r - 1) * 33) + c - 1);
                Vector256<int> sum = Avx2.MultiplyAddAdjacent(Pair(above, Unsafe.Add(ref above, 1)), t01)
                    + Avx2.MultiplyAddAdjacent(Pair(Unsafe.Add(ref above, 2), Unsafe.Add(ref above, 3)), t23)
                    + Avx2.MultiplyAddAdjacent(Pair(Unsafe.Add(ref above, 4), Unsafe.Add(ref b0, (r * 33) + c - 1)), t45)
                    + Avx2.MultiplyAddAdjacent(Pair(Unsafe.Add(ref b0, ((r + 1) * 33) + c - 1), 0), t6);
                Vector256<int> sign = Vector256.ShiftRightArithmetic(sum, 31);
                Vector256<int> value = (Vector256.ShiftRightArithmetic(Vector256.Abs(sum) + eight, 4) ^ sign) - sign;
                value = Vector256.Min(Vector256.Max(value, Vector256<int>.Zero), maxV);
                value.GetLower().StoreUnsafe(ref b0, (nuint)((r * 33) + c));
                value.GetUpper().StoreUnsafe(ref b0, (nuint)(((r + 1) * 33) + c));
            }
        }

        static Vector256<short> Pair(int first, int second) => Vector256.Create(first | (second << 16)).AsInt16();
    }

    /// <summary>Each filter intra mode's taps for vpmaddwd: four vectors of the eight outputs' taps (0, 1), (2, 3), (4, 5) and (6, 0).</summary>
    private static readonly Vector256<short>[] FilterIntraPairs = MakeFilterIntraPairs();

    private static Vector256<short>[] MakeFilterIntraPairs()
    {
        sbyte[] taps = Av1Tables.FilterIntraTaps;
        Vector256<short>[] pairs = new Vector256<short>[(taps.Length / 64) * 4];
        Span<short> lanes = stackalloc short[16];
        for (int mode = 0; mode < taps.Length / 64; mode++)
        {
            for (int pair = 0; pair < 4; pair++)
            {
                for (int k = 0; k < 8; k++)
                {
                    lanes[2 * k] = taps[(mode * 64) + (k * 8) + (2 * pair)];
                    lanes[(2 * k) + 1] = pair == 3 ? (short)0 : taps[(mode * 64) + (k * 8) + (2 * pair) + 1];
                }

                pairs[(mode * 4) + pair] = Vector256.Create<short>(lanes);
            }
        }

        return pairs;
    }
}
