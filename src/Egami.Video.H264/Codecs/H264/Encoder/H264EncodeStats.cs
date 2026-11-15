using System.Diagnostics;

namespace Egami.Video.Codecs.H264;

/// <summary>The kinds of encoder work <see cref="H264EncodeStats"/> counts.</summary>
internal enum H264EncodeStat
{
    /// <summary>Macroblocks coded.</summary>
    Macroblocks,

    /// <summary>Full RD codings: each macroblock candidate quantized, reconstructed and counted with <c>Rate</c>.</summary>
    RdCodings,

    /// <summary>Inter candidates RD-coded (<c>CodeInter</c>).</summary>
    InterCandidates,

    /// <summary>Inter luma codings: one per transform size tried for an inter candidate.</summary>
    InterLumaCodings,

    /// <summary>Intra RD passes, each coding I16, I4 and (from effort 1 with the 8x8 transform) I8.</summary>
    IntraPasses,

    /// <summary>Blocks RDOQ ran on.</summary>
    RdoqBlocks,

    /// <summary>Whole-block rate counts inside RDOQ.</summary>
    RdoqRateCounts,

    /// <summary>Block searches: one partition in one reference of one list.</summary>
    BlockSearches,

    /// <summary>Whole-sample search costs (a SAD, or a SATD for a weighted reference).</summary>
    IntegerCosts,

    /// <summary>Half and quarter-sample search costs (motion compensation and a SATD).</summary>
    SubpelCosts,

    /// <summary>B partition costs of list 0, list 1, bi-prediction or direct (motion compensation and a SATD).</summary>
    PartCosts,

    /// <summary>SATD calls of the macroblock encoder, intra mode decisions included.</summary>
    SatdCalls,

    /// <summary>The samples those SATD calls cover.</summary>
    SatdSamples,

    /// <summary>P_Skip and B_Skip macroblocks taken before any search, their residual too small to code.</summary>
    EarlySkips,

    /// <summary>SA8D calls (the 8x8 Hadamard cost that, against SATD, picks an inter transform size).</summary>
    Sa8dCalls,

    /// <summary>Second candidates that repeat the 16x16 prediction (every part the same references and vectors).</summary>
    DupCandidates,
}

/// <summary>The parts of a macroblock's coded bits that <see cref="H264EncodeStats"/> splits CABAC's bits into.</summary>
internal enum H264BitPart
{
    /// <summary>mb_skip_flag, mb_type, sub_mb_type, intra modes, coded_block_pattern, transform_size_8x8_flag, end_of_slice_flag.</summary>
    Modes,

    /// <summary>mb_qp_delta.</summary>
    QpDelta,

    /// <summary>ref_idx_l0 and ref_idx_l1.</summary>
    Refs,

    /// <summary>mvd_l0 and mvd_l1.</summary>
    Vectors,

    /// <summary>The residual blocks.</summary>
    Residual,
}

/// <summary>The kinds of macroblock whose shares <see cref="H264EncodeStats"/> counts, by picture.</summary>
internal enum H264BlockShare
{
    /// <summary>P_Skip or B_Skip.</summary>
    Skip,

    /// <summary>B_Direct_16x16 without a residual.</summary>
    Direct,

    /// <summary>B_Direct_16x16 with a residual.</summary>
    DirectResidual,

    /// <summary>An inter macroblock with partitions.</summary>
    Inter,

    /// <summary>An intra macroblock.</summary>
    Intra,
}

/// <summary>
/// Test and profiling counters of the H.264 encoder's work per macroblock, by picture type (P, B, I). The counting calls are
/// <see cref="ConditionalAttribute"/> on <c>EGAMI_H264_STATS</c>, which the Debug build and a build with
/// <c>-p:EgamiH264Stats=true</c> define: a Release build compiles them away, arguments and all, so its encoder is unchanged.
/// Each thread counts into its own array; <see cref="Snapshot"/> adds them up.
/// </summary>
internal static class H264EncodeStats
{
#if EGAMI_H264_STATS
    /// <summary>Whether this build counts.</summary>
    public static bool Enabled => true;
#else
    /// <summary>Whether this build counts.</summary>
    public static bool Enabled => false;
#endif

    /// <summary>The picture types, in the order of <see cref="Snapshot"/>'s first index.</summary>
    public static readonly string[] Types = ["P", "B", "I"];

