namespace Egami.Pixels;

/// <summary>Rec. 709 luma of sRGB-encoded colors, weighted in linear light.</summary>
internal static class Luma
{
    private const float Red = 0.2126f;
    private const float Green = 0.7152f;
    private const float Blue = 0.0722f;

    /// <summary>Returns the 8-bit luma of an 8-bit color. Gray input is returned unchanged.</summary>
    internal static byte Of8(byte r, byte g, byte b)
    {
        if (r == g && g == b)
        {
            return r;
        }

        float[] linear = Srgb.LinearFrom8;
        return Srgb.FromLinearTo8((Red * linear[r]) + (Green * linear[g]) + (Blue * linear[b]));
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
}
