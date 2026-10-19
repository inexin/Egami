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

    internal HevcSps Sps { get; }

    internal HevcPps Pps { get; }

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

    /// <summary>Gets the flags of each 4x4 block (<see cref="Pcm"/>, <see cref="Bypass"/>, the transform and prediction edges).</summary>
    internal byte[] Flags { get; }

    /// <summary>Gets MinTbAddrZs of each 4x4 block.</summary>
    internal int[] ZScan { get; }

    /// <summary>Gets SliceAddrRs of each CTB (in raster scan), or -1 before it is decoded.</summary>
    internal int[] CtbSlice { get; }

    /// <summary>Gets the slice header (of the independent slice segment) each CTB belongs to.</summary>
    internal HevcSliceHeader?[] CtbSliceHeader { get; }

    /// <summary>Gets each CTB's SAO parameters.</summary>
    internal HevcSaoParameters[] Sao { get; }

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
