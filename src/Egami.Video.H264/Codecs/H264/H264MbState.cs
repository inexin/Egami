namespace Egami.Video.Codecs.H264;

/// <summary>The kinds of macroblock the decoder tells apart (H.264 Tables 7-11, 7-13 and 7-14 grouped).</summary>
internal enum H264MbKind : byte
{
    /// <summary>Intra 4x4 (I_NxN without the 8x8 transform).</summary>
    I4x4,

    /// <summary>Intra 8x8 (I_NxN with the 8x8 transform).</summary>
    I8x8,

    I16x16,

    IPcm,

    /// <summary>An inter macroblock with partitions (P or B, not skipped, not direct).</summary>
    Inter,

    /// <summary>B_Direct_16x16.</summary>
    BDirect,

    PSkip,

    BSkip,
}

/// <summary>
/// The per-picture macroblock data the slices fill and later macroblocks, CAVLC and CABAC context selection and the deblocking
/// filter read: one entry per macroblock, or per 4x4 block in picture raster order.
/// </summary>
internal sealed class H264MbState
{
    internal H264MbState(int widthMbs, int heightMbs, int chromaFormatIdc)
    {
        WidthMbs = widthMbs;
        HeightMbs = heightMbs;
        int mbs = widthMbs * heightMbs;
        W4 = widthMbs * 4;
        int blocks = W4 * heightMbs * 4;
        ChromaW4 = chromaFormatIdc == 0 ? 0 : widthMbs * 2;
        ChromaH4 = chromaFormatIdc switch { 0 => 0, 1 => heightMbs * 2, _ => heightMbs * 4 };
        Slice = new int[mbs];
        Kind = new H264MbKind[mbs];
        Qp = new sbyte[mbs];
        QpC = [new sbyte[mbs], new sbyte[mbs]];
        Cbp = new byte[mbs];
        ChromaPredMode = new byte[mbs];
        Transform8x8 = new bool[mbs];
        CbfDc = new byte[mbs];
        Direct8x8 = new byte[mbs];
        QpDeltaNonZero = new bool[mbs];
        NzLuma = new byte[blocks];
        NzCb = new byte[ChromaW4 * ChromaH4];
        NzCr = new byte[ChromaW4 * ChromaH4];
        IntraModes = new sbyte[blocks];
        Mvd = [new byte[blocks * 2], new byte[blocks * 2]];
        Nonzero = new byte[blocks];
        FlatInside = new bool[mbs];
    }

    internal int WidthMbs { get; }

    internal int HeightMbs { get; }

    /// <summary>Gets the picture width in 4x4 blocks.</summary>
    internal int W4 { get; }

    internal int ChromaW4 { get; }

    internal int ChromaH4 { get; }

    /// <summary>Gets the slice number each macroblock was decoded in, or -1 before it is decoded.</summary>
    internal int[] Slice { get; }

    internal H264MbKind[] Kind { get; }

    /// <summary>Gets QPY per macroblock (0 for I_PCM, as the deblocking filter uses it).</summary>
    internal sbyte[] Qp { get; }

    /// <summary>Gets QPC (without the bit depth offset) of Cb and Cr per macroblock, for the deblocking filter.</summary>
    internal sbyte[][] QpC { get; }

    /// <summary>Gets coded_block_pattern: luma in bits 0 to 3, chroma in bits 4 and 5.</summary>
    internal byte[] Cbp { get; }

    internal byte[] ChromaPredMode { get; }

    internal bool[] Transform8x8 { get; }

    /// <summary>Gets the CABAC coded_block_flag of the DC blocks: bit 0 luma (Intra16x16), bit 1 Cb, bit 2 Cr.</summary>
    internal byte[] CbfDc { get; }

    /// <summary>Gets which 8x8 blocks are predicted in direct mode (bits 0 to 3), for CABAC ref_idx contexts.</summary>
    internal byte[] Direct8x8 { get; }

    /// <summary>Gets whether mb_qp_delta was non-zero, for the next macroblock's CABAC context.</summary>
    internal bool[] QpDeltaNonZero { get; }

    /// <summary>Gets TotalCoeff per luma 4x4 block (CAVLC nC; CABAC coded_block_flag when non-zero).</summary>
    internal byte[] NzLuma { get; }

    internal byte[] NzCb { get; }

    internal byte[] NzCr { get; }

    /// <summary>Gets whether each luma 4x4 block has non-zero coefficients, the 8x8 block's for the 8x8 transform (deblocking).</summary>
    internal byte[] Nonzero { get; }

    /// <summary>
    /// Gets whether an inter macroblock has no coefficients and one motion for all its blocks, so its internal edges have bS 0
    /// and only its own left and top edges are deblocked.
    /// </summary>
    internal bool[] FlatInside { get; }

    /// <summary>Gets the Intra4x4/8x8 prediction mode per 4x4 block, -1 for other macroblocks.</summary>
    internal sbyte[] IntraModes { get; }

    /// <summary>Gets the absolute mvd per 4x4 block (x then y, each capped at 255) per list, for CABAC contexts.</summary>
    internal byte[][] Mvd { get; }

    internal static bool IsIntra(H264MbKind kind) => kind <= H264MbKind.IPcm;

    internal static bool IsSkip(H264MbKind kind) => kind >= H264MbKind.PSkip;

    /// <summary>Gets ready for a new picture: no macroblock is decoded.</summary>
    internal void Clear() => Array.Fill(Slice, -1);
}
