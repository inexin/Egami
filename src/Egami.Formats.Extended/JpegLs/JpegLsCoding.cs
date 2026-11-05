using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Egami.Formats.Extended.JpegLs;

/// <summary>The marker codes JPEG-LS uses (ITU-T T.87 annex C and T.81 table B.1).</summary>
internal static class JpegLsMarker
{
    public const byte Soi = 0xD8;
    public const byte Eoi = 0xD9;
    public const byte Sos = 0xDA;
    public const byte Dnl = 0xDC;
    public const byte Dri = 0xDD;
    public const byte Rst0 = 0xD0;
    public const byte Sof55 = 0xF7;
    public const byte Lse = 0xF8;
    public const byte App8 = 0xE8;
}

/// <summary>
/// The coding parameters of a scan (T.87 A.2 and C.2.4.1.1): MAXVAL, NEAR, the gradient thresholds, RESET and what follows from
/// them, with the gradient quantizer as a table.
/// </summary>
internal sealed class JpegLsParameters
{
    private static readonly ConcurrentDictionary<(int, int, int, int, int), sbyte[]> Quantizers = new();

    private JpegLsParameters(int maxVal, int near, int t1, int t2, int t3, int reset)
    {
        MaxVal = maxVal;
        Near = near;
        T1 = t1;
        T2 = t2;
        T3 = t3;
        Reset = reset;
        Range = ((maxVal + (2 * near)) / ((2 * near) + 1)) + 1;
        Qbpp = Log2Ceiling(Range);
        Bpp = Math.Max(2, Log2Ceiling(maxVal + 1));
        Limit = 2 * (Bpp + Math.Max(8, Bpp));
        Quantizer = Quantizers.GetOrAdd((maxVal, near, t1, t2, t3), static key => BuildQuantizer(key.Item1, key.Item2, key.Item3, key.Item4, key.Item5));
    }

    /// <summary>Gets the largest sample value.</summary>
    public int MaxVal { get; }

    /// <summary>Gets the largest error a sample may have; 0 for lossless.</summary>
    public int Near { get; }

    /// <summary>Gets the first gradient threshold.</summary>
    public int T1 { get; }

    /// <summary>Gets the second gradient threshold.</summary>
    public int T2 { get; }

    /// <summary>Gets the third gradient threshold.</summary>
    public int T3 { get; }

    /// <summary>Gets the count at which the context statistics are halved.</summary>
    public int Reset { get; }

    /// <summary>Gets the number of distinct quantized errors.</summary>
    public int Range { get; }

    /// <summary>Gets the bits of a quantized error, ⌈log2 RANGE⌉.</summary>
    public int Qbpp { get; }

    /// <summary>Gets the bits of a sample, at least 2.</summary>
    public int Bpp { get; }

    /// <summary>Gets the longest Golomb code word, LIMIT.</summary>
    public int Limit { get; }

    /// <summary>Gets the gradient quantizer: entry D + MAXVAL is Q(D), −4 to 4.</summary>
    public sbyte[] Quantizer { get; }

    /// <summary>
    /// Returns the parameters for samples of <paramref name="bits"/> bits with the preset values of an LSE segment, where 0
    /// means the default.
    /// </summary>
    public static JpegLsParameters Create(int bits, int near, int maxVal = 0, int t1 = 0, int t2 = 0, int t3 = 0, int reset = 0)
    {
        if (maxVal <= 0)
        {
            maxVal = (1 << bits) - 1;
        }

        (int d1, int d2, int d3) = DefaultThresholds(maxVal, near);
        return new JpegLsParameters(maxVal, near, t1 > 0 ? t1 : d1, t2 > 0 ? t2 : d2, t3 > 0 ? t3 : d3, reset > 0 ? reset : 64);
    }

    /// <summary>Returns the default thresholds for MAXVAL and NEAR (T.87 C.2.4.1.1.1).</summary>
    public static (int T1, int T2, int T3) DefaultThresholds(int maxVal, int near)
    {
        const int Basic1 = 3, Basic2 = 7, Basic3 = 21;
        int t1, t2, t3;
        if (maxVal >= 128)
        {
            int factor = (Math.Min(maxVal, 4095) + 128) >> 8;
            t1 = Clamp((factor * (Basic1 - 2)) + 2 + (3 * near), near + 1, maxVal);
            t2 = Clamp((factor * (Basic2 - 3)) + 3 + (5 * near), t1, maxVal);
            t3 = Clamp((factor * (Basic3 - 4)) + 4 + (7 * near), t2, maxVal);
        }
        else
        {
            int factor = 256 / (maxVal + 1);
            t1 = Clamp(Math.Max(2, (Basic1 / factor) + (3 * near)), near + 1, maxVal);
            t2 = Clamp(Math.Max(3, (Basic2 / factor) + (5 * near)), t1, maxVal);
            t3 = Clamp(Math.Max(4, (Basic3 / factor) + (7 * near)), t2, maxVal);
        }

        return (t1, t2, t3);

        static int Clamp(int i, int j, int maxVal) => i > maxVal || i < j ? j : i;
    }

