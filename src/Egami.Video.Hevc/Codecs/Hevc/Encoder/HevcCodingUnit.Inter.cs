using System.Runtime.CompilerServices;

namespace Egami.Heic.Hevc;

/// <summary>
/// The inter decisions of a coding unit in a P or B slice, beside the HEIC encoder's intra ones: whether it is inter or skipped,
/// its partitioning and each prediction unit's motion and how that is coded.
/// </summary>
internal sealed partial class HevcCodingUnit
{
    /// <summary>PartMode 2Nx2N.</summary>
    internal const int Part2Nx2N = 0;

    /// <summary>PartMode 2NxN: two halves, one above the other.</summary>
    internal const int Part2NxN = 1;

    /// <summary>PartMode Nx2N: two halves side by side.</summary>
    internal const int PartNx2N = 2;

    /// <summary>Gets or sets whether the unit is predicted from other pictures (CuPredMode MODE_INTER).</summary>
    internal bool Inter { get; set; }

    /// <summary>Gets or sets cu_skip_flag: one merged 2Nx2N prediction unit and no residual.</summary>
    internal bool Skip { get; set; }

    /// <summary>Gets or sets PartMode of an inter unit (<see cref="Part2Nx2N"/>, <see cref="Part2NxN"/>, <see cref="PartNx2N"/>).</summary>
    internal int PartMode { get; set; }

    /// <summary>Gets or sets rqt_root_cbf: whether an inter unit codes a transform tree.</summary>
    internal bool RootCbf { get; set; }

    /// <summary>The prediction units of an inter unit, one or two.</summary>
    internal PredictionUnits Units;

    /// <summary>Gets the number of prediction units of PartMode.</summary>
    internal int UnitCount => PartMode == Part2Nx2N ? 1 : 2;

    /// <summary>Gets the place and size of prediction unit <paramref name="part"/> of a PartMode in a coding unit.</summary>
    internal static (int X, int Y, int Width, int Height) UnitRect(int x0, int y0, int log2, int partMode, int part)
    {
        int size = 1 << log2, half = size >> 1;
        return partMode switch
        {
            Part2NxN => (x0, y0 + (part * half), size, half),
            PartNx2N => (x0 + (part * half), y0, half, size),
            _ => (x0, y0, size, size),
        };
    }

    /// <summary>Two prediction units.</summary>
    [InlineArray(2)]
    internal struct PredictionUnits
    {
        private HevcPredictionUnit _first;
    }
}

/// <summary>
/// A prediction unit's motion and its syntax: merged (with merge_idx), or each list's reference index, motion vector difference
/// and predictor flag. Vectors are packed as the decoder keeps them: horizontal in the low 16 bits, vertical in the high.
/// </summary>
internal struct HevcPredictionUnit
{
    /// <summary>predFlagL0 (bit 0) and predFlagL1 (bit 1).</summary>
    internal int Flags;

    /// <summary>merge_flag.</summary>
    internal bool Merge;

    /// <summary>merge_idx.</summary>
    internal int MergeIdx;

    /// <summary>refIdxL0 and refIdxL1.</summary>
    internal int RefIdx0, RefIdx1;

    /// <summary>mvL0 and mvL1.</summary>
    internal int Mv0, Mv1;

    /// <summary>MvdL0 and MvdL1 of an unmerged unit.</summary>
    internal int Mvd0, Mvd1;

    /// <summary>mvp_l0_flag and mvp_l1_flag of an unmerged unit.</summary>
    internal int MvpFlag0, MvpFlag1;
}
