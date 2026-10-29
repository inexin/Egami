namespace Egami.Heic.Hevc;

/// <summary>
/// A picture being decoded: its sample planes and what the decoding, deblocking and SAO of later blocks need to know about each
/// 4x4 luma block (intra mode, coding tree depth, QpY, PCM and bypass flags, transform edges) and each coding tree block (its
/// slice, SAO parameters).
/// </summary>
internal sealed class HevcPicture
{
    internal const byte Pcm = 1;
    internal const byte Bypass = 2;
    internal const byte EdgeLeft = 4;
    internal const byte EdgeTop = 8;
    internal const byte Coded = 16;
    internal const byte TransformLeft = 32;
    internal const byte TransformTop = 64;

    internal HevcPicture(HevcSps sps, HevcPps pps)
    {
        Sps = sps;
        Pps = pps;
        Layout = new HevcLayout(sps, pps);
        int components = sps.ChromaFormatIdc == 0 ? 1 : 3;
        Planes = new ushort[components][];
        Widths = new int[components];
        Heights = new int[components];
        for (int c = 0; c < components; c++)
        {
            Widths[c] = c == 0 ? sps.Width : sps.Width / sps.SubWidthC;
            Heights[c] = c == 0 ? sps.Height : sps.Height / sps.SubHeightC;
            Planes[c] = new ushort[Widths[c] * Heights[c]];
        }

        Width4 = (sps.Width + 3) >> 2;
        Height4 = (sps.Height + 3) >> 2;
        int blocks = Width4 * Height4;
        IntraMode = new byte[blocks];
        CtDepth = new byte[blocks];
        QpY = new sbyte[blocks];
        Flags = new byte[blocks];
        ZScan = new int[blocks];
        int ctbs = sps.WidthInCtbs * sps.HeightInCtbs;
        CtbSlice = new int[ctbs];
        Array.Fill(CtbSlice, -1);
        CtbSliceHeader = new HevcSliceHeader?[ctbs];
        Sao = new HevcSaoParameters[ctbs];
        PredFlags = new byte[blocks];
        SkipFlags = new byte[blocks];
        RefIdx = [new sbyte[blocks], new sbyte[blocks]];
        Mv = [new int[blocks], new int[blocks]];
        RefId = [new int[blocks], new int[blocks]];
        CtbRefs = new HevcSliceRefs?[ctbs];

        // MinTbAddrZs (6-10) at 4x4 granularity: the tile scan address of the CTB, then the z-order inside it.
        int shift = sps.CtbLog2Size - 2;
        for (int y = 0; y < Height4; y++)
        {
            for (int x = 0; x < Width4; x++)
            {
                int ctbRs = ((y >> shift) * sps.WidthInCtbs) + (x >> shift);
                int inner = 0;
                for (int i = 0; i < shift; i++)
                {
                    inner |= ((x >> i) & 1) << (2 * i);
                    inner |= ((y >> i) & 1) << ((2 * i) + 1);
                }

                ZScan[(y * Width4) + x] = (Layout.RsToTs[ctbRs] << (2 * shift)) + inner;
            }
        }
    }

    internal HevcSps Sps { get; private set; }

    /// <summary>
    /// Gets whether the picture's arrays and layout fit pictures of these parameter sets: the same size, chroma format, block
    /// sizes and tiles, as when a stream sends its parameter sets again.
    /// </summary>
    internal bool Fits(HevcSps sps, HevcPps pps) =>
        (sps == Sps || (sps.Width == Sps.Width && sps.Height == Sps.Height && sps.ChromaFormatIdc == Sps.ChromaFormatIdc
            && sps.CtbLog2Size == Sps.CtbLog2Size && sps.MinCbLog2Size == Sps.MinCbLog2Size && sps.MinTbLog2Size == Sps.MinTbLog2Size))
        && (pps == Pps || (pps.TilesEnabled == Pps.TilesEnabled && pps.ColumnWidths.AsSpan().SequenceEqual(Pps.ColumnWidths)
            && pps.RowHeights.AsSpan().SequenceEqual(Pps.RowHeights) && pps.TileColumns == Pps.TileColumns && pps.TileRows == Pps.TileRows));

    /// <summary>Makes a recycled picture ready for the next one: its parameter sets, and the block maps a picture fills only where it is decoded.</summary>
    internal void Clear(HevcSps sps, HevcPps pps)
    {
        Sps = sps;
        Pps = pps;
        Array.Clear(Flags);
        Array.Clear(PredFlags);
        Array.Clear(SkipFlags);
        Array.Fill(CtbSlice, -1);
        Array.Clear(CtbSliceHeader);
        Array.Clear(CtbRefs);
        Array.Clear(Sao);
    }

