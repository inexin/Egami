namespace Egami.Pixels;

/// <summary>The IEC 61966-2-1 sRGB transfer function, exact and table-driven.</summary>
internal static class Srgb
{
    private const int EncodeTableSize = 4096;

    /// <summary>Gets the linear value, 0..1, of each 8-bit encoded value.</summary>
    internal static float[] LinearFrom8 { get; } = BuildLinearFrom8();

    /// <summary>
    /// Encoded value times 255 at linear x = i / 4095, for i = 0..4095,
    /// plus one guard entry so interpolation can always read entry i + 1.
    /// </summary>
    private static readonly float[] EncodeTable = BuildEncodeTable();

    /// <summary>Converts an sRGB-encoded value in 0..1 to linear light.</summary>
    internal static double ToLinear(double encoded)
    {
        return encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);
    }

    /// <summary>Converts a linear-light value in 0..1 to sRGB encoding.</summary>
    internal static double FromLinear(double linear)
    {
        return linear <= 0.0031308 ? linear * 12.92 : (1.055 * Math.Pow(linear, 1.0 / 2.4)) - 0.055;
    }

    /// <summary>Encodes a linear value in 0..1 to the nearest 8-bit sRGB value, using the interpolated table.</summary>
    internal static byte FromLinearTo8(float linear)
    {
        if (!(linear > 0f))
        {
            return 0;
        }

        if (linear >= 1f)
        {
            return 255;
        }

        float x = linear * (EncodeTableSize - 1);
        int i = (int)x;
        float fraction = x - i;
        float a = EncodeTable[i];
        float encoded = a + (fraction * (EncodeTable[i + 1] - a));
        return (byte)(encoded + 0.5f);
    }

    private static float[] BuildLinearFrom8()
    {
        float[] table = new float[256];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = (float)ToLinear(i / 255.0);
        }

        return table;
    }

    private static float[] BuildEncodeTable()
    {
        float[] table = new float[EncodeTableSize + 1];
        for (int i = 0; i < EncodeTableSize; i++)
        {
            table[i] = (float)(FromLinear(i / (double)(EncodeTableSize - 1)) * 255.0);
        }

        table[EncodeTableSize] = table[EncodeTableSize - 1];
        return table;
    }
}
