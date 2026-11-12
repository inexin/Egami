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

    private static readonly int Count = Enum.GetValues<H264EncodeStat>().Length;
    private static readonly List<long[]> All = [];

    [ThreadStatic]
    private static long[]? _counts;

    [ThreadStatic]
    private static int _type;

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
        }
    }
}
