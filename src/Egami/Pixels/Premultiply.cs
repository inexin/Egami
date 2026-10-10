namespace Egami.Pixels;

/// <summary>Exact 8-bit premultiplication: round(c × a / 255) one way, round(c × 255 / a) the other, half up.</summary>
internal static class Premultiply
{
    /// <summary>Premultiplies an 8-bit color channel.</summary>
    internal static byte Times(byte color, byte alpha)
    {
        return (byte)(((color * alpha) + 127) / 255);
    }

    /// <summary>Premultiplies a scaled color channel by a scaled alpha, both clamped, rounding to 8 bits.</summary>
    internal static byte Scaled(float color, float alpha)
    {
        return (byte)((Scale.Clamp(color) * alpha * 255f) + 0.5f);
    }

    /// <summary>Unpremultiplies an 8-bit channel; a color above its alpha counts as the alpha, and zero alpha gives zero.</summary>
    internal static byte Over(byte color, byte alpha)
    {
        return alpha == 0 ? (byte)0 : (byte)(((Math.Min(color, alpha) * 255) + (alpha / 2)) / alpha);
    }
}
