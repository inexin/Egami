namespace Egami.Pixels;

/// <summary>Exact conversions between channel depths.</summary>
internal static class Scale
{
    /// <summary>Clamps a scaled value to 0..1. NaN becomes 0.</summary>
    internal static float Clamp(float value)
    {
        return value > 0f ? MathF.Min(value, 1f) : 0f;
    }

    /// <summary>Rounds a scaled value to the nearest 8-bit value, clamping first.</summary>
    internal static byte ToByte(float value)
    {
        return (byte)((Clamp(value) * 255f) + 0.5f);
    }

    /// <summary>Rounds a scaled value to the nearest 16-bit value, clamping first.</summary>
    internal static ushort ToUInt16(float value)
    {
        return (ushort)((Clamp(value) * 65535f) + 0.5f);
    }

    /// <summary>Widens 8 bits to 16 exactly: 0 maps to 0 and 255 to 65535.</summary>
    internal static ushort Widen(byte value)
    {
        return (ushort)(value * 257);
    }

    /// <summary>Narrows 16 bits to the nearest 8-bit value.</summary>
    internal static byte Narrow(ushort value)
    {
        return (byte)(((value * 255) + 32895) >> 16);
    }
}