    /// <summary>The picture kinds of <see cref="PictureSnapshot"/>'s first index: B pictures split into referenced and not.</summary>
    public static readonly string[] PictureKinds = ["I", "P", "Bref", "B"];

    /// <summary>
    /// The columns of <see cref="PictureSnapshot"/>: pictures, their NAL units' bits, the CABAC macroblock bits of each
    /// <see cref="H264BitPart"/> (in 1/32768 bit, the rate counter's estimate of the final coding, so the parts add up to a little
    /// less than the bits: the rest is slice headers and the coder's own rounding), and the macroblocks of each
    /// <see cref="H264BlockShare"/>.
    /// </summary>
    public static readonly string[] PictureColumns =
        ["Pictures", "Bits", .. Enum.GetNames<H264BitPart>().Select(n => n + "Bits"), .. Enum.GetNames<H264BlockShare>()];

    private static readonly int Count = Enum.GetValues<H264EncodeStat>().Length;
    private static readonly int Parts = Enum.GetValues<H264BitPart>().Length;
    private static readonly int Shares = Enum.GetValues<H264BlockShare>().Length;
    private static readonly List<long[]> All = [];
    private static readonly long[,] Pictures = new long[4, 2 + Parts + Shares];

    [ThreadStatic]
    private static long[]? _counts;

    [ThreadStatic]
    private static int _type;

#if EGAMI_H264_STATS
    [ThreadStatic]
    private static int _part;

    /// <summary>Gets the part this thread's CABAC bins count to (<see cref="Part"/>).</summary>
    public static int CurrentPart => _part;
#endif

    /// <summary>Makes <paramref name="part"/> the one this thread's next CABAC bins count to in a counting build's final coding.</summary>
    [Conditional("EGAMI_H264_STATS")]
    public static void Part(H264BitPart part)
    {
#if EGAMI_H264_STATS
        _part = (int)part;
#endif
    }

    /// <summary>Adds a coded picture: its kind (an index of <see cref="PictureKinds"/>), its bits, and its slices' parts and shares.</summary>
    public static void AddPicture(int kind, long bits, ReadOnlySpan<long> parts, ReadOnlySpan<long> shares)
    {
        lock (All)
        {
            Pictures[kind, 0]++;
            Pictures[kind, 1] += bits;
            for (int i = 0; i < Parts; i++)
            {
                Pictures[kind, 2 + i] += parts[i];
            }

            for (int i = 0; i < Shares; i++)
            {
                Pictures[kind, 2 + Parts + i] += shares[i];
            }
        }
    }

    /// <summary>The picture counts so far, [kind, column] (<see cref="PictureKinds"/>, <see cref="PictureColumns"/>).</summary>
    public static long[,] PictureSnapshot()
    {
        lock (All)
        {
            return (long[,])Pictures.Clone();
        }
    }

    /// <summary>Counts a macroblock of a slice type and makes that type the one this thread's next counts go to.</summary>
    [Conditional("EGAMI_H264_STATS")]
    public static void Macroblock(H264SliceType type)
    {
        _type = type switch { H264SliceType.P => 0, H264SliceType.B => 1, _ => 2 };
        Add(H264EncodeStat.Macroblocks);
    }

    /// <summary>Adds to a counter of the current macroblock's picture type.</summary>
    [Conditional("EGAMI_H264_STATS")]
    public static void Add(H264EncodeStat stat, long n = 1)
    {
        long[]? counts = _counts;
        if (counts is null)
        {
            counts = _counts = new long[3 * Count];
            lock (All)
            {
                All.Add(counts);
            }
        }

        counts[(_type * Count) + (int)stat] += n;
    }

    /// <summary>The counts so far, [type, stat], summed over all threads.</summary>
    public static long[,] Snapshot()
    {
        long[,] sum = new long[3, Count];
        lock (All)
        {
            foreach (long[] counts in All)
            {
                for (int t = 0; t < 3; t++)
                {
                    for (int s = 0; s < Count; s++)
                    {
                        sum[t, s] += counts[(t * Count) + s];
                    }
                }
            }
        }

        return sum;
    }

    /// <summary>Sets every thread's counts to zero; call it when no encoder is running.</summary>
    public static void Reset()
    {
        lock (All)
        {
            foreach (long[] counts in All)
            {
                Array.Clear(counts);
            }

            Array.Clear(Pictures);
        }
    }
}
