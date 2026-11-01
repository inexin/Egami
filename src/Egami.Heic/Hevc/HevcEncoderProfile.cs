namespace Egami.Heic.Hevc;

/// <summary>
/// How hard the encoder's search works at an effort from 0 to 10: the coding unit sizes it tries, how many intra modes survive
/// the rough (SATD) decision into the full rate-distortion one, the transform tree depth, the chroma modes, and which tools run.
/// </summary>
internal sealed record HevcEncoderProfile
{
    /// <summary>Gets the smallest coding unit tried, log2 (3 is 8x8; larger blocks at the picture's edge are split anyway).</summary>
    internal int MinCuLog2 { get; init; } = 3;

    /// <summary>Gets the largest coding unit tried, log2.</summary>
    internal int MaxCuLog2 { get; init; } = 6;

    /// <summary>Gets whether 8x8 coding units try four 4x4 prediction units.</summary>
    internal bool NxN { get; init; } = true;

    /// <summary>Gets how many of the 35 intra modes the rough decision checks: 35, or the planar, DC and every other angle.</summary>
    internal bool AllRoughModes { get; init; } = true;

    /// <summary>Gets how many modes, best by SATD, go to the full decision for blocks of 8x8 and smaller.</summary>
    internal int SmallCandidates { get; init; } = 8;

    /// <summary>Gets how many go to the full decision for 16x16 and larger blocks.</summary>
    internal int LargeCandidates { get; init; } = 3;

    /// <summary>Gets whether the most probable modes join the full decision even when the SATD ranks them lower.</summary>
    internal bool MostProbableCandidates { get; init; } = true;

    /// <summary>Gets max_transform_hierarchy_depth_intra, the transform splits below a coding unit.</summary>
    internal int TransformDepth { get; init; } = 2;

    /// <summary>Gets whether transform splits are searched (otherwise only the forced ones are made).</summary>
    internal bool TransformSearch { get; init; } = true;

    /// <summary>Gets whether all five chroma modes are tried (otherwise the luma mode and planar).</summary>
    internal bool AllChromaModes { get; init; } = true;

    /// <summary>Gets whether quantization optimizes the rate (RDOQ) rather than rounding with a dead zone.</summary>
    internal bool Rdoq { get; init; } = true;

    /// <summary>Gets whether 4x4 blocks try transform skip.</summary>
    internal bool TransformSkip { get; init; } = true;

    /// <summary>Gets whether SAO parameters are searched.</summary>
    internal bool Sao { get; init; } = true;

    /// <summary>Gets whether a coding unit with no coefficients at all is not split further (an early stop).</summary>
    internal bool EarlyStop { get; init; } = true;

    /// <summary>Gets the profile for an effort from 0 to 10.</summary>
    internal static HevcEncoderProfile For(int effort) => Math.Clamp(effort, 0, 10) switch
    {
        0 => new() { MinCuLog2 = 4, MaxCuLog2 = 5, NxN = false, AllRoughModes = false, SmallCandidates = 1, LargeCandidates = 1, MostProbableCandidates = false, TransformDepth = 1, TransformSearch = false, AllChromaModes = false, Rdoq = false, TransformSkip = false, Sao = false },
        1 => new() { MinCuLog2 = 3, MaxCuLog2 = 5, NxN = false, AllRoughModes = false, SmallCandidates = 2, LargeCandidates = 1, MostProbableCandidates = false, TransformDepth = 1, TransformSearch = false, AllChromaModes = false, Rdoq = false, TransformSkip = false, Sao = true },
        2 => new() { MaxCuLog2 = 5, NxN = false, AllRoughModes = false, SmallCandidates = 2, LargeCandidates = 1, TransformDepth = 1, TransformSearch = false, AllChromaModes = false, Rdoq = true, TransformSkip = false },
        3 => new() { MaxCuLog2 = 5, AllRoughModes = false, SmallCandidates = 3, LargeCandidates = 2, TransformDepth = 1, TransformSearch = false, AllChromaModes = false },
        4 => new() { AllRoughModes = false, SmallCandidates = 3, LargeCandidates = 2, MostProbableCandidates = false, TransformDepth = 1, AllChromaModes = false },
        5 => new() { SmallCandidates = 4, LargeCandidates = 2, TransformDepth = 1 },
        6 => new() { SmallCandidates = 5, LargeCandidates = 3, TransformDepth = 2 },
        7 => new() { SmallCandidates = 6, LargeCandidates = 3, TransformDepth = 2, EarlyStop = false },
        8 => new() { SmallCandidates = 8, LargeCandidates = 4, TransformDepth = 3, EarlyStop = false },
        9 => new() { SmallCandidates = 12, LargeCandidates = 6, TransformDepth = 3, EarlyStop = false },
        _ => new() { SmallCandidates = 35, LargeCandidates = 35, TransformDepth = 4, EarlyStop = false },
    };
}