    internal HevcPps Pps { get; private set; }

    internal HevcLayout Layout { get; }

    /// <summary>Gets the sample planes, Y then Cb and Cr, each row after row.</summary>
    internal ushort[][] Planes { get; }

    /// <summary>Gets each plane's width (its stride).</summary>
    internal int[] Widths { get; }

    /// <summary>Gets each plane's height.</summary>
    internal int[] Heights { get; }

    /// <summary>Gets the width in 4x4 blocks.</summary>
    internal int Width4 { get; }

    /// <summary>Gets the height in 4x4 blocks.</summary>
    internal int Height4 { get; }

    /// <summary>Gets IntraPredModeY of each 4x4 block.</summary>
    internal byte[] IntraMode { get; }

    /// <summary>Gets CtDepth of each 4x4 block.</summary>
    internal byte[] CtDepth { get; }

    /// <summary>Gets QpY of each 4x4 block.</summary>
    internal sbyte[] QpY { get; }

    /// <summary>
    /// Gets the flags of each 4x4 block: <see cref="Pcm"/>, <see cref="Bypass"/>, the block edges the deblocking filter looks at
    /// (<see cref="EdgeLeft"/>, <see cref="EdgeTop"/>), which of them are transform block edges, and <see cref="Coded"/> for a
    /// luma transform block with coefficients.
    /// </summary>
    internal byte[] Flags { get; }

    /// <summary>Gets MinTbAddrZs of each 4x4 block.</summary>
    internal int[] ZScan { get; }

    /// <summary>Gets SliceAddrRs of each CTB (in raster scan), or -1 before it is decoded.</summary>
    internal int[] CtbSlice { get; }

    /// <summary>Gets the slice header (of the independent slice segment) each CTB belongs to.</summary>
    internal HevcSliceHeader?[] CtbSliceHeader { get; }

    /// <summary>Gets each CTB's SAO parameters.</summary>
    internal HevcSaoParameters[] Sao { get; }

    /// <summary>Gets predFlagL0 (bit 0) and predFlagL1 (bit 1) of each 4x4 block: 0 for an intra block.</summary>
    internal byte[] PredFlags { get; }

    /// <summary>Gets cu_skip_flag of each 4x4 block.</summary>
    internal byte[] SkipFlags { get; }

    /// <summary>Gets refIdxL0 and refIdxL1 of each 4x4 block.</summary>
    internal sbyte[][] RefIdx { get; }

    /// <summary>Gets mvL0 and mvL1 of each 4x4 block: the horizontal component in the low 16 bits, the vertical one in the high.</summary>
    internal int[][] Mv { get; }

    /// <summary>Gets, per list, the <see cref="HevcDpbPicture.Id"/> of each 4x4 block's reference picture, which the deblocking filter compares.</summary>
    internal int[][] RefId { get; }

    /// <summary>Gets the reference pictures of the slice each CTB belongs to (null for an I slice).</summary>
    internal HevcSliceRefs?[] CtbRefs { get; }

    /// <summary>
    /// Gets the motion a later picture's temporal motion vector prediction reads (8.5.3.2.8), one per 16x16 block, kept when the
    /// picture is done: or null before.
    /// </summary>
    internal HevcColMotion? ColMotion { get; set; }

    /// <summary>
    /// Gets whether an intra block at (xC, yC) may predict from the samples at (xN, yN) (8.4.4.2.2): available, and with
    /// constrained_intra_pred_flag not in an inter block.
    /// </summary>
    internal bool IntraReferenceAvailable(int xC, int yC, int xN, int yN) =>
        Available(xC, yC, xN, yN) && (!Pps.ConstrainedIntraPred || PredFlags[((yN >> 2) * Width4) + (xN >> 2)] == 0);

    /// <summary>
    /// Gets whether the neighbouring luma location (xN, yN) is available to the current one (xC, yC) (6.4.1): inside the
    /// picture, before it in z-scan order, in the same slice and the same tile.
    /// </summary>
    internal bool Available(int xC, int yC, int xN, int yN)
    {
        if (xN < 0 || yN < 0 || xN >= Sps.Width || yN >= Sps.Height)
        {
            return false;
        }

        int n = ((yN >> 2) * Width4) + (xN >> 2), c = ((yC >> 2) * Width4) + (xC >> 2);
        if (ZScan[n] > ZScan[c])
        {
            return false;
        }

        int ctbN = ((yN >> Sps.CtbLog2Size) * Sps.WidthInCtbs) + (xN >> Sps.CtbLog2Size);
        int ctbC = ((yC >> Sps.CtbLog2Size) * Sps.WidthInCtbs) + (xC >> Sps.CtbLog2Size);
        return CtbSlice[ctbN] == CtbSlice[ctbC] && Layout.TileId[Layout.RsToTs[ctbN]] == Layout.TileId[Layout.RsToTs[ctbC]];
    }
}

