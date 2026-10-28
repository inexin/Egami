using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Video.Codecs.Ffv1;

/// <summary>
/// How one plane of a slice is coded: its quantization tables and the adaptive states of its contexts. The luma, chroma and
/// extra planes each have one (Cb and Cr share the chroma one), kept from frame to frame until the next keyframe.
/// </summary>
internal sealed class Ffv1PlaneCoding
{
    public short[] Q0 = [];
    public short[] Q1 = [];
    public short[] Q2 = [];
    public short[] Q3 = [];
    public short[] Q4 = [];

    /// <summary>The range coder states, 32 per context.</summary>
    public byte[] States = [];

    /// <summary>The Golomb-Rice states, one per context.</summary>
    public Ffv1VlcState[] Vlc = [];

    public int QuantIndex = -1;

    /// <summary>Whether the context uses L and T (the large context model); the small one's last two tables are all zero.</summary>
    public bool UsesFarNeighbours = true;

    /// <summary>Sets every state to its initial value for quantization table set <paramref name="set"/>.</summary>
    public void Reset(Ffv1Parameters parameters, int set)
    {
        short[][] tables = parameters.QuantTables[set];
        (Q0, Q1, Q2, Q3, Q4) = (tables[0], tables[1], tables[2], tables[3], tables[4]);
        QuantIndex = set;
        UsesFarNeighbours = Array.Exists(Q3, v => v != 0) || Array.Exists(Q4, v => v != 0);
        int contexts = parameters.ContextCounts[set];
        if (parameters.CoderType == 0)
        {
            if (Vlc.Length != contexts)
            {
                Vlc = new Ffv1VlcState[contexts];
            }

            Vlc.AsSpan().Fill(Ffv1VlcState.Initial);
        }
        else
        {
            int size = contexts * Ffv1Parameters.ContextSize;
            if (States.Length != size)
            {
                States = new byte[size];
            }

            if (parameters.InitialStates[set] is { } initial)
            {
                initial.CopyTo(States, 0);
            }
            else
            {
                States.AsSpan().Fill(128);
            }
        }
    }
}

/// <summary>The adaptive state of one Golomb-Rice context (RFC 9043 section 3.8.2.4).</summary>
internal struct Ffv1VlcState
{
    public static readonly Ffv1VlcState Initial = new() { ErrorSum = 4, Count = 1 };

    public int Drift;
    public int ErrorSum;
    public int Bias;
    public int Count;
}

/// <summary>Decodes one line of samples; implemented by the range coder and the Golomb-Rice reader.</summary>
internal interface IFfv1LineDecoder
{
    /// <summary>Starts a new plane (Golomb-Rice resets its run index).</summary>
    void StartPlane();

    /// <summary>
    /// Decodes <paramref name="width"/> samples into <paramref name="cur"/> from <see cref="Ffv1Lines.Offset"/>, predicting from
    /// the line above (<paramref name="top"/>) and the one above that (<paramref name="top2"/>), and fills the line's borders.
    /// </summary>
    void DecodeLine(int[] cur, int[] top, int[] top2, int width, Ffv1PlaneCoding coding, int bits, bool signed16);
}

