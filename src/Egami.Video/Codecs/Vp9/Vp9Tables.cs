namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// VP9's enumerations and small constants, with libvpx's values (vp9_enums.h, vp9_blockd.h, vp9_entropy*.h). The large tables
/// are generated into Vp9Tables.Data.cs by tools/video/make-vp9-tables.py.
/// </summary>
internal static partial class Vp9Tables
{
    // Block sizes (BLOCK_SIZE).
    internal const int Block4x4 = 0;
    internal const int Block4x8 = 1;
    internal const int Block8x4 = 2;
    internal const int Block8x8 = 3;
    internal const int Block8x16 = 4;
    internal const int Block16x8 = 5;
    internal const int Block16x16 = 6;
    internal const int Block32x32 = 9;
    internal const int Block64x64 = 12;
    internal const int BlockSizes = 13;
    internal const int BlockInvalid = 13;

    // Partition types.
    internal const int PartitionNone = 0;
    internal const int PartitionHorz = 1;
    internal const int PartitionVert = 2;
    internal const int PartitionSplit = 3;

    // Transform sizes and modes.
    internal const int Tx4x4 = 0;
    internal const int Tx8x8 = 1;
    internal const int Tx16x16 = 2;
    internal const int Tx32x32 = 3;
    internal const int Allow32x32 = 3;
    internal const int TxModeSelect = 4;

    // Transform types.
    internal const int DctDct = 0;

    // Prediction modes.
    internal const int DcPred = 0;
    internal const int VPred = 1;
    internal const int HPred = 2;
    internal const int D45Pred = 3;
    internal const int D135Pred = 4;
    internal const int D117Pred = 5;
    internal const int D153Pred = 6;
    internal const int D207Pred = 7;
    internal const int D63Pred = 8;
    internal const int TmPred = 9;
    internal const int IntraModes = 10;
    internal const int NearestMv = 10;
    internal const int NearMv = 11;
    internal const int ZeroMv = 12;
    internal const int NewMv = 13;

    // Reference frames.
    internal const int NoRefFrame = -1;
    internal const int IntraFrame = 0;
    internal const int LastFrame = 1;
    internal const int GoldenFrame = 2;
    internal const int AltrefFrame = 3;

    // Reference modes.
    internal const int SingleReference = 0;
    internal const int CompoundReference = 1;
    internal const int ReferenceModeSelect = 2;

    // Interpolation filters.
    internal const int EightTap = 0;
    internal const int EightTapSmooth = 1;
    internal const int EightTapSharp = 2;
    internal const int Bilinear = 3;
    internal const int SwitchableFilters = 3;
    internal const int Switchable = 4;

    // Segmentation features.
    internal const int SegLvlAltQ = 0;
    internal const int SegLvlAltLf = 1;
    internal const int SegLvlRefFrame = 2;
    internal const int SegLvlSkip = 3;
    internal const int MaxSegments = 8;

    // Intra edge needs (reconintra.c's extend_modes).
    internal const int NeedLeft = 1 << 1;
    internal const int NeedAbove = 1 << 2;
    internal const int NeedAboveRight = 1 << 3;

    internal const int MaxLoopFilter = 63;
    internal const int MvBorder = 16 << 3;
    internal const int MvLow = -(1 << 14);
    internal const int MvUpp = 1 << 14;
    internal const int CompandedMvrefThresh = 8;

    /// <summary>The seg feature value ranges (vp9_seg_common.c): bits to read for each feature.</summary>
    internal static ReadOnlySpan<int> SegFeatureDataMax => [255, 63, 3, 0];

    internal static ReadOnlySpan<bool> SegFeatureSigned => [true, true, false, false];

    /// <summary>libvpx's clamp: the low bound wins when the bounds cross, where Math.Clamp would throw.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int Clamp(int value, int low, int high) => value < low ? low : value > high ? high : value;
}
