namespace Egami.Video.Codecs.H264;

/// <summary>
/// Direct prediction in B slices (8.4.1.2) as the decoder derives it, with direct_8x8_inference_flag: spatial from the
/// macroblock's neighbours and the co-located block's colZeroFlag, or temporal from the co-located block's vector scaled by
/// picture order distance.
/// </summary>
internal sealed partial class H264MbSyntax
{
    /// <summary>Gets or sets whether the B slice predicts direct blocks spatially (direct_spatial_mv_pred_flag).</summary>
    public bool DirectSpatial { get; set; } = true;

    /// <summary>Gets or sets RefPicList1[0], whose motion arrays hold the co-located blocks.</summary>
    public H264Picture? Col { get; set; }

    /// <summary>Gets or sets the picture order count of each list 0 reference.</summary>
    public int[] RefPocs { get; set; } = [];

    /// <summary>Gets or sets the picture order count of RefPicList1[0].</summary>
    public int ColPoc { get; set; }

    /// <summary>Gets or sets the picture order count of the picture being coded.</summary>
    public int CurrentPoc { get; set; }

    /// <summary>
    /// Derives the direct motion of the whole macroblock at the last <see cref="Locate"/> into <paramref name="code"/>: each
    /// 8x8 block's reference in both lists (-1 for a list it does not use) and each 4x4 block's vectors. Neighbour motion is read
    /// from <see cref="Motion"/>, so call before the macroblock's own motion is written.
    /// </summary>
    public void Direct(H264MbCode code)
    {
        if (DirectSpatial)
        {
            SpatialDirect(code);
        }
        else
        {
            TemporalDirect(code);
        }
    }

    private void SpatialDirect(H264MbCode code)
    {
        Span<int> refIdx = stackalloc int[2];
        Span<int> mvp = stackalloc int[2];
        for (int list = 0; list < 2; list++)
        {
            bool availableA = Neighbor(list, -1, 0, 0, out int refA, out int mvA);
            bool availableB = Neighbor(list, 0, -1, 0, out int refB, out int mvB);
            bool availableC = Neighbor(list, 4, -1, 0, out int refC, out int mvC) || Neighbor(list, -1, -1, 0, out refC, out mvC);
            refIdx[list] = MinPositive(refA, MinPositive(refB, refC));
            mvp[list] = refIdx[list] >= 0 ? Median(availableA, availableB, availableC, refA, refB, refC, mvA, mvB, mvC, refIdx[list]) : 0;
        }

        bool zero = refIdx[0] < 0 && refIdx[1] < 0;
        if (zero)
        {
            refIdx[0] = refIdx[1] = 0;
            mvp[0] = mvp[1] = 0;
        }

        H264Picture col = Col!;
        for (int i8 = 0; i8 < 4; i8++)
        {
            // With 8x8 inference each 8x8 block's co-located block is the corner one.
            int cx = (i8 & 1) * 3, cy = (i8 >> 1) * 3;
            bool colZero = false;
            if (!zero && !col.MbIntra[_addr])
            {
                int b = _b0 + (cy * _w4) + cx;
                int list = col.RefIdx[0][b] >= 0 ? 0 : 1;
                int mvCol = col.Mv[list][b];
                colZero = col.RefIdx[list][b] == 0 && (uint)(H264SliceDecoder.MvX(mvCol) + 1) <= 2 && (uint)(H264SliceDecoder.MvY(mvCol) + 1) <= 2;
            }

            code.RefIdx[i8] = refIdx[0];
            code.RefIdx1[i8] = refIdx[1];
            int mv0 = refIdx[0] < 0 || (refIdx[0] == 0 && colZero) ? 0 : mvp[0];
            int mv1 = refIdx[1] < 0 || (refIdx[1] == 0 && colZero) ? 0 : mvp[1];
            FillQuarter(code.Mv, i8, mv0);
            FillQuarter(code.Mv1, i8, mv1);
        }
    }

    private void TemporalDirect(H264MbCode code)
    {
        H264Picture col = Col!;
        for (int i8 = 0; i8 < 4; i8++)
        {
            int cx = (i8 & 1) * 3, cy = (i8 >> 1) * 3;
            int b = _b0 + (cy * _w4) + cx;
            int mvCol = 0, refL0 = 0;
            if (!col.MbIntra[_addr])
            {
                int list = col.RefIdx[0][b] >= 0 ? 0 : 1;
                mvCol = col.Mv[list][b];
                refL0 = Array.IndexOf(RefIds, col.RefPicId[list][b], 0, RefCount);
                if (refL0 < 0)
                {
                    throw new InvalidOperationException("A co-located block refers to a picture outside list 0; temporal direct was not allowed.");
                }
            }

            int tb = Math.Clamp(CurrentPoc - RefPocs[refL0], -128, 127);
            int td = Math.Clamp(ColPoc - RefPocs[refL0], -128, 127);
            int mv0, mv1;
            if (td == 0)
            {
                mv0 = mvCol;
                mv1 = 0;
            }
            else
            {
                int tx = (16384 + Math.Abs(td / 2)) / td;
                int scale = Math.Clamp(((tb * tx) + 32) >> 6, -1024, 1023);
                int x = ((scale * H264SliceDecoder.MvX(mvCol)) + 128) >> 8, y = ((scale * H264SliceDecoder.MvY(mvCol)) + 128) >> 8;
                mv0 = H264SliceDecoder.PackMv(x, y);
                mv1 = H264SliceDecoder.PackMv(x - H264SliceDecoder.MvX(mvCol), y - H264SliceDecoder.MvY(mvCol));
            }

            code.RefIdx[i8] = refL0;
            code.RefIdx1[i8] = 0;
            FillQuarter(code.Mv, i8, mv0);
            FillQuarter(code.Mv1, i8, mv1);
        }
    }

    /// <summary>
    /// Whether temporal direct prediction can be used in a B slice: every co-located corner block of an inter macroblock refers
    /// to a picture among the list 0 references.
    /// </summary>
    public bool TemporalAllowed()
    {
        H264Picture col = Col!;
        int[] ids = RefIds;
        for (int mbY = 0; mbY < _mb.HeightMbs; mbY++)
        {
            for (int mbX = 0; mbX < _mb.WidthMbs; mbX++)
            {
                if (col.MbIntra[(mbY * _mb.WidthMbs) + mbX])
                {
                    continue;
                }

                int b0 = (mbY * 4 * _w4) + (mbX * 4);
                for (int i8 = 0; i8 < 4; i8++)
                {
                    int b = b0 + ((i8 >> 1) * 3 * _w4) + ((i8 & 1) * 3);
                    int list = col.RefIdx[0][b] >= 0 ? 0 : 1;
                    if (Array.IndexOf(ids, col.RefPicId[list][b], 0, RefCount) < 0)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static void FillQuarter(int[] mvs, int i8, int mv)
    {
        int at = ((i8 >> 1) * 8) + ((i8 & 1) * 2);
        mvs[at] = mvs[at + 1] = mvs[at + 4] = mvs[at + 5] = mv;
    }

    private static int MinPositive(int x, int y) => x >= 0 && y >= 0 ? Math.Min(x, y) : Math.Max(x, y);
}
