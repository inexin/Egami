namespace Egami.Heic.Hevc;

/// <summary>
/// The coding decisions of one coding unit, as the encoder's search leaves them for the final CABAC pass: its place and size,
/// partitioning, intra modes, transquant bypass, the QP change it codes, and its transform tree with the coefficient levels.
/// </summary>
internal sealed class HevcCodingUnit
{
    /// <summary>Gets or sets the top-left luma sample.</summary>
    internal int X { get; set; }

    /// <summary>Gets or sets the top-left luma sample's row.</summary>
    internal int Y { get; set; }

    /// <summary>Gets or sets log2CbSize.</summary>
    internal int Log2 { get; set; }

    /// <summary>Gets or sets the coding quadtree depth.</summary>
    internal int Depth { get; set; }

    /// <summary>Gets or sets whether the unit is split into four prediction units (PART_NxN).</summary>
    internal bool NxN { get; set; }

    /// <summary>Gets or sets cu_transquant_bypass_flag.</summary>
    internal bool Bypass { get; set; }

    /// <summary>Gets IntraPredModeY of each prediction unit (one for 2Nx2N).</summary>
    internal int[] LumaModes { get; } = new int[4];

    /// <summary>Gets intra_chroma_pred_mode of each chroma prediction block (four only for NxN in 4:4:4).</summary>
    internal int[] ChromaSyntax { get; } = new int[4];

    /// <summary>Gets or sets CuQpDeltaVal when this unit codes it (its quantization group's first unit with coefficients).</summary>
    internal int QpDelta { get; set; }

    /// <summary>Gets or sets the QP the unit's coefficients were quantized with (QpY).</summary>
    internal int Qp { get; set; }

    /// <summary>Gets or sets the transform tree.</summary>
    internal HevcTransformNode Tree { get; set; } = null!;
}

/// <summary>
/// A node of a transform tree: split into four, or a transform unit with its coded block flags and levels. The chroma flags of a
/// 4:2:2 node come in pairs (the two square blocks of a chroma transform block).
/// </summary>
internal sealed class HevcTransformNode
{
    /// <summary>Gets or sets split_transform_flag.</summary>
    internal bool Split { get; set; }

    /// <summary>Gets or sets the four children of a split node, in z order.</summary>
    internal HevcTransformNode[]? Children { get; set; }

    /// <summary>Gets or sets cbf_cb of the node (the upper block for 4:2:2).</summary>
    internal bool Cb0 { get; set; }

    /// <summary>Gets or sets cbf_cb of the lower block (4:2:2).</summary>
    internal bool Cb1 { get; set; }

    /// <summary>Gets or sets cbf_cr of the node.</summary>
    internal bool Cr0 { get; set; }

    /// <summary>Gets or sets cbf_cr of the lower block (4:2:2).</summary>
    internal bool Cr1 { get; set; }

    /// <summary>Gets or sets cbf_luma of a transform unit.</summary>
    internal bool CbfLuma { get; set; }

    /// <summary>
    /// The levels of a transform unit's blocks, raster order, null where a block has none: luma, then Cb and Cr (with the lower
    /// 4:2:2 blocks at 3 and 4). Held in the node, so a node is one allocation.
    /// </summary>
    internal BlockLevels Levels;

    /// <summary>transform_skip_flag of each block, as <see cref="Levels"/>.</summary>
    internal BlockFlags TransformSkip;

    /// <summary>Gets the block of <paramref name="c"/> (0 luma, 1 Cb, 2 Cr), <paramref name="t"/> 1 for a lower 4:2:2 block.</summary>
    internal static int Block(int c, int t) => c + (2 * t);

    /// <summary>Five level arrays.</summary>
    [System.Runtime.CompilerServices.InlineArray(5)]
    internal struct BlockLevels
    {
        private short[]? _first;
    }

    /// <summary>Five flags.</summary>
    [System.Runtime.CompilerServices.InlineArray(5)]
    internal struct BlockFlags
    {
        private bool _first;
    }
}
