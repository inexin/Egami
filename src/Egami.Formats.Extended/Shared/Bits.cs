using Egami.Pixels;

namespace Egami.Formats.Extended;

/// <summary>One-bit rows: unpacked to gray and packed from it.</summary>
internal static class Bits
{
    /// <summary>
    /// Writes one gray pixel per bit of <paramref name="packed"/> into <paramref name="row"/>, as far as either goes:
    /// <paramref name="one"/> for a set bit and <paramref name="zero"/> for a clear one.
    /// </summary>
    public static void Expand(ReadOnlySpan<byte> packed, Span<Gray8> row, bool msbFirst, byte one, byte zero)
    {
        int count = (int)Math.Min(row.Length, packed.Length * 8L);
        for (int x = 0; x < count; x++)
        {
            int bit = msbFirst ? 7 - (x & 7) : x & 7;
            row[x] = new Gray8(((packed[x >> 3] >> bit) & 1) != 0 ? one : zero);
        }
    }

    /// <summary>
    /// Packs <paramref name="row"/> into bits, a set bit for a light pixel (gray 128 or more) when <paramref name="oneIsLight"/>
    /// and for a dark one otherwise. The bits after the last pixel are clear.
    /// </summary>
    public static void Pack(ReadOnlySpan<Gray8> row, Span<byte> packed, bool msbFirst, bool oneIsLight)
    {
        packed.Clear();
        for (int x = 0; x < row.Length; x++)
        {
            if ((row[x].L >= 128) == oneIsLight)
            {
                packed[x >> 3] |= (byte)(1 << (msbFirst ? 7 - (x & 7) : x & 7));
            }
        }
    }
}
