namespace Egami.Formats.Av1;

/// <summary>Block sizes, in libaom's BLOCK_SIZE order (the generated tables are indexed by it).</summary>
internal static class Bs
{
    internal const int B4x4 = 0, B4x8 = 1, B8x4 = 2, B8x8 = 3, B8x16 = 4, B16x8 = 5, B16x16 = 6, B16x32 = 7, B32x16 = 8, B32x32 = 9,
        B32x64 = 10, B64x32 = 11, B64x64 = 12, B64x128 = 13, B128x64 = 14, B128x128 = 15, B4x16 = 16, B16x4 = 17, B8x32 = 18,
        B32x8 = 19, B16x64 = 20, B64x16 = 21, Count = 22, Invalid = 255;
}

/// <summary>Transform sizes, in libaom's TX_SIZE order.</summary>
internal static class Tx
{
    internal const int T4x4 = 0, T8x8 = 1, T16x16 = 2, T32x32 = 3, T64x64 = 4, T4x8 = 5, T8x4 = 6, T8x16 = 7, T16x8 = 8, T16x32 = 9,
        T32x16 = 10, T32x64 = 11, T64x32 = 12, T4x16 = 13, T16x4 = 14, T8x32 = 15, T32x8 = 16, T16x64 = 17, T64x16 = 18, Count = 19;

    /// <summary>The square sizes, which index the coefficient CDFs.</summary>
    internal const int SquareCount = 5;
}

/// <summary>Transform types, in libaom's TX_TYPE order.</summary>
internal static class TxType
{
    internal const int DctDct = 0, AdstDct = 1, DctAdst = 2, AdstAdst = 3, FlipadstDct = 4, DctFlipadst = 5, FlipadstFlipadst = 6,
        AdstFlipadst = 7, FlipadstAdst = 8, Idtx = 9, VDct = 10, HDct = 11, VAdst = 12, HAdst = 13, VFlipadst = 14, HFlipadst = 15;

    /// <summary>Gets the transform class: 0 two-dimensional, 1 horizontal (H_*), 2 vertical (V_*).</summary>
    internal static int Class(int type) => type switch
    {
        VDct or VAdst or VFlipadst => 2,
        HDct or HAdst or HFlipadst => 1,
        _ => 0,
    };
}

/// <summary>Intra prediction modes (PREDICTION_MODE up to PAETH_PRED, then UV_CFL_PRED as 13).</summary>
internal static class Pred
{
    internal const int Dc = 0, V = 1, H = 2, D45 = 3, D135 = 4, D113 = 5, D157 = 6, D203 = 7, D67 = 8, Smooth = 9, SmoothV = 10,
        SmoothH = 11, Paeth = 12, UvCfl = 13, Count = 13;

    internal static bool IsDirectional(int mode) => mode is >= V and <= D67;
}

/// <summary>Partition types.</summary>
internal static class Partition
{
    internal const int None = 0, Horz = 1, Vert = 2, Split = 3, HorzA = 4, HorzB = 5, VertA = 6, VertB = 7, Horz4 = 8, Vert4 = 9;
}

/// <summary>Lookups of the AV1 block and transform geometry, after libaom's common_data.h and blockd.h.</summary>
internal static partial class Av1Tables
{
    /// <summary>Gets the size a plane of the block covers (Subsampled_Size), or <see cref="Bs.Invalid"/>.</summary>
    internal static int PlaneBlockSize(int bsize, int ssx, int ssy) => SsSizeLookup[(bsize * 4) + (ssx * 2) + ssy];

    /// <summary>Gets the subsize of a partition of a square block.</summary>
    internal static int PartitionSubsize(int bsize, int partition)
    {
        int square = bsize switch
        {
            Bs.B4x4 => 0,
            Bs.B8x8 => 1,
            Bs.B16x16 => 2,
            Bs.B32x32 => 3,
            Bs.B64x64 => 4,
            Bs.B128x128 => 5,
            _ => -1,
        };
        return square < 0 ? Bs.Invalid : SubsizeLookup[(partition * 6) + square];
    }

    /// <summary>Gets the transform size whose coefficients are coded: 64-point sizes code only 32 of them.</summary>
    internal static int AdjustedTxSize(int txSize) => txSize switch
    {
        Tx.T64x64 or Tx.T64x32 or Tx.T32x64 => Tx.T32x32,
        Tx.T64x16 => Tx.T32x16,
        Tx.T16x64 => Tx.T16x32,
        _ => txSize,
    };

    /// <summary>Gets the chroma transform size of a block.</summary>
    internal static int MaxUvTxSize(int bsize, int ssx, int ssy) => AdjustedTxSize(MaxTxSizeRectLookup[PlaneBlockSize(bsize, ssx, ssy)]);

    /// <summary>The transform type set types (TxSetType).</summary>
    internal const int SetDctOnly = 0, SetDctIdtx = 1, SetDtt4Idtx = 2, SetDtt4Idtx1dDct = 3, SetDtt9Idtx1dDct = 4, SetAll16 = 5;

    /// <summary>av1_num_ext_tx_set.</summary>
    internal static ReadOnlySpan<byte> NumExtTxSet => [1, 2, 5, 7, 12, 16];

