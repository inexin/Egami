namespace Egami.Video.Codecs.H264;

/// <summary>
/// One macroblock as the encoder codes it: its type and prediction modes and its quantized levels, each block's in the order
/// its residual block codes them (scan order, an Intra16x16 or chroma AC block from scan position 1).
/// </summary>
internal sealed class H264MbCode
{
    /// <summary>
    /// Gets or sets the macroblock's kind: <see cref="H264MbKind.I4x4"/>, <see cref="H264MbKind.I8x8"/>, <see cref="H264MbKind.I16x16"/>,
    /// <see cref="H264MbKind.Inter"/> or <see cref="H264MbKind.PSkip"/>.
    /// </summary>
    public H264MbKind Kind { get; set; }

    /// <summary>Gets or sets whether an inter macroblock's luma uses the 8x8 transform.</summary>
    public bool Transform8x8 { get; set; }

    /// <summary>Gets whether the luma levels are 8x8 blocks' (<see cref="Luma8"/>).</summary>
    public bool Eight => Kind == H264MbKind.I8x8 || (Kind == H264MbKind.Inter && Transform8x8);

    /// <summary>Gets or sets an inter macroblock's partitioning: 0 16x16, 1 16x8, 2 8x16, 3 8x8 (mb_type of a P slice).</summary>
    public int Partition { get; set; }

    /// <summary>Gets each 8x8 partition's sub_mb_type: 0 8x8, 1 8x4, 2 4x8, 3 4x4.</summary>
    public int[] SubTypes { get; } = new int[4];

    /// <summary>Gets each partition's ref_idx_l0 (one per 8x8 block for P_8x8).</summary>
    public int[] RefIdx { get; } = new int[4];

    /// <summary>Gets the motion vector of each 4x4 block, raster order in the macroblock, x in the low 16 bits.</summary>
    public int[] Mv { get; } = new int[16];

    /// <summary>Gets the motion vector difference coded for each 4x4 block's partition, raster order, packed as <see cref="Mv"/>.</summary>
    public int[] Mvd { get; } = new int[16];

    /// <summary>Gets or sets the Intra16x16 prediction mode.</summary>
    public int I16Mode { get; set; }

    /// <summary>Gets or sets intra_chroma_pred_mode.</summary>
    public int ChromaMode { get; set; }

    /// <summary>Gets the Intra4x4 mode of each 4x4 block by luma4x4BlkIdx, or the Intra8x8 mode of each 8x8 block in the first four.</summary>
    public int[] Modes { get; } = new int[16];

    /// <summary>Gets or sets coded_block_pattern: luma in bits 0 to 3, chroma (0 to 2) in bits 4 and 5.</summary>
    public int Cbp { get; set; }

    /// <summary>Gets or sets QPY of the macroblock.</summary>
    public int Qp { get; set; }

    /// <summary>Gets the Intra16x16 DC levels in scan order.</summary>
    public int[] LumaDc { get; } = new int[16];

    /// <summary>Gets or sets the number of non-zero Intra16x16 DC levels.</summary>
    public int LumaDcCount { get; set; }

    /// <summary>Gets the levels of each 4x4 luma block by luma4x4BlkIdx, 16 each (15 used for Intra16x16 AC).</summary>
    public int[] Luma { get; } = new int[16 * 16];

    /// <summary>Gets the number of non-zero levels of each 4x4 luma block.</summary>
    public int[] LumaCount { get; } = new int[16];

    /// <summary>Gets the levels of each 8x8 luma block in scan order, 64 each.</summary>
    public int[] Luma8 { get; } = new int[4 * 64];

    /// <summary>Gets the number of non-zero levels of each 8x8 luma block.</summary>
    public int[] Luma8Count { get; } = new int[4];

    /// <summary>Gets the chroma DC levels, Cb then Cr, 4 each.</summary>
    public int[] ChromaDc { get; } = new int[8];

    /// <summary>Gets the number of non-zero chroma DC levels of Cb and Cr.</summary>
    public int[] ChromaDcCount { get; } = new int[2];

    /// <summary>Gets the chroma AC levels, Cb blocks 0 to 3 then Cr's, 15 each.</summary>
    public int[] ChromaAc { get; } = new int[8 * 16];

    /// <summary>Gets the number of non-zero levels of each chroma AC block.</summary>
    public int[] ChromaAcCount { get; } = new int[8];

    /// <summary>Copies another macroblock's choices and levels into this one.</summary>
    public void CopyFrom(H264MbCode other)
    {
        Kind = other.Kind;
        Transform8x8 = other.Transform8x8;
        Partition = other.Partition;
        other.SubTypes.CopyTo(SubTypes, 0);
        other.RefIdx.CopyTo(RefIdx, 0);
        other.Mv.CopyTo(Mv, 0);
        other.Mvd.CopyTo(Mvd, 0);
        I16Mode = other.I16Mode;
        ChromaMode = other.ChromaMode;
        Cbp = other.Cbp;
        Qp = other.Qp;
        LumaDcCount = other.LumaDcCount;
        other.Modes.CopyTo(Modes, 0);
        other.LumaDc.CopyTo(LumaDc, 0);
        other.Luma.CopyTo(Luma, 0);
        other.LumaCount.CopyTo(LumaCount, 0);
        other.Luma8.CopyTo(Luma8, 0);
        other.Luma8Count.CopyTo(Luma8Count, 0);
        other.ChromaDc.CopyTo(ChromaDc, 0);
        other.ChromaDcCount.CopyTo(ChromaDcCount, 0);
        other.ChromaAc.CopyTo(ChromaAc, 0);
        other.ChromaAcCount.CopyTo(ChromaAcCount, 0);
    }
}