/// <summary>The SAO parameters of one CTB (H.265 7.4.9.3): per component its type, offsets and band position or edge class.</summary>
internal struct HevcSaoParameters
{
    /// <summary>SaoTypeIdx per component: 0 off, 1 band offset, 2 edge offset.</summary>
    internal int TypeY, TypeCb, TypeCr;

    /// <summary>The band position (band offset) or edge class (edge offset) per component.</summary>
    internal int ClassY, ClassCb, ClassCr;

    /// <summary>SaoOffsetVal[1..4] per component, already scaled by the offset scale.</summary>
    internal OffsetSet OffsetsY, OffsetsCb, OffsetsCr;

    internal readonly int Type(int c) => c == 0 ? TypeY : c == 1 ? TypeCb : TypeCr;

    internal readonly int Class(int c) => c == 0 ? ClassY : c == 1 ? ClassCb : ClassCr;

    internal readonly OffsetSet Offsets(int c) => c == 0 ? OffsetsY : c == 1 ? OffsetsCb : OffsetsCr;

    /// <summary>Four offsets.</summary>
    [System.Runtime.CompilerServices.InlineArray(4)]
    internal struct OffsetSet
    {
        private int _first;
    }
}

/// <summary>
/// A decoded picture's motion as a co-located picture gives it (8.5.3.2.8, compressed to 16x16 blocks as its "((xColBr &gt;&gt; 4)
/// &lt;&lt; 4)"): per 16x16 block and list, the vector, the order count of its reference picture and whether that was long-term.
/// </summary>
internal sealed class HevcColMotion
{
    /// <summary>Keeps the motion of each 16x16 block's top-left 4x4 block, with its slice's reference order counts.</summary>
    internal HevcColMotion(HevcPicture picture)
    {
        HevcSps sps = picture.Sps;
        Width16 = (sps.Width + 15) >> 4;
        int count = Width16 * ((sps.Height + 15) >> 4);
        PredFlags = new byte[count];
        Mv = [new int[count], new int[count]];
        RefPoc = [new int[count], new int[count]];
        LongTerm = [new bool[count], new bool[count]];
        Update(picture);
    }

    /// <summary>Takes the motion of a picture of the same size again, for a recycled picture.</summary>
    internal void Update(HevcPicture picture)
    {
        HevcSps sps = picture.Sps;
        int height16 = (sps.Height + 15) >> 4;
        for (int y = 0; y < height16; y++)
        {
            for (int x = 0; x < Width16; x++)
            {
                int i = (y * Width16) + x, b = (y * 4 * picture.Width4) + (x * 4);
                int ctb = ((y << 4 >> sps.CtbLog2Size) * sps.WidthInCtbs) + (x << 4 >> sps.CtbLog2Size);
                HevcSliceRefs? refs = picture.CtbRefs[ctb];
                byte flags = picture.PredFlags[b];
                PredFlags[i] = refs is null ? (byte)0 : flags;
                for (int list = 0; list < 2 && refs is not null; list++)
                {
                    if ((flags & (1 << list)) != 0)
                    {
                        int r = picture.RefIdx[list][b];
                        Mv[list][i] = picture.Mv[list][b];
                        RefPoc[list][i] = refs.Lists[list][r].Poc;
                        LongTerm[list][i] = refs.LongTerm[list][r];
                    }
                }
            }
        }
    }

    /// <summary>Gets the width in 16x16 blocks.</summary>
    internal int Width16 { get; }

    /// <summary>Gets predFlagL0 and predFlagL1 per 16x16 block, as <see cref="HevcPicture.PredFlags"/>.</summary>
    internal byte[] PredFlags { get; }

    /// <summary>Gets the vectors per list and 16x16 block.</summary>
    internal int[][] Mv { get; }

    /// <summary>Gets the order count of the reference picture per list and 16x16 block.</summary>
    internal int[][] RefPoc { get; }

    /// <summary>Gets whether the reference picture was a long-term one, per list and 16x16 block.</summary>
    internal bool[][] LongTerm { get; }
}
