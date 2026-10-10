using System.Runtime.CompilerServices;

namespace Egami.Pixels;

/// <summary>
/// Exact conversions for packed channels. Both directions round to the nearest value, round(v × wider / narrower), so every
/// route (through 8 bits, 16 bits or floats) gives the same pixel and every packed value survives a round trip. For 2 and 4 bits
/// that is a plain multiply; for 5, 6 and 10 bits it differs from replicating the high bits for a few levels.
/// </summary>
internal static class Packing
{
    /// <summary>Rounds a scaled value to the nearest of <paramref name="max"/> + 1 levels, clamping first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Level(float value, int max)
    {
        return (int)((Scale.Clamp(value) * max) + 0.5f);
    }

    /// <summary>Rounds an 8-bit value to the nearest of <paramref name="max"/> + 1 levels: round(v × max / 255).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Narrow8(byte value, int max)
    {
        return ((value * max) + 127) / 255;
    }

    /// <summary>Rounds a 16-bit value to the nearest of <paramref name="max"/> + 1 levels: round(v × max / 65535).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Narrow16(ushort value, int max)
    {
        return (int)((((long)value * max) + 32767) / 65535);
    }

    /// <summary>Rounds a 10-bit level to the nearest 8-bit value: round(v × 255 / 1023).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte Narrow10To8(int value)
    {
        return (byte)(((value * 255) + 511) / 1023);
    }

    /// <summary>Widens a 5-bit level to the nearest 8-bit value: round(v × 255 / 31).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte Widen5(int value)
    {
        return (byte)(((value * 255) + 15) / 31);
    }

    /// <summary>Widens a 6-bit level to the nearest 8-bit value: round(v × 255 / 63).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte Widen6(int value)
    {
        return (byte)(((value * 255) + 31) / 63);
    }

    /// <summary>Widens a 10-bit level to the nearest 16-bit value: round(v × 65535 / 1023).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort Widen10(int value)
    {
        return (ushort)(((value * 65535) + 511) / 1023);
    }
}
