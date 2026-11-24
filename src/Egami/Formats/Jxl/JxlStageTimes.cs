using System.Diagnostics;

namespace Egami.Formats.Jxl;

/// <summary>
/// Where a JPEG XL decode or encode spends its time, for the roadmap 2.4.11 profile (benchmarks/Egami.Benchmarks/JxlTiming.cs,
/// EGAMI_JXL_STAGES=1). Each <see cref="Lap"/> adds the time since the previous lap to one stage. The calls exist only in a build
/// with JXL_STAGE_TIMERS defined (dotnet build -p:JxlStageTimers=true); the shipped library never defines it, so there they
/// compile to nothing, arguments included. The stages are plain statics, so time one decode or encode at a time. Every lap is
/// taken on the thread that runs the decode or encode, between its parallel loops, so on more than one thread a stage is its
/// wall-clock time and comparing it with one thread's shows how much of the stage runs in parallel. Each lap also adds the
/// managed bytes allocated since the previous lap, on every thread, to <see cref="Bytes"/>.
/// <para>
/// Work that runs inside a parallel loop, such as the lossless encoder's streams that each learn their own tree, is split with
/// a <see cref="JxlStageClock"/> per piece of work instead: <see cref="Split"/> adds the time and the bytes the calling thread
/// spent since the clock's last split to <see cref="ThreadTicks"/> and <see cref="ThreadBytes"/>. Those are summed over the
/// threads, so on one thread they divide the wall-clock stage they run in, and on more they show where the threads' time goes.
/// </para>
/// </summary>
internal static class JxlStageTimes
{
    /// <summary>The stages, in <see cref="Stopwatch"/> ticks.</summary>
    internal static readonly long[] Ticks = new long[(int)JxlStage.Count];

    /// <summary>The managed bytes allocated in each stage, on every thread.</summary>
    internal static readonly long[] Bytes = new long[(int)JxlStage.Count];

    /// <summary>The time <see cref="Split"/> gives each stage, in <see cref="Stopwatch"/> ticks summed over the threads.</summary>
    internal static readonly long[] ThreadTicks = new long[(int)JxlStage.Count];

    /// <summary>The managed bytes <see cref="Split"/> gives each stage, summed over the threads.</summary>
    internal static readonly long[] ThreadBytes = new long[(int)JxlStage.Count];

    private static long s_last;
    private static long s_lastBytes;

    /// <summary>Clears the stages and starts the first lap.</summary>
    [Conditional("JXL_STAGE_TIMERS")]
    internal static void Start()
    {
        Array.Clear(Ticks);
        Array.Clear(Bytes);
        Array.Clear(ThreadTicks);
        Array.Clear(ThreadBytes);
        s_lastBytes = GC.GetTotalAllocatedBytes();
        s_last = Stopwatch.GetTimestamp();
    }

    /// <summary>Adds the time since the last lap to <paramref name="stage"/>, unless <paramref name="skip"/>, when the time goes to the next lap's stage.</summary>
    [Conditional("JXL_STAGE_TIMERS")]
    internal static void Lap(JxlStage stage, bool skip = false)
    {
        if (skip)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        long bytes = GC.GetTotalAllocatedBytes();
        Ticks[(int)stage] += now - s_last;
        Bytes[(int)stage] += bytes - s_lastBytes;
        s_last = now;
        s_lastBytes = bytes;
    }

    /// <summary>Starts <paramref name="clock"/> on the calling thread.</summary>
    [Conditional("JXL_STAGE_TIMERS")]
    internal static void Begin(ref JxlStageClock clock)
    {
        clock.Bytes = GC.GetAllocatedBytesForCurrentThread();
        clock.Ticks = Stopwatch.GetTimestamp();
    }

    /// <summary>Adds the calling thread's time and bytes since <paramref name="clock"/>'s last split (or <see cref="Begin"/>) to <paramref name="stage"/>.</summary>
    [Conditional("JXL_STAGE_TIMERS")]
    internal static void Split(ref JxlStageClock clock, JxlStage stage)
    {
        long now = Stopwatch.GetTimestamp();
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        Interlocked.Add(ref ThreadTicks[(int)stage], now - clock.Ticks);
        Interlocked.Add(ref ThreadBytes[(int)stage], bytes - clock.Bytes);
        clock.Ticks = now;
        clock.Bytes = bytes;
    }
}