/// <summary>The line buffers' layout and the predictor and context they share.</summary>
internal static class Ffv1Lines
{
    /// <summary>Where a line's first sample sits: two border samples come before it, one after.</summary>
    public const int Offset = 2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Median(int a, int b, int c)
    {
        // Without branches: on noisy pictures the order of the three is random, and mispredicted branches cost more than this.
        int d = a - b;
        int m = d >> 31;
        int lo = b + (d & m);
        int hi = a - (d & m);
        d = hi - c;
        hi = c + (d & (d >> 31));
        d = lo - hi;
        return lo - (d & (d >> 31));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Predict(int l, int t, int tl, bool signed16)
    {
        if (signed16)
        {
            l = (short)l;
            t = (short)t;
            tl = (short)tl;
        }

        return Median(l, t, l + t - tl);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Context(Ffv1PlaneCoding c, int bigL, int l, int tl, int t, int bigT, int tr) =>
        c.Q0[(l - tl) & 255] + c.Q1[(tl - t) & 255] + c.Q2[(t - tr) & 255] + c.Q3[(bigL - l) & 255] + c.Q4[(bigT - t) & 255];

    /// <summary>
    /// The Golomb-Rice parameter of a context (RFC 9043 section 3.8.2.4): the fewest doublings of <paramref name="count"/> that
    /// reach <paramref name="errorSum"/>, worked out with a logarithm instead of a loop.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GolombParameter(int count, int errorSum)
    {
        if (errorSum <= count)
        {
            return 0;
        }

        // count * 2^k >= errorSum exactly when 2^k >= ceil(errorSum / count).
        uint ratio = (uint)(((long)errorSum + count - 1) / count);
        return System.Numerics.BitOperations.Log2(ratio - 1) + 1;
    }

    /// <summary>Sets the left borders of a line before it is decoded (RFC 9043 section 3.1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StartLine(int[] cur, int[] top)
    {
        cur[Offset - 2] = 0;
        cur[Offset - 1] = top[Offset];
    }
}

/// <summary>Decodes range-coded lines (RFC 9043 section 3.8.1).</summary>
internal struct Ffv1RangeLineDecoder(Ffv1RangeDecoder decoder) : IFfv1LineDecoder
{
    public Ffv1RangeDecoder Decoder = decoder;

    public readonly void StartPlane()
    {
    }

    public void DecodeLine(int[] cur, int[] top, int[] top2, int width, Ffv1PlaneCoding coding, int bits, bool signed16)
    {
        const int o = Ffv1Lines.Offset;
        Ffv1Lines.StartLine(cur, top);
        if ((uint)(width + o + 1) > (uint)cur.Length || (uint)(width + o + 1) > (uint)top.Length || (uint)(width + o + 1) > (uint)top2.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        int mask = (1 << bits) - 1;
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
        Ffv1RangeDecoder rc = Decoder;

        // The neighbours slide along the line in registers: l and L from this line, tl, t and tr from the line above.
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

            int sign = context >> 31;
            int diff = rc.ReadSigned(ref Unsafe.Add(ref states, ((context ^ sign) - sign) * Ffv1Parameters.ContextSize));
            diff = (diff ^ sign) - sign;
            int sample = (Ffv1Lines.Predict(l, t, tl, signed16) + diff) & mask;
            Unsafe.Add(ref c, x) = sample;
            bigL = l;
            l = sample;
            tl = t;
            t = tr;
        }

        Decoder = rc;
        cur[o + width] = cur[o + width - 1];
    }
}

/// <summary>Decodes Golomb-Rice coded lines with run mode (RFC 9043 section 3.8.2).</summary>
internal struct Ffv1GolombLineDecoder(Ffv1BitReader reader) : IFfv1LineDecoder
{
    private static readonly byte[] Log2Run =
    [
        0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3,
        4, 4, 5, 5, 6, 6, 7, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 19, 20, 21, 22, 23, 24,
    ];

    public Ffv1BitReader Reader = reader;
    private int _runIndex;

    public void StartPlane() => _runIndex = 0;

    public void DecodeLine(int[] cur, int[] top, int[] top2, int width, Ffv1PlaneCoding coding, int bits, bool signed16)
    {
        const int o = Ffv1Lines.Offset;
        Ffv1Lines.StartLine(cur, top);
        if ((uint)(width + o + 1) > (uint)cur.Length || (uint)(width + o + 1) > (uint)top.Length || (uint)(width + o + 1) > (uint)top2.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        int mask = (1 << bits) - 1;
        ref Ffv1VlcState vlc = ref MemoryMarshal.GetArrayDataReference(coding.Vlc);
        ref short q0 = ref MemoryMarshal.GetArrayDataReference(coding.Q0);
        ref short q1 = ref MemoryMarshal.GetArrayDataReference(coding.Q1);
        ref short q2 = ref MemoryMarshal.GetArrayDataReference(coding.Q2);
        ref short q3 = ref MemoryMarshal.GetArrayDataReference(coding.Q3);
        ref short q4 = ref MemoryMarshal.GetArrayDataReference(coding.Q4);
        bool wide = coding.UsesFarNeighbours;
        ref int c = ref MemoryMarshal.GetArrayDataReference(cur);
        ref int up = ref MemoryMarshal.GetArrayDataReference(top);
        ref int up2 = ref MemoryMarshal.GetArrayDataReference(top2);
        Ffv1BitReader reader = Reader;
        int runIndex = _runIndex;
        int runMode = 0;
        int runCount = 0;
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

            int sign = context >> 31;
            context = (context ^ sign) - sign;
            int diff;
            if (context == 0 && runMode == 0)
            {
                runMode = 1;
            }

            if (runMode != 0)
            {
                if (runCount == 0 && runMode == 1)
                {
                    if (reader.ReadBits(1) != 0)
                    {
                        runCount = 1 << Log2Run[runIndex];
                        if (x + runCount <= width && runIndex < Log2Run.Length - 1)
                        {
                            runIndex++;
                        }
                    }
                    else
                    {
                        runCount = reader.ReadBits(Log2Run[runIndex]);
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
                    diff = ReadVlc(ref reader, ref Unsafe.Add(ref vlc, context), bits);
                    if (diff >= 0)
                    {
                        diff++;
                    }
                }
                else
                {
                    diff = 0;
                }
            }
            else
            {
                diff = ReadVlc(ref reader, ref Unsafe.Add(ref vlc, context), bits);
            }

            diff = (diff ^ sign) - sign;
            int sample = (Ffv1Lines.Predict(l, t, tl, signed16) + diff) & mask;
            Unsafe.Add(ref c, i) = sample;
            bigL = l;
            l = sample;
            tl = t;
            t = tr;
        }

        Reader = reader;
        _runIndex = runIndex;
        cur[o + width] = cur[o + width - 1];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadVlc(ref Ffv1BitReader reader, ref Ffv1VlcState state, int bits)
    {
        int k = Ffv1Lines.GolombParameter(state.Count, state.ErrorSum);
        if (k > 30)
        {
            ThrowStatistics();
        }

        int u = reader.ReadUnsignedGolomb(k, bits);
        int v = (u & 1) != 0 ? -(u >> 1) - 1 : u >> 1;
        if (2 * state.Drift < -state.Count)
        {
            v = -1 - v;
        }

        int bias = 1 << (bits - 1);
        int sum = v + state.Bias;
        int result = (sum & (bias - 1)) - (sum & bias);
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

        return result;
    }

    private static void ThrowStatistics() => throw new CorruptDataException("FFV1", -1, "An FFV1 Golomb-Rice context's statistics are out of range.");
}
