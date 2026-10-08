using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>Rec. 709 luma of sRGB-encoded colors, weighted in linear light.</summary>
internal static class Luma
{
    private const int CoarseSteps = 4096;

    /// <summary>Each 8-bit value's linear light times its Rec. 709 weight, for red, green and blue in turn (3 × 256 entries).</summary>
    private static readonly float[] Weighted = BuildWeighted();

    /// <summary>
    /// Entry v is the linear value where 8-bit encoding switches from v to v + 1, from the exact IEC 61966-2-1 curve;
    /// entry 255 is above every possible value.
    /// </summary>
    private static readonly float[] Thresholds = BuildThresholds();

    /// <summary>For linear x = i / 4095, the 8-bit value whose interval contains x, so a lookup needs at most a step or two of correction.</summary>
    private static readonly byte[] Coarse = BuildCoarse();

    /// <summary>Returns the 8-bit luma of an 8-bit color, encoded with the exact sRGB curve. Gray input keeps its value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte Of8(byte r, byte g, byte b)
    {
        ref float weighted = ref MemoryMarshal.GetArrayDataReference(Weighted);
        float linear = Unsafe.Add(ref weighted, r) + Unsafe.Add(ref weighted, 256 + g) + Unsafe.Add(ref weighted, 512 + b);
        return Encode(linear);
    }

    /// <summary>Writes the 8-bit luma of each pixel of an 8-bit RGB row. The channel offsets give the layout.</summary>
    /// <param name="source">The pixels' bytes.</param>
    /// <param name="destination">One gray byte per pixel.</param>
    /// <param name="size">Bytes per pixel, 3 or 4.</param>
    /// <param name="red">Offset of red within a pixel.</param>
    /// <param name="blue">Offset of blue within a pixel.</param>
    internal static void Row(ReadOnlySpan<byte> source, Span<byte> destination, int size, int red, int blue)
    {
        ref byte s = ref MemoryMarshal.GetReference(source);
        ref byte d = ref MemoryMarshal.GetReference(destination);
        ref float weighted = ref MemoryMarshal.GetArrayDataReference(Weighted);
        ref byte coarse = ref MemoryMarshal.GetArrayDataReference(Coarse);
        ref float thresholds = ref MemoryMarshal.GetArrayDataReference(Thresholds);
        for (int i = 0, o = 0; i < destination.Length; i++, o += size)
        {
            float linear = Unsafe.Add(ref weighted, Unsafe.Add(ref s, o + red))
                + Unsafe.Add(ref weighted, 256 + Unsafe.Add(ref s, o + 1))
                + Unsafe.Add(ref weighted, 512 + Unsafe.Add(ref s, o + blue));
            int v = Unsafe.Add(ref coarse, Math.Min((int)(linear * (CoarseSteps - 1)), CoarseSteps - 1));
            v += linear >= Unsafe.Add(ref thresholds, v) ? 1 : 0;
            v += linear >= Unsafe.Add(ref thresholds, v) ? 1 : 0;
            Unsafe.Add(ref d, i) = (byte)v;
        }
    }

    /// <summary>
    /// Returns the luma, 0..1, of a color whose channels are sRGB-encoded and already clamped to 0..1.
    /// Gray input is returned unchanged.
    /// </summary>
    internal static float OfScaled(float r, float g, float b)
    {
        if (r == g && g == b)
        {
            return r;
        }

        double linear = (0.2126 * Srgb.ToLinear(r)) + (0.7152 * Srgb.ToLinear(g)) + (0.0722 * Srgb.ToLinear(b));
        return (float)Srgb.FromLinear(Math.Clamp(linear, 0.0, 1.0));
    }

    /// <summary>Encodes a linear value in 0..1 to 8-bit sRGB, exactly: the coarse table gives a start, the thresholds settle it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Encode(float linear)
    {
        // The weighted sum is never negative and exceeds 1 by at most float rounding, so only the top needs a bound.
        int index = Math.Min((int)(linear * (CoarseSteps - 1)), CoarseSteps - 1);
        int v = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Coarse), index);
        // A coarse step spans at most one threshold (checked by LumaTests), so two branch-free steps always settle it,
        // with one to spare for float rounding in the weighted sum.
        ref float thresholds = ref MemoryMarshal.GetArrayDataReference(Thresholds);
        v += linear >= Unsafe.Add(ref thresholds, v) ? 1 : 0;
        v += linear >= Unsafe.Add(ref thresholds, v) ? 1 : 0;
        return (byte)v;
    }

    /// <summary>Returns the most thresholds that fall inside one coarse step. Used by tests to check <see cref="Encode"/>.</summary>
    internal static int MaxThresholdsPerStep()
    {
        int most = 0;
        for (int i = 0; i < CoarseSteps - 1; i++)
        {
            float start = i / (float)(CoarseSteps - 1);
            float end = (i + 1) / (float)(CoarseSteps - 1);
            most = Math.Max(most, Thresholds.Count(t => t > start && t <= end));
        }

        return most;
    }

    private static float[] BuildWeighted()
    {
        float[] table = new float[3 * 256];
        for (int i = 0; i < 256; i++)
        {
            double linear = Srgb.ToLinear(i / 255.0);
            table[i] = (float)(0.2126 * linear);
            table[256 + i] = (float)(0.7152 * linear);
            table[512 + i] = (float)(0.0722 * linear);
        }

        return table;
    }

    private static float[] BuildThresholds()
    {
        float[] table = new float[256];
        for (int v = 0; v < 255; v++)
        {
            table[v] = (float)Srgb.ToLinear((v + 0.5) / 255.0);
        }

        table[255] = float.PositiveInfinity;
        return table;
    }

    private static byte[] BuildCoarse()
    {
        // The value for x is the number of thresholds at or below x; taking it at the start of each step keeps it a lower bound.
        byte[] table = new byte[CoarseSteps];
        int v = 0;
        for (int i = 0; i < CoarseSteps; i++)
        {
            float x = i / (float)(CoarseSteps - 1);
            while (v < 255 && x >= Thresholds[v])
            {
                v++;
            }

            table[i] = (byte)v;
        }

        return table;
    }
}