    /// <summary>Returns whether the thresholds and RESET are the defaults for MAXVAL and NEAR.</summary>
    public bool IsDefault(int bits)
    {
        (int t1, int t2, int t3) = DefaultThresholds(MaxVal, Near);
        return MaxVal == (1 << bits) - 1 && T1 == t1 && T2 == t2 && T3 == t3 && Reset == 64;
    }

    private static int Log2Ceiling(int value) => value <= 1 ? 0 : BitOperations.Log2((uint)(value - 1)) + 1;

    private static sbyte[] BuildQuantizer(int maxVal, int near, int t1, int t2, int t3)
    {
        sbyte[] table = new sbyte[(2 * maxVal) + 1];
        for (int d = -maxVal; d <= maxVal; d++)
        {
            table[d + maxVal] = (sbyte)(d <= -t3 ? -4 : d <= -t2 ? -3 : d <= -t1 ? -2 : d < -near ? -1
                : d <= near ? 0 : d < t1 ? 1 : d < t2 ? 2 : d < t3 ? 3 : 4);
        }

        return table;
    }
}

/// <summary>The statistics of one regular-mode context (T.87 A.2.1): error magnitudes A, bias B, correction C and count N.</summary>
internal struct JpegLsContext
{
    public int A;
    public int B;
    public int C;
    public int N;
}

/// <summary>The statistics of a run interruption context (T.87 A.7.2): A, N and the count of negative errors Nn.</summary>
internal struct JpegLsRunContext
{
    public int A;
    public int N;
    public int Nn;
}

/// <summary>The context state of a scan, shared by its components, and the arithmetic both coders use.</summary>
internal sealed class JpegLsState
{
    /// <summary>The run-length order table J (T.87 A.7.1.2).</summary>
    public static ReadOnlySpan<byte> J => [0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    public readonly JpegLsContext[] Contexts = new JpegLsContext[365];
    public readonly JpegLsRunContext[] RunContexts = new JpegLsRunContext[2];

    public JpegLsState(JpegLsParameters parameters)
    {
        Parameters = parameters;
        Reset();
    }

    public JpegLsParameters Parameters { get; }

    /// <summary>Sets every context to its initial state (T.87 A.2.1), at the start of a scan and after a restart marker.</summary>
    public void Reset()
    {
        int a = Math.Max(2, (Parameters.Range + 32) >> 6);
        Contexts.AsSpan().Fill(new JpegLsContext { A = a, B = 0, C = 0, N = 1 });
        RunContexts[0] = new JpegLsRunContext { A = a, N = 1, Nn = 0 };
        RunContexts[1] = new JpegLsRunContext { A = a, N = 1, Nn = 0 };
    }

    /// <summary>Returns the smallest k with N·2^k ≥ A.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GolombK(int n, int a)
    {
        // With l(x) = ⌊log2 x⌋, N·2^(l(A)−l(N)) ≥ 2^l(A) and N·2^(l(A)−l(N)+1) > A, so k is one of those two.
        int k = BitOperations.Log2((uint)a) - BitOperations.Log2((uint)n);
        if (k < 0)
        {
            return 0;
        }

        return (n << k) < a ? k + 1 : k;
    }

    /// <summary>Returns the MED prediction of the sample from its neighbours a (left), b (above) and c (above left).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Predict(int ra, int rb, int rc)
    {
        int max = Math.Max(ra, rb);
        int min = Math.Min(ra, rb);
        return rc >= max ? min : rc <= min ? max : ra + rb - rc;
    }

    /// <summary>Updates a context after a regular-mode sample with error <paramref name="error"/> (T.87 A.6).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Update(ref JpegLsContext context, int error, int near, int reset)
    {
        int b = context.B + (error * ((2 * near) + 1));
        int a = context.A + Math.Abs(error);
        int n = context.N;
        if (n == reset)
        {
            a >>= 1;
            b >>= 1;
            n >>= 1;
        }

        n++;
        context.A = a;
        context.N = n;
        if (b <= -n)
        {
            b += n;
            if (context.C > -128)
            {
                context.C--;
            }

            if (b <= -n)
            {
                b = -n + 1;
            }
        }
        else if (b > 0)
        {
            b -= n;
            if (context.C < 127)
            {
                context.C++;
            }

            if (b > 0)
            {
                b = 0;
            }
        }

        context.B = b;
    }

    /// <summary>Updates run interruption context after an error (T.87 A.7.2, code segment A.23).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateRun(ref JpegLsRunContext context, int error, int mapped, int type, int reset)
    {
        if (error < 0)
        {
            context.Nn++;
        }

        context.A += (mapped + 1 - type) >> 1;
        if (context.N == reset)
        {
            context.A >>= 1;
            context.N >>= 1;
            context.Nn >>= 1;
        }

        context.N++;
    }

    /// <summary>Returns the Golomb parameter of a run interruption context (T.87 A.7.2, code segment A.20).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RunK(in JpegLsRunContext context, int type)
    {
        int temp = type == 0 ? context.A : context.A + (context.N >> 1);
        int k = 0;
        while ((context.N << k) < temp)
        {
            k++;
        }

        return k;
    }
}
