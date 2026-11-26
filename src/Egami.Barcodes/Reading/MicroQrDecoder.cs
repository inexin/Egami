using System.Numerics;

using Egami.Barcodes.ErrorCorrection;
using Egami.Barcodes.Matrix;

namespace Egami.Barcodes.Reading;

/// <summary>
/// Reads a sampled Micro QR Code grid (ISO/IEC 18004, M1 to M4): the format word (the nearest of the 32 valid ones), whose
/// symbol number must suit the grid's size; the unmasked data modules in placement order; the one Reed-Solomon block (M1's
/// two check codewords only detect errors); and the segments.
/// </summary>
internal static class MicroQrDecoder
{
    private static readonly int[]?[] s_placements = new int[]?[5];

    /// <summary>Decodes the grid, then its mirror image when the grid itself does not decode.</summary>
    public static DecodedData? Decode(BarcodeModules grid)
    {
        if (DecodeOnce(grid) is DecodedData data)
        {
            return data;
        }

        return DecodeOnce(QrDecoder.Transpose(grid)) is DecodedData mirrored ? mirrored with { Mirrored = true } : null;
    }

    /// <summary>Returns the version (1 to 4) and level (0 L, 1 M, 2 Q) of a symbol number.</summary>
    private static (int Version, int Level) Symbol(int number) => number switch
    {
        0 => (1, 0),
        7 => (4, 2),
        _ => (((number - 1) / 2) + 2, (number - 1) % 2),
    };

    /// <summary>The data modules of a version (y · 256 + x) in placement order.</summary>
    private static int[] Placement(int version)
    {
        int[]? placement = Volatile.Read(ref s_placements[version]);
        if (placement is not null)
        {
            return placement;
        }

        int size = MicroQrEncoder.Size(version);
        placement = [.. MicroQrEncoder.Order(size, MicroQrEncoder.FunctionMask(size)).Select(i => ((i / size) << 8) | (i % size))];
        Interlocked.CompareExchange(ref s_placements[version], placement, null);
        return s_placements[version]!;
    }

    private static DecodedData? DecodeOnce(BarcodeModules grid)
    {
        int size = grid.Width;
        if (size != grid.Height || size is < 11 or > 17 || size % 2 == 0)
        {
            return null;
        }

        // The format word: row 8 from column 1, then column 8 from row 7 up.
        int word = 0;
        for (int i = 1; i <= 8; i++)
        {
            word = (word << 1) | (grid[i, 8] ? 1 : 0);
        }

        for (int i = 7; i >= 1; i--)
        {
            word = (word << 1) | (grid[8, i] ? 1 : 0);
        }

        int best = -1, bestDistance = 4;
        for (int candidate = 0; candidate < 32; candidate++)
        {
            int distance = BitOperations.PopCount((uint)(MicroQrEncoder.FormatBits(candidate >> 2, candidate & 3) ^ word));
            if (distance < bestDistance)
            {
                (best, bestDistance) = (candidate, distance);
            }
        }

        if (best < 0)
        {
            return null;
        }

        (int version, int level) = Symbol(best >> 2);
        int mask = best & 3;
        if (MicroQrEncoder.Size(version) != size)
        {
            return null;
        }

        // The data bits (the last codeword of M1 and M3 is 4 bits, kept in the high half), then the check codewords.
        int capacity = MicroQrEncoder.DataBits(version, level), dataCount = MicroQrEncoder.DataCodewords(version, level);
        int total = MicroQrEncoder.Total(version), checkCount = total - dataCount;
        int[] codewords = new int[total];
        int[] placement = Placement(version);
        int bits = capacity + (8 * checkCount);
        for (int bit = 0; bit < bits; bit++)
        {
            int x = placement[bit] & 0xFF, y = placement[bit] >> 8;
            if (grid[x, y] != MicroQrEncoder.Masked(mask, x, y))
            {
                int at = bit < capacity ? bit : bit - capacity + (dataCount * 8);
                codewords[at >> 3] |= 0x80 >> (at & 7);
            }
        }

        int corrected = ReedSolomon.Decode(GaloisField.Qr, codewords, checkCount);
        if (corrected < 0 || (version == 1 && corrected > 0) || (capacity % 8 != 0 && (codewords[dataCount - 1] & 0x0F) != 0))
        {
            return null;
        }

        byte[] data = new byte[dataCount];
        for (int i = 0; i < dataCount; i++)
        {
            data[i] = (byte)codewords[i];
        }

        return QrDecoder.Parse(data, version, micro: version, length: capacity) is DecodedData parsed ? parsed with { ErrorsCorrected = corrected } : null;
    }
}
