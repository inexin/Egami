using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// VP9's loop filter (vp9_loopfilter.c and vpx_dsp/loopfilter.c): edge masks built per 64x64 superblock as blocks decode, then
/// superblock by superblock, plane by plane, the vertical edges and then the horizontal ones, with 4-, 8- and 16-wide filters
/// chosen by transform size. The order of the edges is libvpx's, since each filter reads pixels earlier ones changed.
/// </summary>
internal sealed class Vp9LoopFilter<T>
    where T : unmanaged, IBinaryInteger<T>
{
    private const ulong LeftBorder = 0x1111111111111111UL;
    private const ulong AboveBorder = 0x000000ff000000ffUL;
    private const ushort LeftBorderUV = 0x1111;
    private const ushort AboveBorderUV = 0x000f;

    private static readonly byte[] FirstBlockIn16x16 =
    [
        1, 0, 1, 0, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 0, 1, 0, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    private readonly byte[] _lim = new byte[64];
    private readonly byte[] _mblim = new byte[64];
    private readonly byte[] _hevThr = new byte[64];
    private readonly byte[] _lvl = new byte[8 * 4 * 2];
    private Mask[] _masks = [];
    private int[] _rowProgress = [];
    private int _sbCols;
    private int _sharpness = -1;

    /// <summary>One superblock's masks (LOOP_FILTER_MASK): a bit per 8x8 (per 8x8 of chroma for 4:2:0) and transform size.</summary>
    private struct Mask
    {
        public ulong LeftY0, LeftY1, LeftY2, LeftY3;
        public ulong AboveY0, AboveY1, AboveY2, AboveY3;
        public ulong Int4x4Y;
        public ushort LeftUV0, LeftUV1, LeftUV2, LeftUV3;
        public ushort AboveUV0, AboveUV1, AboveUV2, AboveUV3;
        public ushort Int4x4UV;
        public LevelArray Lfl;

        [System.Diagnostics.CodeAnalysis.UnscopedRef]
        public ref ulong LeftY(int tx)
        {
            switch (tx)
            {
                case 0: return ref LeftY0;
                case 1: return ref LeftY1;
                case 2: return ref LeftY2;
                default: return ref LeftY3;
            }
        }

        [System.Diagnostics.CodeAnalysis.UnscopedRef]
        public ref ulong AboveY(int tx)
        {
            switch (tx)
            {
                case 0: return ref AboveY0;
                case 1: return ref AboveY1;
                case 2: return ref AboveY2;
                default: return ref AboveY3;
            }
        }

        [System.Diagnostics.CodeAnalysis.UnscopedRef]
        public ref ushort LeftUV(int tx)
        {
            switch (tx)
            {
                case 0: return ref LeftUV0;
                case 1: return ref LeftUV1;
                case 2: return ref LeftUV2;
                default: return ref LeftUV3;
            }
        }

        [System.Diagnostics.CodeAnalysis.UnscopedRef]
        public ref ushort AboveUV(int tx)
        {
            switch (tx)
            {
                case 0: return ref AboveUV0;
                case 1: return ref AboveUV1;
                case 2: return ref AboveUV2;
                default: return ref AboveUV3;
            }
        }
    }

    [System.Runtime.CompilerServices.InlineArray(64)]
    private struct LevelArray
    {
        private byte _element;
    }

    /// <summary>Sets up the filter levels of a frame (vp9_loop_filter_frame_init) and clears the masks.</summary>
    public void FrameInit(int level, int sharpness, bool deltaEnabled, int[] refDeltas, int[] modeDeltas, Func<int, int, bool> featureActive,
        Func<int, int, int> featureData, bool absDelta, int miRows, int miCols)
    {
        if (_sharpness != sharpness)
        {
            for (int lvl = 0; lvl <= Vp9Tables.MaxLoopFilter; lvl++)
            {
                int limit = lvl >> ((sharpness > 0 ? 1 : 0) + (sharpness > 4 ? 1 : 0));
                if (sharpness > 0 && limit > 9 - sharpness)
                {
                    limit = 9 - sharpness;
                }

                limit = Math.Max(limit, 1);
                _lim[lvl] = (byte)limit;
                _mblim[lvl] = (byte)((2 * (lvl + 2)) + limit);
                _hevThr[lvl] = (byte)(lvl >> 4);
            }

            _sharpness = sharpness;
        }

        int scale = 1 << (level >> 5);
        for (int seg = 0; seg < 8; seg++)
        {
            int lvlSeg = level;
            if (featureActive(seg, Vp9Tables.SegLvlAltLf))
            {
                int data = featureData(seg, Vp9Tables.SegLvlAltLf);
                lvlSeg = Math.Clamp(absDelta ? data : level + data, 0, Vp9Tables.MaxLoopFilter);
            }

            if (!deltaEnabled)
            {
                _lvl.AsSpan(seg * 8, 8).Fill((byte)lvlSeg);
            }
            else
            {
                _lvl[(seg * 8) + 0] = (byte)Math.Clamp(lvlSeg + (refDeltas[0] * scale), 0, Vp9Tables.MaxLoopFilter);
                for (int reference = 1; reference < 4; reference++)
                {
                    for (int mode = 0; mode < 2; mode++)
                    {
                        int inter = lvlSeg + (refDeltas[reference] * scale) + (modeDeltas[mode] * scale);
                        _lvl[(seg * 8) + (reference * 2) + mode] = (byte)Math.Clamp(inter, 0, Vp9Tables.MaxLoopFilter);
                    }
                }
            }
        }

        _sbCols = (miCols + 7) >> 3;
        int count = _sbCols * ((miRows + 7) >> 3);
        if (_masks.Length < count)
        {
            _masks = new Mask[count];
        }
        else
        {
            Array.Clear(_masks, 0, count);
        }
    }

    private int FilterLevel(ref Vp9ModeInfo mi) => _lvl[(mi.SegmentId * 8) + (Math.Max((int)mi.Ref0, 0) * 2) + Vp9Tables.ModeLfLut[mi.Mode]];

    /// <summary>Adds a decoded block to its superblock's masks (vp9_build_mask).</summary>
    public void BuildMask(ref Vp9ModeInfo mi, int miRow, int miCol, int bw, int bh)
    {
        int blockSize = mi.SbType;
        int txY = mi.TxSize;
        int txUV = Vp9Tables.UVTxSizeLookup[(blockSize * 16) + (txY * 4) + 3];
        int level = FilterLevel(ref mi);
        ref Mask m = ref _masks[((miRow >> 3) * _sbCols) + (miCol >> 3)];
        int rowInSb = miRow & 7, colInSb = miCol & 7;
        int shiftY = colInSb + (rowInSb << 3);
        int shiftUV = (colInSb >> 1) + ((rowInSb >> 1) << 2);
        bool buildUV = FirstBlockIn16x16[(rowInSb * 8) + colInSb] != 0;
        if (level == 0)
        {
            return;
        }

        for (int i = 0, index = shiftY; i < bh; i++, index += 8)
        {
            for (int j = 0; j < bw; j++)
            {
                m.Lfl[index + j] = (byte)level;
            }
        }

        m.AboveY(txY) |= Vp9Tables.AbovePredictionMask[blockSize] << shiftY;
        m.LeftY(txY) |= Vp9Tables.LeftPredictionMask[blockSize] << shiftY;
        if (buildUV)
        {
            m.AboveUV(txUV) |= (ushort)(Vp9Tables.AbovePredictionMaskUV[blockSize] << shiftUV);
            m.LeftUV(txUV) |= (ushort)(Vp9Tables.LeftPredictionMaskUV[blockSize] << shiftUV);
        }

        if (mi.Skip != 0 && mi.IsInter)
        {
            return;
        }

        m.AboveY(txY) |= (Vp9Tables.SizeMask[blockSize] & Vp9Tables.Above64x64TxformMask[txY]) << shiftY;
        m.LeftY(txY) |= (Vp9Tables.SizeMask[blockSize] & Vp9Tables.Left64x64TxformMask[txY]) << shiftY;
        if (buildUV)
        {
            m.AboveUV(txUV) |= (ushort)((Vp9Tables.SizeMaskUV[blockSize] & Vp9Tables.Above64x64TxformMaskUV[txUV]) << shiftUV);
            m.LeftUV(txUV) |= (ushort)((Vp9Tables.SizeMaskUV[blockSize] & Vp9Tables.Left64x64TxformMaskUV[txUV]) << shiftUV);
        }

        if (txY == Vp9Tables.Tx4x4)
        {
            m.Int4x4Y |= Vp9Tables.SizeMask[blockSize] << shiftY;
        }

        if (buildUV && txUV == Vp9Tables.Tx4x4)
        {
            m.Int4x4UV |= (ushort)((Vp9Tables.SizeMaskUV[blockSize] & 0xffff) << shiftUV);
        }
    }

    /// <summary>
    /// Filters the frame (loop_filter_rows), superblock by superblock in raster order, or on up to <paramref name="parallelism"/>
    /// threads as a wavefront: a row's superblock waits until the row above has filtered the one above and to the right of it,
    /// the last whose filtering reaches the same pixels, so every pixel sees the same filters in the same order.
    /// </summary>
    public void FilterFrame(Vp9Frame<T> frame, int miRows, int miCols, Vp9ModeInfo[] mi, int[] miGrid, int parallelism)
    {
        int sbRows = (miRows + 7) >> 3, sbCols = (miCols + 7) >> 3;
        int workers = Math.Min(parallelism, sbRows);
        if (workers <= 1)
        {
            for (int miRow = 0; miRow < miRows; miRow += 8)
            {
                for (int miCol = 0; miCol < miCols; miCol += 8)
                {
                    FilterSuperblock(frame, miRows, miCols, miRow, miCol, mi, miGrid);
                }
            }

            return;
        }

        // Rows are handed out in order, so the row above is always being filtered by someone: waiting for it cannot stall.
        if (_rowProgress.Length < sbRows)
        {
            _rowProgress = new int[sbRows];
        }

        int[] progress = _rowProgress;
        Array.Clear(progress, 0, sbRows);
        int next = -1, failed = 0;
        try
        {
            Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, _ =>
            {
                try
                {
                    for (int row = Interlocked.Increment(ref next); row < sbRows; row = Interlocked.Increment(ref next))
                    {
                        for (int col = 0; col < sbCols; col++)
                        {
                            if (row > 0)
                            {
                                int needed = Math.Min(col + 2, sbCols);
                                SpinWait spin = default;
                                while (Volatile.Read(ref progress[row - 1]) < needed)
                                {
                                    // A worker that failed never finishes its row: the others stop waiting for it.
                                    if (Volatile.Read(ref failed) != 0)
                                    {
                                        return;
                                    }

                                    spin.SpinOnce(sleep1Threshold: -1);
                                }
                            }

                            FilterSuperblock(frame, miRows, miCols, row * 8, col * 8, mi, miGrid);
                            Volatile.Write(ref progress[row], col + 1);
                        }
                    }
                }
                catch
                {
                    Volatile.Write(ref failed, 1);
                    throw;
                }
            });
        }
        catch (AggregateException e)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(e.InnerExceptions[0]);
        }
    }

    /// <summary>Filters one superblock row, left to right.</summary>
    public void FilterRow(Vp9Frame<T> frame, int miRows, int miCols, int miRow, Vp9ModeInfo[] mi, int[] miGrid)
    {
        for (int miCol = 0; miCol < miCols; miCol += 8)
        {
            FilterSuperblock(frame, miRows, miCols, miRow, miCol, mi, miGrid);
        }
    }

    private void FilterSuperblock(Vp9Frame<T> frame, int miRows, int miCols, int miRow, int miCol, Vp9ModeInfo[] mi, int[] miGrid)
    {
        int ssX = frame.SubsamplingX, ssY = frame.SubsamplingY;
        int bd = frame.BitDepth;
        ref Mask m = ref _masks[((miRow >> 3) * _sbCols) + (miCol >> 3)];
        AdjustMask(ref m, miRows, miCols, miRow, miCol);
        FilterPlaneSs00(frame.Plane(0), frame.Strides[0], (miRow * 8 * frame.Strides[0]) + (miCol * 8), ref m, miRows, miRow, bd, true);
        if (ssX == 1 && ssY == 1)
        {
            FilterPlaneSs11(frame.Plane(1), frame.Strides[1], (miRow * 4 * frame.Strides[1]) + (miCol * 4), ref m, miRows, miRow, bd);
            FilterPlaneSs11(frame.Plane(2), frame.Strides[2], (miRow * 4 * frame.Strides[2]) + (miCol * 4), ref m, miRows, miRow, bd);
        }
        else if (ssX == 0 && ssY == 0)
        {
            FilterPlaneSs00(frame.Plane(1), frame.Strides[1], (miRow * 8 * frame.Strides[1]) + (miCol * 8), ref m, miRows, miRow, bd, true);
            FilterPlaneSs00(frame.Plane(2), frame.Strides[2], (miRow * 8 * frame.Strides[2]) + (miCol * 8), ref m, miRows, miRow, bd, true);
        }
        else
        {
            int origin1 = (((miRow * 8) >> ssY) * frame.Strides[1]) + ((miCol * 8) >> ssX);
            FilterPlaneNon420(frame.Plane(1), frame.Strides[1], origin1, ssX, ssY, miRows, miCols, miRow, miCol, mi, miGrid, bd);
            FilterPlaneNon420(frame.Plane(2), frame.Strides[2], origin1, ssX, ssY, miRows, miCols, miRow, miCol, mi, miGrid, bd);
        }
    }

    private static void AdjustMask(ref Mask m, int miRows, int miCols, int miRow, int miCol)
    {
        m.LeftY2 |= m.LeftY3;
        m.AboveY2 |= m.AboveY3;
        m.LeftUV2 |= m.LeftUV3;
        m.AboveUV2 |= m.AboveUV3;

        m.LeftY1 |= m.LeftY0 & LeftBorder;
        m.LeftY0 &= ~LeftBorder;
        m.AboveY1 |= m.AboveY0 & AboveBorder;
        m.AboveY0 &= ~AboveBorder;
        m.LeftUV1 |= (ushort)(m.LeftUV0 & LeftBorderUV);
        m.LeftUV0 &= unchecked((ushort)~LeftBorderUV);
        m.AboveUV1 |= (ushort)(m.AboveUV0 & AboveBorderUV);
        m.AboveUV0 &= unchecked((ushort)~AboveBorderUV);

        if (miRow + 8 > miRows)
        {
            int rows = miRows - miRow;
            ulong maskY = (1UL << (rows << 3)) - 1;
            ushort maskUV = (ushort)((1 << (((rows + 1) >> 1) << 2)) - 1);
            for (int i = 0; i < 3; i++)
            {
                m.LeftY(i) &= maskY;
                m.AboveY(i) &= maskY;
                m.LeftUV(i) &= maskUV;
                m.AboveUV(i) &= maskUV;
            }

            m.Int4x4Y &= maskY;
            m.Int4x4UV &= maskUV;
            if (rows == 1)
            {
                m.AboveUV1 |= m.AboveUV2;
                m.AboveUV2 = 0;
            }

            if (rows == 5)
            {
                m.AboveUV1 |= (ushort)(m.AboveUV2 & 0xff00);
                m.AboveUV2 &= (ushort)~(m.AboveUV2 & 0xff00);
            }
        }

        if (miCol + 8 > miCols)
        {
            int columns = miCols - miCol;
            ulong maskY = ((1UL << columns) - 1) * 0x0101010101010101UL;
            ushort maskUV = (ushort)(((1 << ((columns + 1) >> 1)) - 1) * 0x1111);
            ushort maskUVInt = (ushort)(((1 << (columns >> 1)) - 1) * 0x1111);
            for (int i = 0; i < 3; i++)
            {
                m.LeftY(i) &= maskY;
                m.AboveY(i) &= maskY;
                m.LeftUV(i) &= maskUV;
                m.AboveUV(i) &= maskUV;
            }

            m.Int4x4Y &= maskY;
            m.Int4x4UV &= maskUVInt;
            if (columns == 1)
            {
                m.LeftUV1 |= m.LeftUV2;
                m.LeftUV2 = 0;
            }

            if (columns == 5)
            {
                m.LeftUV1 |= (ushort)(m.LeftUV2 & 0xcccc);
                m.LeftUV2 &= (ushort)~(m.LeftUV2 & 0xcccc);
            }
        }

        if (miCol == 0)
        {
            for (int i = 0; i < 3; i++)
            {
                m.LeftY(i) &= 0xfefefefefefefefeUL;
                m.LeftUV(i) &= 0xeeee;
            }
        }
    }

    private void FilterPlaneSs00(Span<T> p, int stride, int origin, ref Mask m, int miRows, int miRow, int bd, bool luma)
    {
        _ = luma;
        ulong mask16 = m.LeftY2, mask8 = m.LeftY1, mask4 = m.LeftY0, mask4Int = m.Int4x4Y;
        int s = origin;
        Span<byte> lfl = m.Lfl;
        for (int r = 0; r < 8 && miRow + r < miRows; r += 2)
        {
            FilterSelectivelyVertRow2(0, p, s, stride, (uint)mask16, (uint)mask8, (uint)mask4, (uint)mask4Int, lfl[(r << 3)..], bd);
            s += 16 * stride;
            mask16 >>= 16;
            mask8 >>= 16;
            mask4 >>= 16;
            mask4Int >>= 16;
        }

        s = origin;
        mask16 = m.AboveY2;
        mask8 = m.AboveY1;
        mask4 = m.AboveY0;
        mask4Int = m.Int4x4Y;
        for (int r = 0; r < 8 && miRow + r < miRows; r++)
        {
            uint m16 = 0, m8 = 0, m4 = 0;
            if (miRow + r != 0)
            {
                m16 = (uint)(mask16 & 0xff);
                m8 = (uint)(mask8 & 0xff);
                m4 = (uint)(mask4 & 0xff);
            }

            FilterSelectivelyHoriz(p, s, stride, m16, m8, m4, (uint)(mask4Int & 0xff), lfl[(r << 3)..], bd);
            s += 8 * stride;
            mask16 >>= 8;
            mask8 >>= 8;
            mask4 >>= 8;
            mask4Int >>= 8;
        }
    }

    private void FilterPlaneSs11(Span<T> p, int stride, int origin, ref Mask m, int miRows, int miRow, int bd)
    {
        Span<byte> lflUV = stackalloc byte[16];
        Span<byte> lflY = m.Lfl;
        uint mask16 = m.LeftUV2, mask8 = m.LeftUV1, mask4 = m.LeftUV0, mask4Int = m.Int4x4UV;
        int s = origin;
        for (int r = 0; r < 8 && miRow + r < miRows; r += 4)
        {
            for (int c = 0; c < 4; c++)
            {
                lflUV[(r << 1) + c] = lflY[(r << 3) + (c << 1)];
                lflUV[((r + 2) << 1) + c] = lflY[((r + 2) << 3) + (c << 1)];
            }

            FilterSelectivelyVertRow2(1, p, s, stride, mask16, mask8, mask4, mask4Int, lflUV[(r << 1)..], bd);
            s += 16 * stride;
            mask16 >>= 8;
            mask8 >>= 8;
            mask4 >>= 8;
            mask4Int >>= 8;
        }

        s = origin;
        mask16 = m.AboveUV2;
        mask8 = m.AboveUV1;
        mask4 = m.AboveUV0;
        mask4Int = m.Int4x4UV;
        for (int r = 0; r < 8 && miRow + r < miRows; r += 2)
        {
            bool skipBorder4x4R = miRow + r == miRows - 1;
            uint m4Int = skipBorder4x4R ? 0 : mask4Int & 0xf;
            uint m16 = 0, m8 = 0, m4 = 0;
            if (miRow + r != 0)
            {
                m16 = mask16 & 0xf;
                m8 = mask8 & 0xf;
                m4 = mask4 & 0xf;
            }

            FilterSelectivelyHoriz(p, s, stride, m16, m8, m4, m4Int, lflUV[(r << 1)..], bd);
            s += 8 * stride;
            mask16 >>= 4;
            mask8 >>= 4;
            mask4 >>= 4;
            mask4Int >>= 4;
        }
    }

    /// <summary>The chroma filter for 4:2:2 and 4:4:0 (vp9_filter_block_plane_non420), which builds its masks from the blocks.</summary>
    private void FilterPlaneNon420(Span<T> p, int stride, int origin, int ssX, int ssY, int miRows, int miCols, int miRow, int miCol,
        Vp9ModeInfo[] mis, int[] miGrid, int bd)
    {
        int rowStep = 1 << ssY, colStep = 1 << ssX;
        Span<uint> mask16 = stackalloc uint[8];
        Span<uint> mask8 = stackalloc uint[8];
        Span<uint> mask4 = stackalloc uint[8];
        Span<uint> mask4Int = stackalloc uint[8];
        Span<byte> lfl = stackalloc byte[64];
        int s = origin;
        for (int r = 0; r < 8 && miRow + r < miRows; r += rowStep)
        {
            uint mask16C = 0, mask8C = 0, mask4C = 0;
            for (int c = 0; c < 8 && miCol + c < miCols; c += colStep)
            {
                ref Vp9ModeInfo mi = ref mis[miGrid[((miRow + r) * miCols) + miCol + c]];
                int sbType = mi.SbType;
                bool skipThis = mi.Skip != 0 && mi.IsInter;
                bool blockEdgeLeft = Vp9Tables.Num4x4BlocksWideLookup[sbType] > 1
                    ? (c & (Vp9Tables.Num8x8BlocksWideLookup[sbType] - 1)) == 0
                    : true;
                bool skipThisC = skipThis && !blockEdgeLeft;
                bool blockEdgeAbove = Vp9Tables.Num4x4BlocksHighLookup[sbType] > 1
                    ? (r & (Vp9Tables.Num8x8BlocksHighLookup[sbType] - 1)) == 0
                    : true;
                bool skipThisR = skipThis && !blockEdgeAbove;
                int txSize = Vp9Tables.UVTxSizeLookup[(sbType * 16) + (mi.TxSize * 4) + (ssX * 2) + ssY];
                bool skipBorder4x4C = ssX != 0 && miCol + c == miCols - 1;
                bool skipBorder4x4R = ssY != 0 && miRow + r == miRows - 1;
                int level = FilterLevel(ref mi);
                lfl[(r << 3) + (c >> ssX)] = (byte)level;
                if (level == 0)
                {
                    continue;
                }

                uint bit = 1u << (c >> ssX);
                if (txSize == Vp9Tables.Tx32x32)
                {
                    if (!skipThisC && ((c >> ssX) & 3) == 0)
                    {
                        if (!skipBorder4x4C)
                        {
                            mask16C |= bit;
                        }
                        else
                        {
                            mask8C |= bit;
                        }
                    }

                    if (!skipThisR && ((r >> ssY) & 3) == 0)
                    {
                        if (!skipBorder4x4R)
                        {
                            mask16[r] |= bit;
                        }
                        else
                        {
                            mask8[r] |= bit;
                        }
                    }
                }
                else if (txSize == Vp9Tables.Tx16x16)
                {
                    if (!skipThisC && ((c >> ssX) & 1) == 0)
                    {
                        if (!skipBorder4x4C)
                        {
                            mask16C |= bit;
                        }
                        else
                        {
                            mask8C |= bit;
                        }
                    }

                    if (!skipThisR && ((r >> ssY) & 1) == 0)
                    {
                        if (!skipBorder4x4R)
                        {
                            mask16[r] |= bit;
                        }
                        else
                        {
                            mask8[r] |= bit;
                        }
                    }
                }
                else
                {
                    if (!skipThisC)
                    {
                        if (txSize == Vp9Tables.Tx8x8 || ((c >> ssX) & 3) == 0)
                        {
                            mask8C |= bit;
                        }
                        else
                        {
                            mask4C |= bit;
                        }
                    }

                    if (!skipThisR)
                    {
                        if (txSize == Vp9Tables.Tx8x8 || ((r >> ssY) & 3) == 0)
                        {
                            mask8[r] |= bit;
                        }
                        else
                        {
                            mask4[r] |= bit;
                        }
                    }

                    if (!skipThis && txSize < Vp9Tables.Tx8x8 && !skipBorder4x4C)
                    {
                        mask4Int[r] |= bit;
                    }
                }
            }

            uint borderMask = ~(miCol == 0 ? 1u : 0u);
            FilterSelectivelyVert(p, s, stride, mask16C & borderMask, mask8C & borderMask, mask4C & borderMask, mask4Int[r], lfl[(r << 3)..], bd);
            s += 8 * stride;
        }

        s = origin;
        for (int r = 0; r < 8 && miRow + r < miRows; r += rowStep)
        {
            bool skipBorder4x4R = ssY != 0 && miRow + r == miRows - 1;
            uint m4Int = skipBorder4x4R ? 0 : mask4Int[r];
            uint m16 = 0, m8 = 0, m4 = 0;
            if (miRow + r != 0)
            {
                m16 = mask16[r];
                m8 = mask8[r];
                m4 = mask4[r];
            }

            FilterSelectivelyHoriz(p, s, stride, m16, m8, m4, m4Int, lfl[(r << 3)..], bd);
            s += 8 * stride;
        }
    }

    private void FilterSelectivelyVertRow2(int subsamplingFactor, Span<T> p, int s0, int pitch, uint mask16, uint mask8, uint mask4, uint mask4Int,
        ReadOnlySpan<byte> lfl, int bd)
    {
        uint dualMaskCutoff = subsamplingFactor != 0 ? 0xffu : 0xffffu;
        int lflForward = subsamplingFactor != 0 ? 4 : 8;
        uint dualOne = 1u | (1u << lflForward);
        int lflIndex = 0;
        for (uint mask = (mask16 | mask8 | mask4 | mask4Int) & dualMaskCutoff; mask != 0; mask = (mask & ~dualOne) >> 1)
        {
            if ((mask & dualOne) != 0)
            {
                int l0 = lfl[lflIndex], l1 = lfl[lflIndex + lflForward];
                int s1 = s0 + (8 * pitch);
                if ((mask16 & dualOne) != 0)
                {
                    if ((mask16 & dualOne) == dualOne)
                    {
                        Edge16(p, s0, 1, pitch, 16, l0, bd);
                    }
                    else
                    {
                        bool second = (mask16 & 1) == 0;
                        Edge16(p, second ? s1 : s0, 1, pitch, 8, second ? l1 : l0, bd);
                    }
                }

                if ((mask8 & dualOne) != 0)
                {
                    if ((mask8 & dualOne) == dualOne)
                    {
                        Dual(8, p, s0, s1, 1, pitch, l0, l1, bd);
                    }
                    else
                    {
                        bool second = (mask8 & 1) == 0;
                        Edge8(p, second ? s1 : s0, 1, pitch, second ? l1 : l0, bd);
                    }
                }

                if ((mask4 & dualOne) != 0)
                {
                    if ((mask4 & dualOne) == dualOne)
                    {
                        Dual(4, p, s0, s1, 1, pitch, l0, l1, bd);
                    }
                    else
                    {
                        bool second = (mask4 & 1) == 0;
                        Edge4(p, second ? s1 : s0, 1, pitch, second ? l1 : l0, bd);
                    }
                }

                if ((mask4Int & dualOne) != 0)
                {
                    if ((mask4Int & dualOne) == dualOne)
                    {
                        Dual(4, p, s0 + 4, s1 + 4, 1, pitch, l0, l1, bd);
                    }
                    else
                    {
                        bool second = (mask4Int & 1) == 0;
                        Edge4(p, (second ? s1 : s0) + 4, 1, pitch, second ? l1 : l0, bd);
                    }
                }
            }

            s0 += 8;
            lflIndex++;
            mask16 >>= 1;
            mask8 >>= 1;
            mask4 >>= 1;
            mask4Int >>= 1;
        }
    }

    private void FilterSelectivelyVert(Span<T> p, int s, int pitch, uint mask16, uint mask8, uint mask4, uint mask4Int, ReadOnlySpan<byte> lfl, int bd)
    {
        int lflIndex = 0;
        for (uint mask = mask16 | mask8 | mask4 | mask4Int; mask != 0; mask >>= 1)
        {
            int level = lfl[lflIndex];
            if ((mask & 1) != 0)
            {
                if ((mask16 & 1) != 0)
                {
                    Edge16(p, s, 1, pitch, 8, level, bd);
                }
                else if ((mask8 & 1) != 0)
                {
                    Edge8(p, s, 1, pitch, level, bd);
                }
                else if ((mask4 & 1) != 0)
                {
                    Edge4(p, s, 1, pitch, level, bd);
                }
            }

            if ((mask4Int & 1) != 0)
            {
                Edge4(p, s + 4, 1, pitch, level, bd);
            }

            s += 8;
            lflIndex++;
            mask16 >>= 1;
            mask8 >>= 1;
            mask4 >>= 1;
            mask4Int >>= 1;
        }
    }

    private void FilterSelectivelyHoriz(Span<T> p, int s, int pitch, uint mask16, uint mask8, uint mask4, uint mask4Int, ReadOnlySpan<byte> lfl, int bd)
    {
        int lflIndex = 0;
        int count;
        for (uint mask = mask16 | mask8 | mask4 | mask4Int; mask != 0; mask >>= count)
        {
            count = 1;
            if ((mask & 1) != 0)
            {
                int l = lfl[lflIndex];
                if ((mask16 & 1) != 0)
                {
                    if ((mask16 & 3) == 3)
                    {
                        Edge16(p, s, pitch, 1, 16, l, bd);
                        count = 2;
                    }
                    else
                    {
                        Edge16(p, s, pitch, 1, 8, l, bd);
                    }
                }
                else if ((mask8 & 1) != 0)
                {
                    if ((mask8 & 3) == 3)
                    {
                        int ln = lfl[lflIndex + 1];
                        Dual(8, p, s, s + 8, pitch, 1, l, ln, bd);
                        if ((mask4Int & 3) == 3)
                        {
                            Dual(4, p, s + (4 * pitch), s + 8 + (4 * pitch), pitch, 1, l, ln, bd);
                        }
                        else if ((mask4Int & 1) != 0)
                        {
                            Edge4(p, s + (4 * pitch), pitch, 1, l, bd);
                        }
                        else if ((mask4Int & 2) != 0)
                        {
                            Edge4(p, s + 8 + (4 * pitch), pitch, 1, ln, bd);
                        }

                        count = 2;
                    }
                    else
                    {
                        Edge8(p, s, pitch, 1, l, bd);
                        if ((mask4Int & 1) != 0)
                        {
                            Edge4(p, s + (4 * pitch), pitch, 1, l, bd);
                        }
                    }
                }
                else if ((mask4 & 1) != 0)
                {
                    if ((mask4 & 3) == 3)
                    {
                        int ln = lfl[lflIndex + 1];
                        Dual(4, p, s, s + 8, pitch, 1, l, ln, bd);
                        if ((mask4Int & 3) == 3)
                        {
                            Dual(4, p, s + (4 * pitch), s + 8 + (4 * pitch), pitch, 1, l, ln, bd);
                        }
                        else if ((mask4Int & 1) != 0)
                        {
                            Edge4(p, s + (4 * pitch), pitch, 1, l, bd);
                        }
                        else if ((mask4Int & 2) != 0)
                        {
                            Edge4(p, s + 8 + (4 * pitch), pitch, 1, ln, bd);
                        }

                        count = 2;
                    }
                    else
                    {
                        Edge4(p, s, pitch, 1, l, bd);
                        if ((mask4Int & 1) != 0)
                        {
                            Edge4(p, s + (4 * pitch), pitch, 1, l, bd);
                        }
                    }
                }
                else
                {
                    Edge4(p, s + (4 * pitch), pitch, 1, l, bd);
                }
            }

            s += 8 * count;
            lflIndex += count;
            mask16 >>= count;
            mask8 >>= count;
            mask4 >>= count;
            mask4Int >>= count;
        }
    }

    // The filters. 'across' steps across the edge (1 for a vertical edge, the pitch for a horizontal one), 'along' along it.

    /// <summary>Gets whether the filters run in vectors here: 8-bit and 16-bit samples, on SSE2.</summary>
    private static bool Vectorized => (typeof(T) == typeof(byte) || typeof(T) == typeof(ushort)) && Vp9LoopFilterSimd.IsSupported;

    private void SimdFilter(Span<T> p, int s, int across, int along, int count, int width, int level, int bd)
    {
        if (typeof(T) == typeof(byte))
        {
            Vp9LoopFilterSimd.Filter(MemoryMarshal.Cast<T, byte>(p), s, across, along, count, width, _lim[level], _mblim[level], _hevThr[level]);
        }
        else
        {
            int shift = bd - 8;
            Vp9LoopFilterSimd.Filter(MemoryMarshal.Cast<T, ushort>(p), s, across, along, count, width, _lim[level] << shift, _mblim[level] << shift,
                _hevThr[level] << shift, bd);
        }
    }

    private void SimdDual(Span<T> p, int s, int across, int along, int width, int level0, int level1, int bd)
    {
        if (typeof(T) == typeof(byte))
        {
            Vp9LoopFilterSimd.Dual(MemoryMarshal.Cast<T, byte>(p), s, across, along, width, _lim[level0], _mblim[level0], _hevThr[level0],
                _lim[level1], _mblim[level1], _hevThr[level1]);
        }
        else
        {
            int shift = bd - 8;
            Vp9LoopFilterSimd.Dual(MemoryMarshal.Cast<T, ushort>(p), s, across, along, width, _lim[level0] << shift, _mblim[level0] << shift,
                _hevThr[level0] << shift, _lim[level1] << shift, _mblim[level1] << shift, _hevThr[level1] << shift, bd);
        }
    }

    /// <summary>Two 8-pixel edges of one filter width, the second 8 pixels along from the first, with their own levels (libvpx's dual filters).</summary>
    private void Dual(int width, Span<T> p, int s0, int s1, int across, int along, int level0, int level1, int bd)
    {
        if (Vectorized && Vp9LoopFilterSimd.IsDualSupported && s1 == s0 + (8 * along))
        {
            SimdDual(p, s0, across, along, width, level0, level1, bd);
            return;
        }

        if (width == 8)
        {
            Edge8(p, s0, across, along, level0, bd);
            Edge8(p, s1, across, along, level1, bd);
        }
        else
        {
            Edge4(p, s0, across, along, level0, bd);
            Edge4(p, s1, across, along, level1, bd);
        }
    }

    private void Edge4(Span<T> p, int s, int across, int along, int level, int bd)
    {
        if (Vectorized)
        {
            SimdFilter(p, s, across, along, 8, 4, level, bd);
            return;
        }

        int shift = bd - 8;
        int limit = _lim[level] << shift, blimit = _mblim[level] << shift, thresh = _hevThr[level] << shift;
        for (int i = 0; i < 8; i++, s += along)
        {
            int p3 = Get(p, s - (4 * across)), p2 = Get(p, s - (3 * across)), p1 = Get(p, s - (2 * across)), p0 = Get(p, s - across);
            int q0 = Get(p, s), q1 = Get(p, s + across), q2 = Get(p, s + (2 * across)), q3 = Get(p, s + (3 * across));
            if (FilterMask(limit, blimit, p3, p2, p1, p0, q0, q1, q2, q3))
            {
                Filter4(p, s, across, thresh, p1, p0, q0, q1, bd);
            }
        }
    }

    private void Edge8(Span<T> p, int s, int across, int along, int level, int bd)
    {
        if (Vectorized)
        {
            SimdFilter(p, s, across, along, 8, 8, level, bd);
            return;
        }

        int shift = bd - 8;
        int limit = _lim[level] << shift, blimit = _mblim[level] << shift, thresh = _hevThr[level] << shift;
        int flatThresh = 1 << shift;
        for (int i = 0; i < 8; i++, s += along)
        {
            int p3 = Get(p, s - (4 * across)), p2 = Get(p, s - (3 * across)), p1 = Get(p, s - (2 * across)), p0 = Get(p, s - across);
            int q0 = Get(p, s), q1 = Get(p, s + across), q2 = Get(p, s + (2 * across)), q3 = Get(p, s + (3 * across));
            if (!FilterMask(limit, blimit, p3, p2, p1, p0, q0, q1, q2, q3))
            {
                continue;
            }

            if (Flat4(flatThresh, p3, p2, p1, p0, q0, q1, q2, q3))
            {
                Set(p, s - (3 * across), (p3 + p3 + p3 + (2 * p2) + p1 + p0 + q0 + 4) >> 3);
                Set(p, s - (2 * across), (p3 + p3 + p2 + (2 * p1) + p0 + q0 + q1 + 4) >> 3);
                Set(p, s - across, (p3 + p2 + p1 + (2 * p0) + q0 + q1 + q2 + 4) >> 3);
                Set(p, s, (p2 + p1 + p0 + (2 * q0) + q1 + q2 + q3 + 4) >> 3);
                Set(p, s + across, (p1 + p0 + q0 + (2 * q1) + q2 + q3 + q3 + 4) >> 3);
                Set(p, s + (2 * across), (p0 + q0 + q1 + (2 * q2) + q3 + q3 + q3 + 4) >> 3);
            }
            else
            {
                Filter4(p, s, across, thresh, p1, p0, q0, q1, bd);
            }
        }
    }

    private void Edge16(Span<T> p, int s, int across, int along, int count, int level, int bd)
    {
        if (Vectorized)
        {
            if (count == 16 && Vp9LoopFilterSimd.IsDualSupported)
            {
                SimdDual(p, s, across, along, 16, level, level, bd);
                return;
            }

            SimdFilter(p, s, across, along, count, 16, level, bd);
            return;
        }

        int shift = bd - 8;
        int limit = _lim[level] << shift, blimit = _mblim[level] << shift, thresh = _hevThr[level] << shift;
        int flatThresh = 1 << shift;
        for (int i = 0; i < count; i++, s += along)
        {
            int p3 = Get(p, s - (4 * across)), p2 = Get(p, s - (3 * across)), p1 = Get(p, s - (2 * across)), p0 = Get(p, s - across);
            int q0 = Get(p, s), q1 = Get(p, s + across), q2 = Get(p, s + (2 * across)), q3 = Get(p, s + (3 * across));
            if (!FilterMask(limit, blimit, p3, p2, p1, p0, q0, q1, q2, q3))
            {
                continue;
            }

            bool flat = Flat4(flatThresh, p3, p2, p1, p0, q0, q1, q2, q3);
            if (!flat)
            {
                Filter4(p, s, across, thresh, p1, p0, q0, q1, bd);
                continue;
            }

            int p7 = Get(p, s - (8 * across)), p6 = Get(p, s - (7 * across)), p5 = Get(p, s - (6 * across)), p4 = Get(p, s - (5 * across));
            int q4 = Get(p, s + (4 * across)), q5 = Get(p, s + (5 * across)), q6 = Get(p, s + (6 * across)), q7 = Get(p, s + (7 * across));
            bool flat2 = Math.Abs(p4 - p0) <= flatThresh && Math.Abs(q4 - q0) <= flatThresh && Math.Abs(p5 - p0) <= flatThresh &&
                Math.Abs(q5 - q0) <= flatThresh && Math.Abs(p6 - p0) <= flatThresh && Math.Abs(q6 - q0) <= flatThresh &&
                Math.Abs(p7 - p0) <= flatThresh && Math.Abs(q7 - q0) <= flatThresh;
            if (flat2)
            {
                Set(p, s - (7 * across), ((p7 * 7) + (p6 * 2) + p5 + p4 + p3 + p2 + p1 + p0 + q0 + 8) >> 4);
                Set(p, s - (6 * across), ((p7 * 6) + p6 + (p5 * 2) + p4 + p3 + p2 + p1 + p0 + q0 + q1 + 8) >> 4);
                Set(p, s - (5 * across), ((p7 * 5) + p6 + p5 + (p4 * 2) + p3 + p2 + p1 + p0 + q0 + q1 + q2 + 8) >> 4);
                Set(p, s - (4 * across), ((p7 * 4) + p6 + p5 + p4 + (p3 * 2) + p2 + p1 + p0 + q0 + q1 + q2 + q3 + 8) >> 4);
                Set(p, s - (3 * across), ((p7 * 3) + p6 + p5 + p4 + p3 + (p2 * 2) + p1 + p0 + q0 + q1 + q2 + q3 + q4 + 8) >> 4);
                Set(p, s - (2 * across), ((p7 * 2) + p6 + p5 + p4 + p3 + p2 + (p1 * 2) + p0 + q0 + q1 + q2 + q3 + q4 + q5 + 8) >> 4);
                Set(p, s - across, (p7 + p6 + p5 + p4 + p3 + p2 + p1 + (p0 * 2) + q0 + q1 + q2 + q3 + q4 + q5 + q6 + 8) >> 4);
                Set(p, s, (p6 + p5 + p4 + p3 + p2 + p1 + p0 + (q0 * 2) + q1 + q2 + q3 + q4 + q5 + q6 + q7 + 8) >> 4);
                Set(p, s + across, (p5 + p4 + p3 + p2 + p1 + p0 + q0 + (q1 * 2) + q2 + q3 + q4 + q5 + q6 + (q7 * 2) + 8) >> 4);
                Set(p, s + (2 * across), (p4 + p3 + p2 + p1 + p0 + q0 + q1 + (q2 * 2) + q3 + q4 + q5 + q6 + (q7 * 3) + 8) >> 4);
                Set(p, s + (3 * across), (p3 + p2 + p1 + p0 + q0 + q1 + q2 + (q3 * 2) + q4 + q5 + q6 + (q7 * 4) + 8) >> 4);
                Set(p, s + (4 * across), (p2 + p1 + p0 + q0 + q1 + q2 + q3 + (q4 * 2) + q5 + q6 + (q7 * 5) + 8) >> 4);
                Set(p, s + (5 * across), (p1 + p0 + q0 + q1 + q2 + q3 + q4 + (q5 * 2) + q6 + (q7 * 6) + 8) >> 4);
                Set(p, s + (6 * across), (p0 + q0 + q1 + q2 + q3 + q4 + q5 + (q6 * 2) + (q7 * 7) + 8) >> 4);
            }
            else
            {
                Set(p, s - (3 * across), (p3 + p3 + p3 + (2 * p2) + p1 + p0 + q0 + 4) >> 3);
                Set(p, s - (2 * across), (p3 + p3 + p2 + (2 * p1) + p0 + q0 + q1 + 4) >> 3);
                Set(p, s - across, (p3 + p2 + p1 + (2 * p0) + q0 + q1 + q2 + 4) >> 3);
                Set(p, s, (p2 + p1 + p0 + (2 * q0) + q1 + q2 + q3 + 4) >> 3);
                Set(p, s + across, (p1 + p0 + q0 + (2 * q1) + q2 + q3 + q3 + 4) >> 3);
                Set(p, s + (2 * across), (p0 + q0 + q1 + (2 * q2) + q3 + q3 + q3 + 4) >> 3);
            }
        }
    }

    private static bool FilterMask(int limit, int blimit, int p3, int p2, int p1, int p0, int q0, int q1, int q2, int q3) =>
        Math.Abs(p3 - p2) <= limit && Math.Abs(p2 - p1) <= limit && Math.Abs(p1 - p0) <= limit && Math.Abs(q1 - q0) <= limit &&
        Math.Abs(q2 - q1) <= limit && Math.Abs(q3 - q2) <= limit && (Math.Abs(p0 - q0) * 2) + (Math.Abs(p1 - q1) / 2) <= blimit;

    private static bool Flat4(int thresh, int p3, int p2, int p1, int p0, int q0, int q1, int q2, int q3) =>
        Math.Abs(p1 - p0) <= thresh && Math.Abs(q1 - q0) <= thresh && Math.Abs(p2 - p0) <= thresh && Math.Abs(q2 - q0) <= thresh &&
        Math.Abs(p3 - p0) <= thresh && Math.Abs(q3 - q0) <= thresh;

    /// <summary>The 4-tap filter (filter4 and highbd_filter4), in values centered on zero and clamped to the signed range of the bit depth.</summary>
    private static void Filter4(Span<T> p, int s, int across, int thresh, int p1, int p0, int q0, int q1, int bd)
    {
        int shift = bd - 8;
        int offset = 0x80 << shift;
        int lo = -128 << shift, hi = (128 << shift) - 1;
        int ps1 = p1 - offset, ps0 = p0 - offset, qs0 = q0 - offset, qs1 = q1 - offset;
        bool hev = Math.Abs(p1 - p0) > thresh || Math.Abs(q1 - q0) > thresh;
        int filter = hev ? Math.Clamp(ps1 - qs1, lo, hi) : 0;
        filter = Math.Clamp(filter + (3 * (qs0 - ps0)), lo, hi);
        int filter1 = Math.Clamp(filter + 4, lo, hi) >> 3;
        int filter2 = Math.Clamp(filter + 3, lo, hi) >> 3;
        Set(p, s, Math.Clamp(qs0 - filter1, lo, hi) + offset);
        Set(p, s - across, Math.Clamp(ps0 + filter2, lo, hi) + offset);
        if (!hev)
        {
            filter = (filter1 + 1) >> 1;
            Set(p, s + across, Math.Clamp(qs1 - filter, lo, hi) + offset);
            Set(p, s - (2 * across), Math.Clamp(ps1 + filter, lo, hi) + offset);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int Get(Span<T> p, int i) => int.CreateTruncating(p[i]);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void Set(Span<T> p, int i, int value) => p[i] = T.CreateTruncating(value);
}
