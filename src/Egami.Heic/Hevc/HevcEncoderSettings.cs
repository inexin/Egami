namespace Egami.Heic.Hevc;

/// <summary>
/// What <see cref="HevcEncoder"/> codes one picture with: the size, chroma format and bit depth of its planes, the base QP and
/// effort, the colour description for the VUI, and the coding tools the parameter sets switch on.
/// </summary>
internal sealed record HevcEncoderSettings
{
    /// <summary>Gets the picture's width in luma samples (before padding to whole coding blocks).</summary>
    internal required int Width { get; init; }

    /// <summary>Gets the picture's height in luma samples.</summary>
    internal required int Height { get; init; }

    /// <summary>Gets chroma_format_idc: 0 monochrome, 1 4:2:0, 2 4:2:2, 3 4:4:4.</summary>
    internal int ChromaFormat { get; init; } = 1;

    /// <summary>Gets the bit depth of every component: 8, 10 or 12.</summary>
    internal int BitDepth { get; init; } = 8;

    /// <summary>Gets the QP of the slice (0 to 51, more at 10 and 12 bits counted from -QpBdOffset).</summary>
    internal int Qp { get; init; } = 30;

    /// <summary>Gets the effort, 0 (fastest) to 10 (smallest files).</summary>
    internal int Effort { get; init; } = 4;

    /// <summary>Gets whether every coding unit is coded without transform or quantization (cu_transquant_bypass_flag).</summary>
    internal bool Lossless { get; init; }

    /// <summary>Gets colour_primaries for the VUI.</summary>
    internal int ColourPrimaries { get; init; } = 1;

    /// <summary>Gets transfer_characteristics for the VUI.</summary>
    internal int TransferCharacteristics { get; init; } = 13;

    /// <summary>Gets matrix_coeffs for the VUI.</summary>
    internal int MatrixCoefficients { get; init; } = 6;

    /// <summary>Gets video_full_range_flag for the VUI.</summary>
    internal bool FullRange { get; init; } = true;

    /// <summary>Gets pps_cb_qp_offset and pps_cr_qp_offset.</summary>
    internal int ChromaQpOffset { get; init; }

    /// <summary>Gets whether coding units may change the QP (cu_qp_delta_enabled_flag), for the adaptive quantizer.</summary>
    internal bool AdaptiveQp { get; init; }

    /// <summary>Gets the size of a quantization group, log2 (4 to 6), when <see cref="AdaptiveQp"/> is on.</summary>
    internal int QpGroupLog2 { get; init; } = 5;

    /// <summary>Gets whether SAO is on (sample_adaptive_offset_enabled_flag and the slice's flags).</summary>
    internal bool Sao { get; init; } = true;

    /// <summary>Gets whether the deblocking filter runs.</summary>
    internal bool Deblocking { get; init; } = true;

    /// <summary>Gets whether sign data hiding is on.</summary>
    internal bool SignHiding { get; init; } = true;

    /// <summary>Gets whether 4x4 blocks may skip the transform (transform_skip_enabled_flag).</summary>
    internal bool TransformSkip { get; init; } = true;

    /// <summary>Gets max_transform_hierarchy_depth_intra.</summary>
    internal int MaxTransformDepth { get; init; } = 2;

    /// <summary>Gets whether CTB rows are wavefront substreams (entropy_coding_sync_enabled_flag).</summary>
    internal bool Wavefronts { get; init; } = true;

    /// <summary>Gets how many threads may code CTB rows at once (the output does not depend on it).</summary>
    internal int Parallelism { get; init; } = 1;

    /// <summary>Gets the log2 of the CTB size (6, 64x64, unless a test asks for less).</summary>
    internal int CtbLog2Size { get; init; } = 6;

    /// <summary>Gets SubWidthC.</summary>
    internal int SubWidthC => ChromaFormat is 1 or 2 ? 2 : 1;

    /// <summary>Gets SubHeightC.</summary>
    internal int SubHeightC => ChromaFormat == 1 ? 2 : 1;

    /// <summary>Gets the coded width: the width rounded up to the smallest coding block, 8.</summary>
    internal int CodedWidth => (Width + 7) & ~7;

    /// <summary>Gets the coded height.</summary>
    internal int CodedHeight => (Height + 7) & ~7;
}
