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

    /// <summary>Gets or sets the first vector's row (an intra block copy's, or the first reference's), in eighth samples.</summary>
    internal int MvRow { get; set; }

    /// <summary>Gets or sets the first vector's column, in eighth samples.</summary>
    internal int MvCol { get; set; }

    /// <summary>Gets or sets the second reference's vector row, in eighth samples.</summary>
    internal int Mv1Row { get; set; }

    /// <summary>Gets or sets the second reference's vector column, in eighth samples.</summary>
    internal int Mv1Col { get; set; }

    /// <summary>Gets or sets the first reference frame (<see cref="Ref"/>): <see cref="Ref.Intra"/> for intra blocks.</summary>
    internal int RefFrame0 { get; set; }

    /// <summary>Gets or sets the second reference frame: <see cref="Ref.None"/> for one reference, <see cref="Ref.Intra"/> for inter-intra.</summary>
    internal int RefFrame1 { get; set; } = Ref.None;

    /// <summary>Gets or sets which of the reference vector stack's candidates a new or near mode starts from (ref_mv_idx).</summary>
    internal int RefMvIdx { get; set; }

    /// <summary>Gets or sets whether the block takes skip mode (two references, no residual, no other syntax).</summary>
    internal bool SkipMode { get; set; }

    /// <summary>Gets or sets the vertical interpolation filter (<see cref="Interp"/>), filter direction 0.</summary>
    internal int FilterY { get; set; }

    /// <summary>Gets or sets the horizontal interpolation filter, filter direction 1.</summary>
    internal int FilterX { get; set; }

    /// <summary>Gets or sets the motion mode: 0 translation, 1 overlapped (OBMC), 2 local warp.</summary>
    internal int MotionMode { get; set; }

    /// <summary>Gets or sets the inter-intra mode (II_DC to II_SMOOTH) when <see cref="RefFrame1"/> is intra.</summary>
    internal int InterintraMode { get; set; }

    /// <summary>Gets or sets whether inter-intra blends with a wedge.</summary>
    internal bool UseWedgeInterintra { get; set; }

    /// <summary>Gets or sets the inter-intra wedge.</summary>
    internal int InterintraWedgeIndex { get; set; }

    /// <summary>Gets or sets how two references combine (<see cref="Compound"/>).</summary>
    internal int CompoundType { get; set; }

    /// <summary>Gets or sets the compound wedge.</summary>
    internal int WedgeIndex { get; set; }

    /// <summary>Gets or sets the compound wedge's sign.</summary>
    internal int WedgeSign { get; set; }

    /// <summary>Gets or sets the difference-weighted compound's mask type.</summary>
    internal int MaskType { get; set; }

    /// <summary>Gets or sets comp_group_idx.</summary>
    internal int CompGroupIdx { get; set; }

    /// <summary>Gets or sets compound_idx.</summary>
    internal int CompoundIdx { get; set; } = 1;

    /// <summary>Gets or sets whether the segment came from the previous frame's map (seg_id_predicted).</summary>
    internal bool SegIdPredicted { get; set; }

    /// <summary>Gets or sets the local warp's parameters, when <see cref="MotionMode"/> is 2.</summary>
    internal int[]? WarpParams { get; set; }

    /// <summary>Gets or sets whether <see cref="WarpParams"/> make a valid warp (LocalValid and the shear check).</summary>
    internal bool WarpValid { get; set; }

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
    internal bool IsInter => UseIntrabc || RefFrame0 > Ref.Intra;

    /// <summary>Gets whether the block predicts from two reference frames.</summary>
    internal bool HasSecondRef => RefFrame1 > Ref.Intra;

    /// <summary>Gets the chroma prediction mode as a luma one: chroma from luma counts as DC.</summary>
    internal int UvModeAsY => UvMode == Pred.UvCfl ? Pred.Dc : UvMode;
}
