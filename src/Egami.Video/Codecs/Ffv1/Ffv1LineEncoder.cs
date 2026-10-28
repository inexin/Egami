using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Video.Codecs.Ffv1;

/// <summary>
/// Codes lines of samples the way <see cref="Ffv1RangeLineDecoder"/> and <see cref="Ffv1GolombLineDecoder"/> read them: the same
/// borders, predictor and contexts, with each sample's difference from its prediction written instead of read.
/// </summary>
internal static class Ffv1LineEncoder
{
    private static readonly byte[] Log2Run =
    [
        0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3,
        4, 4, 5, 5, 6, 6, 7, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 19, 20, 21, 22, 23, 24,
    ];

    /// <summary>Range codes the samples in <paramref name="cur"/> from <see cref="Ffv1Lines.Offset"/> and fills the line's borders.</summary>
    public static void EncodeRange(Ffv1RangeEncoder rc, int[] cur, int[] top, int[] top2, int width, Ffv1PlaneCoding coding, int bits, bool signed16)
    {
        const int o = Ffv1Lines.Offset;
        Ffv1Lines.StartLine(cur, top);
        if ((uint)(width + o + 1) > (uint)cur.Length || (uint)(width + o + 1) > (uint)top.Length || (uint)(width + o + 1) > (uint)top2.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        int mask = (1 << bits) - 1;
        int half = 1 << (bits - 1);
        ref byte states = ref MemoryMarshal.GetArrayDataReference(coding.States);
        ref short q0 = ref MemoryMarshal.GetArrayDataReference(coding.Q0);
        ref short q1 = ref MemoryMarshal.GetArrayDataReference(coding.Q1);
        ref short q2 = ref MemoryMarshal.GetArrayDataReference(coding.Q2);
        ref short q3 = ref MemoryMarshal.GetArrayDataReference(coding.Q3);
        ref short q4 = ref MemoryMarshal.GetArrayDataReference(coding.Q4);
        bool wide = coding.UsesFarNeighbours;
        ref int c = ref MemoryMarshal.GetArrayDataReference(cur);
        ref int up = ref MemoryMarshal.GetArrayDataReference(top);
        ref int up2 = ref MemoryMarshal.GetArrayDataReference(top2);
        int bigL = Unsafe.Add(ref c, o - 2);
        int l = Unsafe.Add(ref c, o - 1);
        int tl = Unsafe.Add(ref up, o - 1);
        int t = Unsafe.Add(ref up, o);
        for (int x = o; x < o + width; x++)
        {
            int tr = Unsafe.Add(ref up, x + 1);
            int context = Unsafe.Add(ref q0, (l - tl) & 255) + Unsafe.Add(ref q1, (tl - t) & 255) + Unsafe.Add(ref q2, (t - tr) & 255);
            if (wide)
            {
                context += Unsafe.Add(ref q3, (bigL - l) & 255) + Unsafe.Add(ref q4, (Unsafe.Add(ref up2, x) - t) & 255);
            }

            int sample = Unsafe.Add(ref c, x);
            int diff = ((sample - Ffv1Lines.Predict(l, t, tl, signed16) + half) & mask) - half;
            int sign = context >> 31;
            diff = (diff ^ sign) - sign;
            rc.WriteSigned(ref Unsafe.Add(ref states, ((context ^ sign) - sign) * Ffv1Parameters.ContextSize), diff);
            bigL = l;
            l = sample;
            tl = t;
            t = tr;
        }

        cur[o + width] = cur[o + width - 1];
    }

    /// <summary>
    /// Golomb-Rice codes the samples in <paramref name="cur"/>, with run mode where the context is 0. The line's contexts and
    /// differences are worked out first, so each run's length is known when it starts.
    /// </summary>
    public static void EncodeGolomb(
        ref Ffv1BitWriter writer, ref int runIndex, int[] cur, int[] top, int[] top2, int width, Ffv1PlaneCoding coding, int bits, bool signed16,
        int[] contexts, int[] diffs, int[] zeros)
    {
        const int o = Ffv1Lines.Offset;
        Ffv1Lines.StartLine(cur, top);
        if ((uint)(width + o + 1) > (uint)cur.Length || (uint)(width + o + 1) > (uint)top.Length || (uint)(width + o + 1) > (uint)top2.Length
            || (uint)width >= (uint)contexts.Length || (uint)width >= (uint)diffs.Length || (uint)width >= (uint)zeros.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        int mask = (1 << bits) - 1;
        int half = 1 << (bits - 1);
        ref short q0 = ref MemoryMarshal.GetArrayDataReference(coding.Q0);
        ref short q1 = ref MemoryMarshal.GetArrayDataReference(coding.Q1);
        ref short q2 = ref MemoryMarshal.GetArrayDataReference(coding.Q2);
        ref short q3 = ref MemoryMarshal.GetArrayDataReference(coding.Q3);
        ref short q4 = ref MemoryMarshal.GetArrayDataReference(coding.Q4);
        bool wide = coding.UsesFarNeighbours;
        ref int c = ref MemoryMarshal.GetArrayDataReference(cur);
        ref int up = ref MemoryMarshal.GetArrayDataReference(top);
        ref int up2 = ref MemoryMarshal.GetArrayDataReference(top2);
        ref int ctxOut = ref MemoryMarshal.GetArrayDataReference(contexts);
        ref int diffOut = ref MemoryMarshal.GetArrayDataReference(diffs);
        int bigL = Unsafe.Add(ref c, o - 2);
        int l = Unsafe.Add(ref c, o - 1);
        int tl = Unsafe.Add(ref up, o - 1);
        int t = Unsafe.Add(ref up, o);
        for (int x = 0; x < width; x++)
        {
            int i = o + x;
            int tr = Unsafe.Add(ref up, i + 1);
            int context = Unsafe.Add(ref q0, (l - tl) & 255) + Unsafe.Add(ref q1, (tl - t) & 255) + Unsafe.Add(ref q2, (t - tr) & 255);
            if (wide)
            {
                context += Unsafe.Add(ref q3, (bigL - l) & 255) + Unsafe.Add(ref q4, (Unsafe.Add(ref up2, i) - t) & 255);
            }

            int sample = Unsafe.Add(ref c, i);
            int diff = ((sample - Ffv1Lines.Predict(l, t, tl, signed16) + half) & mask) - half;
            int sign = context >> 31;
            Unsafe.Add(ref ctxOut, x) = (context ^ sign) - sign;
            Unsafe.Add(ref diffOut, x) = (diff ^ sign) - sign;
            bigL = l;
            l = sample;
            tl = t;
            t = tr;
        }

        zeros[width] = 0;
        for (int x = width - 1; x >= 0; x--)
        {
            zeros[x] = diffs[x] == 0 ? zeros[x + 1] + 1 : 0;
        }

        Ffv1VlcState[] vlc = coding.Vlc;
        int runMode = 0;
        int runCount = 0;
        for (int x = 0; x < width; x++)
        {
            int context = contexts[x];
            if (context == 0 && runMode == 0)
            {
                runMode = 1;
            }

            if (runMode == 0)
            {
                WriteVlc(ref writer, ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(vlc), context), diffs[x], bits);
                continue;
            }

            if (runCount == 0 && runMode == 1)
            {
                int chunk = 1 << Log2Run[runIndex];
                int run = zeros[x];

                // A run that reaches the end of the line may stop short of a whole chunk: the line ends it.
                if (run >= chunk || x + run == width)
                {
                    writer.WriteBits(1, 1);
                    runCount = chunk;
                    if (x + runCount <= width && runIndex < Log2Run.Length - 1)
                    {
                        runIndex++;
                    }
                }
                else
                {
                    writer.WriteBits(1, 0);
                    writer.WriteBits(Log2Run[runIndex], (uint)run);
                    runCount = run;
                    if (runIndex > 0)
                    {
                        runIndex--;
                    }

                    runMode = 2;
                }
            }

            runCount--;
            if (runCount < 0)
            {
                runMode = 0;
                runCount = 0;
                int d = diffs[x];
                WriteVlc(ref writer, ref vlc[context], d > 0 ? d - 1 : d, bits);
            }
        }

        cur[o + width] = cur[o + width - 1];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteVlc(ref Ffv1BitWriter writer, ref Ffv1VlcState state, int value, int bits)
    {
        int k = Ffv1Lines.GolombParameter(state.Count, state.ErrorSum);

        int bias = 1 << (bits - 1);
        int sum = value - state.Bias;
        int v = (sum & (bias - 1)) - (sum & bias);
        int coded = 2 * state.Drift < -state.Count ? -1 - v : v;
        writer.WriteUnsignedGolomb(coded >= 0 ? 2 * coded : (-2 * coded) - 1, k, bits);
        state.ErrorSum += Math.Abs(v);
        state.Drift += v;
        if (state.Count == 128)
        {
            state.Count >>= 1;
            state.Drift >>= 1;
            state.ErrorSum >>= 1;
        }

        state.Count++;
        if (state.Drift <= -state.Count)
        {
            state.Bias = Math.Max(state.Bias - 1, -128);
            state.Drift = Math.Max(state.Drift + state.Count, -state.Count + 1);
        }
        else if (state.Drift > 0)
        {
            state.Bias = Math.Min(state.Bias + 1, 127);
            state.Drift = Math.Min(state.Drift - state.Count, 0);
        }
    }
}
