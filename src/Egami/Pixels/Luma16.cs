using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>
/// 16-bit Rec. 709 luma in linear light, bit for bit what <c>Scale.ToUInt16(Luma.OfScaled(r, g, b))</c> gives, without its four
/// powers per pixel. The linear sum is encoded with a table of exact thresholds: entry k is the smallest linear value that
/// encodes to at least k, found once by bisection against the exact curve. 8-bit channels take their linear values from a
/// table; float channels from an interpolated table, with the exact powers only when the sum lands within the interpolation's
/// error bound of a threshold.
/// </summary>
internal static class Luma16
{
    // Buckets are uniform in the square root of linear light, where the thresholds are spaced at least 1.07e-5 apart (at the
    // end of the curve's linear segment), more than a bucket's 7.6e-6: so a bucket holds at most one threshold, and one
    // comparison settles the code. BuildStart checks this.
    private const int Buckets = 1 << 17;

    // Interpolating the sRGB decoding curve over 4096 steps is accurate to 2.3e-8 (its second derivative is below 3.1).
    private const int CurveSteps = 4096;
    private const double Margin = 1e-7;

    /// <summary>Each 8-bit value's linear light times its Rec. 709 weight, for red, green and blue in turn: the same products Luma.OfScaled forms.</summary>
    private static readonly double[] Weighted8 = BuildWeighted8();

    private static readonly float[] Curve = BuildCurve();

    /// <summary>Entry k (1 to 65535) is the smallest linear value that encodes to k or more; entry 65536 is above them all.</summary>
    private static readonly double[] Thresholds = BuildThresholds();

    /// <summary>For linear x in bucket i (√x × 2^17 in [i, i + 1)), the code at the bucket's start: the code, or one below it.</summary>
    private static readonly ushort[] Start = BuildStart();

    /// <summary>Returns the 16-bit luma of an 8-bit color. Gray input keeps its value.</summary>
    internal static ushort Of8(byte r, byte g, byte b)
    {
        if (r == g && g == b)
        {
            return Scale.ToUInt16(r / 255f);
        }

        ref double weighted = ref MemoryMarshal.GetArrayDataReference(Weighted8);
        double sum = Unsafe.Add(ref weighted, r) + Unsafe.Add(ref weighted, 256 + g) + Unsafe.Add(ref weighted, 512 + b);
        return Encode(sum);
    }

    /// <summary>Returns the 16-bit luma of a color whose channels are already clamped to 0..1. Gray input keeps its value.</summary>
    internal static ushort OfScaled(float r, float g, float b)
    {
        if (r == g && g == b)
        {
            return Scale.ToUInt16(r);
        }

        double approximate = (0.2126 * Decode(r)) + (0.7152 * Decode(g)) + (0.0722 * Decode(b));
        ushort code = Encode(approximate);
        ref double thresholds = ref MemoryMarshal.GetArrayDataReference(Thresholds);
        if (approximate - Unsafe.Add(ref thresholds, code) < Margin || Unsafe.Add(ref thresholds, code + 1) - approximate < Margin)
        {
            // Too close to call: the exact sum, as Luma.OfScaled computes it.
            double exact = (0.2126 * Srgb.ToLinear(r)) + (0.7152 * Srgb.ToLinear(g)) + (0.0722 * Srgb.ToLinear(b));
            return Encode(exact);
        }

        return code;
    }

    /// <summary>The exact encoding the thresholds stand for: clamp, encode with the sRGB curve, round to float, then to 16 bits.</summary>
    internal static ushort EncodeExactly(double linear)
    {
        return Scale.ToUInt16((float)Srgb.FromLinear(Math.Clamp(linear, 0.0, 1.0)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort Encode(double linear)
    {
        if (!(linear > 0))
        {
            return 0;
        }

        if (linear >= 1)
        {
            return ushort.MaxValue;
        }

        int code = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Start), (int)(Math.Sqrt(linear) * Buckets));
        code += linear >= Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Thresholds), code + 1) ? 1 : 0;
        return (ushort)code;
    }

    /// <summary>The sRGB decoding curve, from the exact linear segment or the interpolated table; within 2.3e-8 of exact.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Decode(float encoded)
    {
        if (encoded <= 0.04045f)
        {
            return encoded / 12.92;
        }

        float position = encoded * CurveSteps;
        int index = Math.Min((int)position, CurveSteps - 1);
        ref float curve = ref MemoryMarshal.GetArrayDataReference(Curve);
        float low = Unsafe.Add(ref curve, index);
        return low + ((Unsafe.Add(ref curve, index + 1) - low) * (double)(position - index));
    }

    private static double[] BuildWeighted8()
    {
        double[] table = new double[3 * 256];
        for (int v = 0; v < 256; v++)
        {
            double linear = Srgb.ToLinear(v / 255f);
            table[v] = 0.2126 * linear;
            table[256 + v] = 0.7152 * linear;
            table[512 + v] = 0.0722 * linear;
        }

        return table;
    }

    private static float[] BuildCurve()
    {
        float[] curve = new float[CurveSteps + 1];
        for (int i = 0; i <= CurveSteps; i++)
        {
            curve[i] = (float)Srgb.ToLinear(i / (double)CurveSteps);
        }

        return curve;
    }

    private static double[] BuildThresholds()
    {
        double[] thresholds = new double[ushort.MaxValue + 2];
        thresholds[ushort.MaxValue + 1] = double.PositiveInfinity;
        long low = BitConverter.DoubleToInt64Bits(0.0), top = BitConverter.DoubleToInt64Bits(1.0);
        for (int k = 1; k <= ushort.MaxValue; k++)
        {
            // Positive doubles order like their bits: bisect on the bits for the first value that encodes to k or more.
            long lo = low, hi = top;
            while (lo < hi)
            {
                long mid = lo + ((hi - lo) >> 1);
                if (EncodeExactly(BitConverter.Int64BitsToDouble(mid)) >= k)
                {
                    hi = mid;
                }
                else
                {
                    lo = mid + 1;
                }
            }

            thresholds[k] = BitConverter.Int64BitsToDouble(lo);
            low = lo;
        }

        return thresholds;
    }

    private static ushort[] BuildStart()
    {
        ushort[] start = new ushort[Buckets];
        int code = 0;
        for (int i = 0; i < Buckets; i++)
        {
            // The smallest x whose bucket is i: (i / 2^17)², then nudged up until its square root really lands in bucket i.
            double x = Math.Pow(i / (double)Buckets, 2);
            while ((int)(Math.Sqrt(x) * Buckets) < i)
            {
                x = Math.BitIncrement(x);
            }

            while (code < ushort.MaxValue && Thresholds[code + 1] <= x)
            {
                code++;
            }

            start[i] = (ushort)code;
        }

        // At most one threshold per bucket, or Encode would need a loop.
        for (int i = 0; i + 1 < Buckets; i++)
        {
            if (start[i + 1] - start[i] > 1)
            {
                throw new InvalidOperationException($"Luma16 bucket {i} holds {start[i + 1] - start[i]} thresholds.");
            }
        }

        return start;
    }
}
