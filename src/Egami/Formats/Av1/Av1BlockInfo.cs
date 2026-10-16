namespace Egami.Formats.Av1;

/// <summary>
/// The mode info of one coding block (libaom's MB_MODE_INFO): every 4x4 unit the block covers points at the same instance in the
/// frame's grid.
/// </summary>
internal sealed class Av1BlockInfo
{
    /// <summary>Gets or sets the block size (<see cref="Bs"/>).</summary>
    internal int Size { get; set; }

    /// <summary>Gets or sets the partition that made the block.</summary>
    internal int Partition { get; set; }

    /// <summary>Gets or sets the top row, in 4x4 units.</summary>
    internal int MiRow { get; set; }

    /// <summary>Gets or sets the left column, in 4x4 units.</summary>
    internal int MiCol { get; set; }

    /// <summary>Gets or sets the segment.</summary>
    internal int SegmentId { get; set; }

    /// <summary>Gets or sets whether the block codes no residual (skip_txfm).</summary>
    internal bool Skip { get; set; }

    /// <summary>Gets or sets the luma prediction mode (<see cref="Pred"/>).</summary>
    internal int YMode { get; set; }

    /// <summary>Gets or sets the chroma prediction mode, <see cref="Pred.UvCfl"/> for chroma from luma.</summary>
    internal int UvMode { get; set; }

    /// <summary>Gets or sets the luma angle delta (-3 to 3).</summary>
    internal int AngleDeltaY { get; set; }

    /// <summary>Gets or sets the chroma angle delta (-3 to 3).</summary>
    internal int AngleDeltaUv { get; set; }

    /// <summary>Gets or sets the chroma from luma alphas: the U magnitude index times 16 plus the V one.</summary>
    internal int CflAlphaIdx { get; set; }

    /// <summary>Gets or sets the chroma from luma joint sign.</summary>
    internal int CflAlphaSigns { get; set; }

    /// <summary>Gets or sets whether the block copies from the frame itself (intra block copy).</summary>
    internal bool UseIntrabc { get; set; }

    /// <summary>Gets or sets the intra block copy vector's row, in eighth samples.</summary>
    internal int MvRow { get; set; }

    /// <summary>Gets or sets the intra block copy vector's column, in eighth samples.</summary>
    internal int MvCol { get; set; }

    /// <summary>Gets the palette sizes of luma and chroma (0 for none).</summary>
    internal int[] PaletteSize { get; } = new int[2];

    /// <summary>Gets the palette colors: Y at 0, U at 8 and V at 16.</summary>
    internal ushort[] PaletteColors { get; } = new ushort[24];

    /// <summary>Gets or sets whether the block uses filter intra prediction.</summary>
    internal bool UseFilterIntra { get; set; }

    /// <summary>Gets or sets the filter intra mode.</summary>
    internal int FilterIntraMode { get; set; }

    /// <summary>Gets or sets the luma transform size (<see cref="Tx"/>); for an intra block copy, the last one its tree set.</summary>
    internal int TxSize { get; set; }

    /// <summary>Gets or sets the block's quantizer index (CurrentQIndex).</summary>
    internal int QIndex { get; set; }

    /// <summary>Gets the block's loop filter deltas (delta_lf, or delta_lf_from_base at 0 when not multi).</summary>
    internal int[] DeltaLf { get; } = new int[4];

    /// <summary>Gets whether the block is predicted from a reference: true for inter blocks and intra block copies.</summary>
    internal bool IsInter => UseIntrabc;

    /// <summary>Gets the chroma prediction mode as a luma one: chroma from luma counts as DC.</summary>
    internal int UvModeAsY => UvMode == Pred.UvCfl ? Pred.Dc : UvMode;
}