/// <summary>The start of a <see cref="JxlStageTimes.Split"/>: a time and the calling thread's allocated bytes.</summary>
internal struct JxlStageClock
{
    /// <summary>The <see cref="Stopwatch"/> timestamp.</summary>
    internal long Ticks;

    /// <summary>The bytes the thread had allocated.</summary>
    internal long Bytes;
}

/// <summary>The stages <see cref="JxlStageTimes"/> splits a decode or an encode into.</summary>
internal enum JxlStage
{
    /// <summary>Decode: the container, headers, TOC and everything before the first frame's LF global section.</summary>
    Headers,

    /// <summary>Decode: LF global and the LF groups (VarDCT LF coefficients and HF metadata, Modular channels squeezed 3 or more).</summary>
    LfGroups,

    /// <summary>Decode: the pass groups' entropy decoding, HF coefficients and Modular group channels.</summary>
    Entropy,

    /// <summary>Decode: the Modular inverse transforms (RCT, palette, squeeze).</summary>
    ModularInverse,

    /// <summary>Decode: LF preparation (smoothing, chroma from luma), HF dequantization and chroma from luma on HF.</summary>
    Dequantize,

    /// <summary>Decode: the LF coefficients placed in each varblock and the inverse DCTs.</summary>
    Idct,

    /// <summary>Decode: cropping the planes to the frame, chroma upsampling, or Modular samples to floats.</summary>
    Planes,

    /// <summary>Decode: the Gaborish filter.</summary>
    Gaborish,

    /// <summary>Decode: the edge-preserving filter.</summary>
    Epf,

    /// <summary>Decode: upsampling, patches, splines, noise, blending and the other per-frame rendering.</summary>
    Render,

    /// <summary>Decode: XYB or the color transform to the output space.</summary>
    Color,

    /// <summary>Decode: the samples written into the raster's pixel type.</summary>
    Pixels,

    /// <summary>Encode: the raster's pixels read into planes (linear float or integers).</summary>
    Input,

    /// <summary>Lossy encode: linear to XYB, padded to whole blocks.</summary>
    Xyb,

    /// <summary>Encode: the patch search (lossy from effort 7, lossless screenshots).</summary>
    Patches,

    /// <summary>Lossy encode: the adaptive quantization field.</summary>
    Aq,

    /// <summary>Lossy encode: the inverse Gaborish, chroma from luma and the block-size (AC strategy) search.</summary>
    AcStrategy,

    /// <summary>Lossy encode: the quantization search (Butteraugli iterations, from effort 8).</summary>
    QuantSearch,

    /// <summary>Lossy encode: the forward DCTs and quantization of HF and LF.</summary>
    Quantize,

    /// <summary>Lossy encode: the coefficient orders and the block context model.</summary>
    Orders,

    /// <summary>Lossy encode: the edge-preserving filter's sharpness, from the encoder's own decode of a draft.</summary>
    EpfSharpness,

    /// <summary>Lossless encode: palettes, color transforms and the split into groups.</summary>
    Transforms,

    /// <summary>Lossless encode: learning the context tree.</summary>
    Tree,

    /// <summary>Encode: the symbols to code (Modular residuals and HF coefficients) as tokens.</summary>
    Tokens,

    /// <summary>Encode: the histograms, their clustering and the entropy codes.</summary>
    Histograms,

    /// <summary>
    /// Lossless encode, on the path that gives each stream a tree of its own (efforts 2, 8 and 9 on frames of more than 8 groups):
    /// the global and LF group streams, coded one after another. The thread stages split it into tree, tokens, histograms and write.
    /// </summary>
    LocalSerial,

    /// <summary>Lossless encode, on the same path: the group streams, coded in parallel.</summary>
    LocalGroups,

    /// <summary>Encode: the tokens written, the sections and the file assembled.</summary>
    Write,

    /// <summary>Lossless encode: libjxl's fast lossless path at effort 1, all of it.</summary>
    FastLossless,

    /// <summary>The number of stages.</summary>
    Count,
}
