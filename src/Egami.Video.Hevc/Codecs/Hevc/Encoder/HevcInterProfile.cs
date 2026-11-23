using Egami.Heic.Hevc;

namespace Egami.Video.Codecs.Hevc.Encoder;

/// <summary>
/// How hard the inter search works at an effort from 0 to 10 (HEVC encoder design, coding tools table), beside the HEIC
/// encoder's intra profile: the coding unit sizes of P and B pictures, references, merge candidates, partitions, motion search, the
/// inter transform tree, how many candidates get a full RD coding, and the early terminations.
/// </summary>
internal sealed record HevcInterProfile
{
    /// <summary>Gets the smallest coding unit tried in P and B pictures, log2.</summary>
    internal int MinCuLog2 { get; init; } = 3;

    /// <summary>Gets the largest coding unit tried in P and B pictures, log2.</summary>
    internal int MaxCuLog2 { get; init; } = 6;

    /// <summary>Gets the CTB size, log2: the unit of the wavefront rows, which threads code two CTBs apart.</summary>
    internal int CtbLog2 { get; init; } = 6;

    /// <summary>Gets how many earlier pictures a P picture predicts from (num_ref_idx_l0_active).</summary>
    internal int References { get; init; } = 3;

    /// <summary>Gets how many earlier pictures list 0 of a B picture holds (list 1 holds one).</summary>
    internal int BReferences { get; init; } = 1;

    /// <summary>Gets whether a bi-predicted 2Nx2N unit of a B picture refines its two vectors together.</summary>
    internal bool BiRefine { get; init; } = true;

    /// <summary>Gets whether a B picture's 2Nx2N runner-up between one list and both is coded in full when within 17/16 of the best.</summary>
    internal bool BAlternative { get; init; } = true;

    /// <summary>Gets whether a B picture's motion search also starts from the last P picture's vectors, scaled.</summary>
    internal bool BSeeds { get; init; } = true;

    /// <summary>Gets whether the B pictures no picture predicts from code their kept residuals without RDOQ.</summary>
    internal bool PlainB { get; init; }

    /// <summary>
    /// Gets the share of λ that weighs bits against error in the RD codings of I and P pictures (<see
    /// cref="HevcEncoderSettings.RdLambdaScale"/>), as the H.264 encoder's: pictures others predict from are worth more than
    /// their own error says, so they spend more bits on it; B pictures keep the whole λ and the searches √λ unscaled. On the
    /// four clips at effort 4, 0.8 made SSIM-Y BD-rate 1.2 points better and PSNR-Y 0.3 points worse, no clip more than 0.3
    /// points worse on SSIM; the H.264 encoder's 0.6 was 1.7 points better on SSIM but 1.8 worse on PSNR (ducks_take_off 4.2).
    /// </summary>
    internal double IpLambdaScale { get; init; } = 0.8;

    /// <summary>Gets MaxNumMergeCand.</summary>
    internal int MergeCandidates { get; init; } = 3;

    /// <summary>Gets whether 2NxN and Nx2N partitions are tried.</summary>
    internal bool Halves { get; init; } = true;

    /// <summary>
    /// Gets whether the halves are tried only after 2Nx2N leaves a residual and the four smaller units, where tried, cost less than
    /// the unit whole; and searched in 2Nx2N's reference only.
    /// </summary>
    internal bool HalvesGate { get; init; } = true;

    /// <summary>Gets max_transform_hierarchy_depth_inter.</summary>
    internal int TransformDepth { get; init; } = 1;

    /// <summary>Gets the motion search range in whole samples.</summary>
    internal int SearchRange { get; init; } = 57;

    /// <summary>Gets whether the halves search only their start vectors at whole samples (2Nx2N's, the merge candidates and AMVP's), then refine.</summary>
    internal bool HalvesStartsOnly { get; init; } = true;

    /// <summary>Gets whether each half also weighs merging with a neighbour (its merge candidates by SATD) against its searched motion.</summary>
    internal bool HalvesMerges { get; init; } = true;

    /// <summary>Gets whether the sub-sample steps try eight neighbours rather than four.</summary>
    internal bool SquareSubpel { get; init; }

    /// <summary>Gets whether the search refines to quarter samples.</summary>
    internal bool QuarterSamples { get; init; } = true;

    /// <summary>Gets the ratio to the best SATD cost within which a second inter candidate also gets a full RD coding (0: one only).</summary>
    internal double SecondCandidateRatio { get; init; } = 1.25;

    /// <summary>Gets whether a skipped coding unit is not split further (early skip).</summary>
    internal bool EarlySkip { get; init; } = true;

    /// <summary>
    /// Gets whether a CTB's coding units shallower than the co-located CTBs of the first reference of each list (the least depth
    /// they used, one less where that reference coded at a QP no higher and its mean depth is under 1.5 times its least) are split
    /// without trying them whole. On at efforts 2 to 5: at 4 and 5 it saves 7 to 10 % of the work and a little rate.
    /// </summary>
    internal bool CoLocatedDepth { get; init; }

    /// <summary>Gets whether the split of a coding unit is not tried when merging it already costs less than the skips of that size around it.</summary>
    internal bool SplitStatistics { get; init; } = true;

    /// <summary>Gets whether the trials quantize with a dead zone and only the kept coding with RDOQ (else every trial with RDOQ).</summary>
    internal bool RdoqKeptOnly { get; init; } = true;

