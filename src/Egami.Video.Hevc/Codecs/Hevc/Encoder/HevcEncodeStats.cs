using System.Diagnostics;

namespace Egami.Video.Codecs.Hevc.Encoder;

/// <summary>The kinds of encoder work <see cref="HevcEncodeStats"/> counts.</summary>
internal enum HevcEncodeStat
{
    /// <summary>Coding tree units decided.</summary>
    Ctus,

    /// <summary>Coding units in the final coding.</summary>
    CodingUnits,

    /// <summary>Full RD codings of a coding unit candidate: predicted, transformed, quantized, reconstructed and counted.</summary>
    RdCodings,

    /// <summary>Intra RD passes in inter pictures (each the HEIC search's mode decision for one coding unit).</summary>
    IntraPasses,

    /// <summary>Merge candidates priced (motion compensation and an SA8D).</summary>
    MergeCosts,

    /// <summary>Inter candidates RD-coded with motion found by search (AMVP).</summary>
    InterCandidates,

    /// <summary>Block searches: one prediction unit in one reference.</summary>
    BlockSearches,

    /// <summary>Whole-sample search costs (SAD).</summary>
    IntegerCosts,

    /// <summary>Half and quarter-sample search costs (SATD).</summary>
    SubpelCosts,

    /// <summary>Coding units coded as skip before any motion search (early skip).</summary>
    EarlySkips,

    /// <summary>Coding quadtree nodes whose split was not tried because of the skip and depth statistics.</summary>
    SplitsSkipped,
}

/// <summary>The kinds of coding unit whose shares <see cref="HevcEncodeStats"/> counts, by picture kind, in 8x8 luma blocks.</summary>
internal enum HevcBlockShare
{
    /// <summary>cu_skip_flag set.</summary>
    Skip,

    /// <summary>A merged 2Nx2N prediction unit with a residual.</summary>
    Merge,

    /// <summary>An inter coding unit with motion vector differences.</summary>
    Inter,

    /// <summary>An intra coding unit.</summary>
    Intra,
}

/// <summary>
/// Test and profiling counters of the HEVC video encoder's work, by picture type (P, B, I). The counting calls are
/// <see cref="ConditionalAttribute"/> on <c>EGAMI_HEVC_STATS</c>, which the Debug build and a build with
/// <c>-p:EgamiHevcStats=true</c> define: a Release build compiles them away, arguments and all, so its encoder is unchanged.
/// Each thread counts into its own array; <see cref="Snapshot"/> adds them up. The counts depend only on the input and the
/// options, not on threads or timing, so they compare between runs and machines.
/// </summary>
internal static class HevcEncodeStats
{
#if EGAMI_HEVC_STATS
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

    /// <summary>The columns of <see cref="PictureSnapshot"/>: pictures, their NAL units' bits, and the 8x8 blocks of each <see cref="HevcBlockShare"/>.</summary>
    public static readonly string[] PictureColumns = ["Pictures", "Bits", .. Enum.GetNames<HevcBlockShare>()];

    private static readonly int Count = Enum.GetValues<HevcEncodeStat>().Length;
    private static readonly int Shares = Enum.GetValues<HevcBlockShare>().Length;
    private static readonly List<long[]> All = [];
    private static readonly long[,] Pictures = new long[4, 2 + Shares];

    [ThreadStatic]
    private static long[]? _counts;

    [ThreadStatic]
    private static int _type;

    /// <summary>Makes a picture type (0 P, 1 B, 2 I) the one this thread's next counts go to.</summary>
    [Conditional("EGAMI_HEVC_STATS")]
    public static void Picture(int type) => _type = type;

    /// <summary>Adds a coded picture: its kind (an index of <see cref="PictureKinds"/>), its bits and the 8x8 blocks of each share.</summary>
    [Conditional("EGAMI_HEVC_STATS")]
    public static void AddPicture(int kind, long bits, long[] shares)
    {
        lock (All)
        {
            Pictures[kind, 0]++;
            Pictures[kind, 1] += bits;
            for (int i = 0; i < Shares; i++)
            {
                Pictures[kind, 2 + i] += shares[i];
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

    /// <summary>Adds to a counter of the current picture type.</summary>
    [Conditional("EGAMI_HEVC_STATS")]
    public static void Add(HevcEncodeStat stat, long n = 1)
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
