namespace Egami.Video.Codecs.H264;

/// <summary>
/// Motion search shortcuts that save cycles for some compression (task 12); <see cref="H264MbEncoder.DefaultShortcuts"/> says
/// which an effort takes. Measured on the four 720p clips at effort 4 against the full search, before the macroblock tree:
/// the worst clip's BD-rate and the cycle change with profile-guided optimization.
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
    /// 16x8 and 8x16 only, never 8x8, each part searched from its start vectors (the predicted vector, the 16x16 vector and
    /// the neighbours') without a pattern search, then refined to sub-samples as usual (speed round 9). Without it 16x8 and
    /// 8x16 are searched first and 8x8 only where one of them beats 16x16. Until round 9 this shortcut searched 8x8 first and
    /// the halves only where 8x8 beat 16x16: on six of eight clips 8x8 was a net loss as that gate.
    /// </summary>
    HalvesOnly = 2,

    /// <summary><see cref="QuickSubpel"/> in B pictures only, where no picture or few predict from the result.</summary>
    QuickSubpelB = 4,
}
