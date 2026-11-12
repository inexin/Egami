namespace Egami.Video.Codecs.H264;

/// <summary>
/// Motion search shortcuts that save cycles for some compression, off by default (task 12). Measured on the four 720p
/// clips at effort 4 against the full search: the worst clip's BD-rate and the cycle change with profile-guided optimization.
/// </summary>
[Flags]
internal enum H264SearchShortcuts
{
    /// <summary>The full search.</summary>
    None = 0,

    /// <summary>
    /// Half samples by a SAD diamond and quarter samples by a SATD diamond instead of two (three from effort 5) square rounds
    /// by SATD; a second or later reference whose half-sample cost is more than 8/7 of the best so far stops there.
    /// +0.65 % BD-rate on the worst clip for 4 to 9 % fewer cycles.
    /// </summary>
    QuickSubpel = 1,

    /// <summary>
    /// 8x8 is searched first and 16x8 and 8x16 only where it beats 16x16. +0.94 % BD-rate on the worst clip for 4 to 13 %
    /// fewer cycles; both shortcuts together +1.71 % for 9 to 16 % fewer.
    /// </summary>
    PruneHalves = 2,
}