    /// <summary>Gets whether trials (with <see cref="RdoqKeptOnly"/>) are priced statically, their distortion in the transform domain.</summary>
    internal bool StaticTrials { get; init; } = true;

    /// <summary>Gets whether intra is tried only when the best inter coding has a residual and a smaller unit inside chose intra.</summary>
    internal bool IntraGate { get; init; } = true;

    /// <summary>Gets whether intra is tried, past <see cref="IntraGate"/>, only when a few intra modes' SATD beats the best inter prediction's.</summary>
    internal bool IntraSatdGate { get; init; } = true;

    /// <summary>
    /// Gets whether the kept trial is coded again in full only once it has beaten the four smaller units (compared at its trial cost);
    /// else before that comparison.
    /// </summary>
    internal bool RecodeAfterSplit { get; init; } = true;

    /// <summary>Gets whether intra in P and B pictures searches as the I pictures do (else one 2Nx2N mode, its transform tree unsearched).</summary>
    internal bool FullIntra { get; init; }

    /// <summary>Gets whether the inter transform tree's splits are searched (else a transform unit is the coding unit, split only where it must).</summary>
    internal bool TransformSplit { get; init; }

    /// <summary>
    /// Gets whether static trials (with <see cref="StaticTrials"/>) search the transform tree's splits too; else only the kept coding
    /// does, and the trials price the unsplit tree.
    /// </summary>
    internal bool TrialTransformSplit { get; init; } = true;

    /// <summary>The intra search of a P picture's coding units: the I pictures' unless <see cref="FullIntra"/> is off.</summary>
    internal HevcEncoderProfile IntraProfile(HevcEncoderProfile intra) => FullIntra ? intra : intra with
    {
        NxN = false,
        SmallCandidates = 1,
        LargeCandidates = 1,
        MostProbableCandidates = false,
        TransformSearch = false,
        AllChromaModes = false,
    };

    /// <summary>Gets or sets a change tests and measurements make to every profile on this thread (encoders made on it), or null.</summary>
    internal static Func<HevcInterProfile, HevcInterProfile>? Adjust
    {
        get => _adjust;
        set => _adjust = value;
    }

    [ThreadStatic]
    private static Func<HevcInterProfile, HevcInterProfile>? _adjust;

    /// <summary>Gets the profile for an effort from 0 to 10.</summary>
    internal static HevcInterProfile For(int effort) => Adjust is { } adjust ? adjust(Plain(effort)) : Plain(effort);

    private static HevcInterProfile Plain(int effort) => Math.Clamp(effort, 0, 10) switch
    {
        0 => new() { MinCuLog2 = 4, MaxCuLog2 = 5, References = 1, MergeCandidates = 2, Halves = false, TransformDepth = 0, SearchRange = 16, QuarterSamples = false, SecondCandidateRatio = 0 },
        1 => new() { MinCuLog2 = 4, MaxCuLog2 = 5, References = 1, MergeCandidates = 2, Halves = false, TransformDepth = 0, SearchRange = 24, SecondCandidateRatio = 0 },
        2 => new() { CoLocatedDepth = true, References = 2, MergeCandidates = 2, SearchRange = 32, SecondCandidateRatio = 0 },
        3 => new() { CoLocatedDepth = true, References = 2, MergeCandidates = 3, SearchRange = 48, TransformSplit = true, Halves = false },
        4 => new() { CoLocatedDepth = true, TransformSplit = true, Halves = false },
        5 => new() { CoLocatedDepth = true, BReferences = 2, References = 3, MergeCandidates = 4, TransformDepth = 2, TransformSplit = true, SquareSubpel = true, RdoqKeptOnly = false },
        6 => new() { BReferences = 2, References = 4, MergeCandidates = 5, TransformDepth = 2, TransformSplit = true, SquareSubpel = true, RdoqKeptOnly = false, SplitStatistics = false },
        7 => new() { BReferences = 2, References = 4, MergeCandidates = 5, TransformDepth = 2, TransformSplit = true, SquareSubpel = true, RdoqKeptOnly = false, SplitStatistics = false, SecondCandidateRatio = 1.5 },
        8 => new() { BReferences = 2, HalvesStartsOnly = false, References = 4, MergeCandidates = 5, TransformDepth = 3, TransformSplit = true, FullIntra = true, SquareSubpel = true, RdoqKeptOnly = false, SplitStatistics = false, SecondCandidateRatio = 2, IntraGate = false, HalvesGate = false },
        9 => new() { BReferences = 2, HalvesStartsOnly = false, References = 4, MergeCandidates = 5, TransformDepth = 3, TransformSplit = true, FullIntra = true, SearchRange = 96, SquareSubpel = true, RdoqKeptOnly = false, SplitStatistics = false, SecondCandidateRatio = 4, IntraGate = false, HalvesGate = false, EarlySkip = false },
        _ => new() { BReferences = 2, HalvesStartsOnly = false, References = 4, MergeCandidates = 5, TransformDepth = 3, TransformSplit = true, FullIntra = true, SearchRange = 128, SquareSubpel = true, RdoqKeptOnly = false, SplitStatistics = false, SecondCandidateRatio = 100, IntraGate = false, HalvesGate = false, EarlySkip = false },
    };
}
