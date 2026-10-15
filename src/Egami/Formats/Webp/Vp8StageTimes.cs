using System.Diagnostics;

namespace Egami.Formats.Webp;

/// <summary>
/// Where a single-thread VP8 decode spends its time, for the Phase 2.4.5 profile (benchmarks/Egami.Benchmarks/Vp8Timing.cs). Each
/// <see cref="Lap"/> adds the time since the previous lap to one stage. The calls exist only in a build with VP8_STAGE_TIMERS
/// defined (dotnet build -p:Vp8StageTimers=true); the shipped library never defines it, so there they compile to nothing. The
/// stages are plain statics: time one thread at a time.
/// </summary>
internal static class Vp8StageTimes
{
    /// <summary>The stages, in <see cref="Stopwatch"/> ticks.</summary>
    internal static readonly long[] Ticks = new long[(int)Vp8Stage.Count];

    private static long s_last;

    /// <summary>Clears the stages and starts the first lap.</summary>
    [Conditional("VP8_STAGE_TIMERS")]
    internal static void Start()
    {
        Array.Clear(Ticks);
        s_last = Stopwatch.GetTimestamp();
    }

    /// <summary>Adds the time since the last lap to <paramref name="stage"/>.</summary>
    [Conditional("VP8_STAGE_TIMERS")]
    internal static void Lap(Vp8Stage stage)
    {
        long now = Stopwatch.GetTimestamp();
        Ticks[(int)stage] += now - s_last;
        s_last = now;
    }
}

/// <summary>The stages <see cref="Vp8StageTimes"/> splits a decode into.</summary>
internal enum Vp8Stage
{
    /// <summary>Frame header, probabilities, the decoder's arrays and buffers, reference updates.</summary>
    Setup,

    /// <summary>Macroblock modes and motion vectors (the first partition).</summary>
    Modes,

    /// <summary>Coefficient tokens (the token partitions) and the second-order transform.</summary>
    Tokens,

    /// <summary>Intra prediction, inverse transforms and the copies into the frame.</summary>
    Reconstruct,

    /// <summary>Inter prediction from the reference frames.</summary>
    Inter,

    /// <summary>The loop filter.</summary>
    Filter,

    /// <summary>Extending the frame's borders for the next frame's prediction.</summary>
    Borders,

    /// <summary>A still's rows handed out and turned to RGB.</summary>
    Output,

    /// <summary>The number of stages.</summary>
    Count,
}
