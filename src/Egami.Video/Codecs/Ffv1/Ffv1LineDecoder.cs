using System.Runtime.CompilerServices;

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

    /// <summary>Sets every state to its initial value for quantization table set <paramref name="set"/>.</summary>
    public void Reset(Ffv1Parameters parameters, int set)
    {
        short[][] tables = parameters.QuantTables[set];
        (Q0, Q1, Q2, Q3, Q4) = (tables[0], tables[1], tables[2], tables[3], tables[4]);
        QuantIndex = set;
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
        if (a > b)
        {
            (a, b) = (b, a);
        }

        return c < a ? a : c > b ? b : c;
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
        int mask = (1 << bits) - 1;
        Span<byte> states = coding.States;
        for (int x = o; x < o + width; x++)
        {
            int l = cur[x - 1];
            int t = top[x];
            int tl = top[x - 1];
            int context = Ffv1Lines.Context(coding, cur[x - 2], l, tl, t, top2[x], top[x + 1]);
            int diff;
            if (context < 0)
            {
                diff = -Decoder.ReadSymbol(states.Slice(-context * Ffv1Parameters.ContextSize, Ffv1Parameters.ContextSize), true);
            }
            else
            {
                diff = Decoder.ReadSymbol(states.Slice(context * Ffv1Parameters.ContextSize, Ffv1Parameters.ContextSize), true);
            }

            cur[x] = (Ffv1Lines.Predict(l, t, tl, signed16) + diff) & mask;
        }

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
        int mask = (1 << bits) - 1;
        Ffv1VlcState[] vlc = coding.Vlc;
        int runMode = 0;
        int runCount = 0;
        for (int x = 0; x < width; x++)
        {
            int i = o + x;
            int l = cur[i - 1];
            int t = top[i];
            int tl = top[i - 1];
            int context = Ffv1Lines.Context(coding, cur[i - 2], l, tl, t, top2[i], top[i + 1]);
            bool negative = context < 0;
            if (negative)
            {
                context = -context;
            }

            int diff;
            if (context == 0 && runMode == 0)
            {
                runMode = 1;
            }

            if (runMode != 0)
            {
                if (runCount == 0 && runMode == 1)
                {
                    if (Reader.ReadBits(1) != 0)
                    {
                        runCount = 1 << Log2Run[_runIndex];
                        if (x + runCount <= width && _runIndex < Log2Run.Length - 1)
                        {
                            _runIndex++;
                        }
                    }
                    else
                    {
                        runCount = Reader.ReadBits(Log2Run[_runIndex]);
                        if (_runIndex > 0)
                        {
                            _runIndex--;
                        }

                        runMode = 2;
                    }
                }

                runCount--;
                if (runCount < 0)
                {
                    runMode = 0;
                    runCount = 0;
                    diff = ReadVlc(ref vlc[context], bits);
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
                diff = ReadVlc(ref vlc[context], bits);
            }

            if (negative)
            {
                diff = -diff;
            }

            cur[i] = (Ffv1Lines.Predict(l, t, tl, signed16) + diff) & mask;
        }

        cur[o + width] = cur[o + width - 1];
    }

    private int ReadVlc(ref Ffv1VlcState state, int bits)
    {
        int k = 0;
        for (int i = state.Count; i < state.ErrorSum; i += i)
        {
            if (++k > 30)
            {
                throw new CorruptDataException("FFV1", -1, "An FFV1 Golomb-Rice context's statistics are out of range.");
            }
        }

        int u = Reader.ReadUnsignedGolomb(k, bits);
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
}
