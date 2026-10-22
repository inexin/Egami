// Follows the Vorbis I specification (Xiph.Org): helpers shared by the decoder, after section 9 "Helper equations".
using System.Numerics;

namespace Egami.Video.Audio.Codecs.Vorbis;

/// <summary>Errors and helper equations the Vorbis decoder shares.</summary>
internal static class Vorbis
{
    public static CorruptDataException Corrupt(string problem) => new("Vorbis", -1, problem);

    public static UnsupportedFormatException Unsupported(string problem) => new("Vorbis: " + problem);

    /// <summary>ilog (9.2.1): the position of the highest set bit, counting from one; zero for zero and negative values.</summary>
    public static int ILog(int x) => x <= 0 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)x);

    /// <summary>float32_unpack (9.2.2).</summary>
    public static float Float32Unpack(uint x)
    {
        int mantissa = (int)(x & 0x1FFFFF);
        int exponent = (int)((x & 0x7FE00000) >> 21);
        if ((x & 0x80000000) != 0)
        {
            mantissa = -mantissa;
        }

        return (float)Math.ScaleB(mantissa, exponent - 788);
    }

    /// <summary>lookup1_values (9.2.3): the greatest r with r^dimensions not above entries.</summary>
    public static int Lookup1Values(int entries, int dimensions)
    {
        int r = (int)Math.Floor(Math.Pow(entries, 1.0 / dimensions));
        while (r > 0 && PowerAbove(r, dimensions, entries))
        {
            r--;
        }

        while (!PowerAbove(r + 1, dimensions, entries))
        {
            r++;
        }

        return r;
    }

    private static bool PowerAbove(long r, int dimensions, long limit)
    {
        long p = 1;
        for (int i = 0; i < dimensions; i++)
        {
            p *= r;
            if (p > limit)
            {
                return true;
            }
        }

        return false;
    }
}