    /// <summary>av1_ext_tx_inv[set type][symbol].</summary>
    internal static readonly byte[] ExtTxInverse =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        9, 0, 3, 1, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        9, 0, 10, 11, 3, 1, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        9, 10, 11, 0, 1, 2, 4, 5, 3, 6, 7, 8, 0, 0, 0, 0,
        9, 10, 11, 12, 13, 14, 15, 0, 1, 2, 4, 5, 3, 6, 7, 8,
    ];

    /// <summary>av1_ext_tx_used[set type][tx type].</summary>
    internal static readonly ushort[] ExtTxUsedMask = [0x0001, 0x0201, 0x020F, 0x0E0F, 0x0FFF, 0xFFFF];

    /// <summary>Gets the transform set type of a transform size (av1_get_ext_tx_set_type).</summary>
    internal static int ExtTxSetType(int txSize, bool inter, bool reducedSet)
    {
        int sqrUp = TxSizeSqrUpMap[txSize];
        if (sqrUp > Tx.T32x32)
        {
            return SetDctOnly;
        }

        if (sqrUp == Tx.T32x32)
        {
            return inter ? SetDctIdtx : SetDctOnly;
        }

        if (reducedSet)
        {
            return inter ? SetDctIdtx : SetDtt4Idtx;
        }

        bool sqr16 = TxSizeSqrMap[txSize] == Tx.T16x16;
        return inter ? (sqr16 ? SetDtt9Idtx1dDct : SetAll16) : (sqr16 ? SetDtt4Idtx : SetDtt4Idtx1dDct);
    }

    /// <summary>Gets the index of an intra set type among the intra transform CDF sets (ext_tx_set_index), or -1.</summary>
    internal static int IntraExtTxSet(int setType) => setType switch
    {
        SetDctOnly => 0,
        SetDtt4Idtx1dDct => 1,
        SetDtt4Idtx => 2,
        _ => -1,
    };

    /// <summary>The transform type an intra mode implies for chroma (intra_mode_to_tx_type).</summary>
    internal static ReadOnlySpan<byte> IntraModeToTxType =>
        [TxType.DctDct, TxType.AdstDct, TxType.DctAdst, TxType.DctDct, TxType.AdstAdst, TxType.AdstDct, TxType.DctAdst, TxType.DctAdst,
            TxType.AdstDct, TxType.AdstAdst, TxType.AdstDct, TxType.DctAdst, TxType.AdstAdst];

    /// <summary>The direction each filter intra mode counts as (fimode_to_intradir).</summary>
    internal static ReadOnlySpan<byte> FilterIntraToMode => [Pred.Dc, Pred.V, Pred.H, Pred.D157, Pred.Dc];

    /// <summary>partition_context_lookup: the above and left partition context of each block size.</summary>
    internal static ReadOnlySpan<byte> PartitionContextAbove => [31, 31, 30, 30, 30, 28, 28, 28, 24, 24, 24, 16, 16, 16, 0, 0, 31, 28, 30, 24, 28, 16];

    /// <inheritdoc cref="PartitionContextAbove"/>
    internal static ReadOnlySpan<byte> PartitionContextLeft => [31, 30, 31, 30, 28, 30, 28, 24, 28, 24, 16, 24, 16, 0, 16, 0, 28, 31, 24, 30, 16, 28];

    /// <summary>bsize_to_max_depth: how far a block's transform can split.</summary>
    internal static ReadOnlySpan<byte> MaxTxDepth => [0, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2];

    /// <summary>bsize_to_tx_size_cat plus one.</summary>
    internal static ReadOnlySpan<byte> TxSizeDepthCategory => [0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 4, 4, 4, 2, 2, 3, 3, 4, 4];

    /// <summary>
    /// Gets where a transform size's inverse quantizer matrix starts in a level's 3344 entries (av1_qm_init): the sizes that code
    /// all their coefficients, in transform size order.
    /// </summary>
    internal static int QmOffset(int adjustedTxSize) => adjustedTxSize switch
    {
        Tx.T4x4 => 0,
        Tx.T8x8 => 16,
        Tx.T16x16 => 80,
        Tx.T32x32 => 336,
        Tx.T4x8 => 1360,
        Tx.T8x4 => 1392,
        Tx.T8x16 => 1424,
        Tx.T16x8 => 1552,
        Tx.T16x32 => 1680,
        Tx.T32x16 => 2192,
        Tx.T4x16 => 2704,
        Tx.T16x4 => 2768,
        Tx.T8x32 => 2832,
        _ => 3088,
    };

    /// <summary>Gets the radii of a self-guided filter parameter set (av1_sgr_params): 0 where a pass is off.</summary>
    internal static (int R0, int R1) SgrRadii(int set) => set < 10 ? (2, 1) : set < 14 ? (0, 1) : (2, 0);

    /// <summary>Gets the coefficient context offsets of a transform size (libaom's av1_nz_map_ctx_offset, whose odd pairings it keeps).</summary>
    internal static sbyte[] NzMapCtxOffset(int txSize) => txSize switch
    {
        Tx.T4x4 => NzMapCtxOffset4x4,
        Tx.T8x8 => NzMapCtxOffset8x8,
        Tx.T16x16 => NzMapCtxOffset16x16,
        Tx.T32x32 or Tx.T64x64 => NzMapCtxOffset32x32,
        Tx.T4x8 => NzMapCtxOffset4x8,
        Tx.T8x4 or Tx.T16x4 => NzMapCtxOffset16x4,
        Tx.T8x16 => NzMapCtxOffset8x16,
        Tx.T16x8 or Tx.T32x8 => NzMapCtxOffset32x8,
        Tx.T16x32 => NzMapCtxOffset16x32,
        Tx.T32x16 or Tx.T64x16 => NzMapCtxOffset32x16,
        Tx.T32x64 or Tx.T16x64 => NzMapCtxOffset32x64,
        Tx.T64x32 => NzMapCtxOffset64x32,
        Tx.T4x16 => NzMapCtxOffset4x16,
        _ => NzMapCtxOffset8x32,
    };
}
