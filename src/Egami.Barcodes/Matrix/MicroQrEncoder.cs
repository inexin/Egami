using Egami.Barcodes.ErrorCorrection;

namespace Egami.Barcodes.Matrix;

/// <summary>
/// Micro QR Code (ISO/IEC 18004:2015, versions M1 to M4): the QR segment modes with Micro QR's shorter mode indicators and
/// character counts, the smallest version that holds the data at the level asked for, the level raised while the version
/// holds, one Reed-Solomon block, and a symbol with a single finder, timing along the top and left edges, and the mask whose
/// dark modules along the right and bottom edges score highest (section 7.8.3.2). Laid out as zint's backend (qr.c, BSD-3; see
/// THIRD-PARTY-NOTICES.md) lays it out, so that the symbols equal zint's.
/// </summary>
internal static class MicroQrEncoder
{
    /// <summary>The symbol's version (1 to 4 for M1 to M4), level (0 L, 1 M, 2 Q), mask (0 to 3) and modules.</summary>
    public readonly record struct Result(int Version, int Level, int Mask, BarcodeModules Modules);

    /// <summary>Data bits by level (L, M, Q) and version (M1 to M4); 0 where the version has no such level (Table 7).</summary>
    private static ReadOnlySpan<byte> DataBitsTable => [20, 40, 84, 128, 0, 32, 68, 112, 0, 0, 0, 80];

    /// <summary>Total codewords by version (Table 1).</summary>
    private static ReadOnlySpan<byte> TotalCodewords => [5, 10, 17, 24];

    /// <summary>Returns the data bits of a version (1 to 4) at a level (0 L, 1 M, 2 Q), 0 when it has no such level.</summary>
    public static int DataBits(int version, int level) => DataBitsTable[(level * 4) + version - 1];

    /// <summary>Returns the data codewords of a version and level: M1 and M3 end with one of 4 bits.</summary>
    public static int DataCodewords(int version, int level) => (DataBits(version, level) + 4) / 8;

    /// <summary>Returns the total codewords of a version.</summary>
    public static int Total(int version) => TotalCodewords[version - 1];

    /// <summary>Returns the symbol number of the format information: 0 for M1, then M2-L, M2-M, M3-L, M3-M, M4-L, M4-M, M4-Q.</summary>
    public static int SymbolNumber(int version, int level) => version == 1 ? 0 : ((version - 2) * 2) + level + 1;

    /// <summary>Encodes the input.</summary>
    /// <param name="input">The data, without an ECI or GS1.</param>
    /// <param name="level">The level (0 L, 1 M, 2 Q).</param>
    /// <param name="raiseLevel">Whether to raise the level while the symbol stays the same version.</param>
    /// <param name="minimumVersion">The smallest version allowed, 1 to 4.</param>
    /// <param name="fixedMask">A mask to use, 0 to 3, or -1 for the one that scores highest.</param>
    /// <returns>The symbol, or <see langword="null"/> when the data does not fit.</returns>
    public static Result? Encode(QrEncoder.Input input, int level, bool raiseLevel, int minimumVersion, int fixedMask)
    {
        BitBuffer? bits = null;
        int version = 0;
        for (int v = minimumVersion; v <= 4; v++)
        {
            if (DataBits(v, level) > 0 && QrEncoder.Bits(input, 0, v) is BitBuffer b && b.Count <= DataBits(v, level))
            {
                (bits, version) = (b, v);
                break;
            }
        }

        if (bits is null)
        {
            return null;
        }

        while (raiseLevel && version > 1 && level < 2 && DataBits(version, level + 1) > 0 && bits.Count <= DataBits(version, level + 1))
        {
            level++;
        }

        bool[] stream = Stream(bits, version, level);
        (bool[] modules, int mask) = Build(version, level, stream, fixedMask);
        int size = Size(version);
        return new Result(version, level, mask, BarcodeModules.Wrap(size, size, modules));
    }

    /// <summary>Returns the width of a version's symbol in modules: 11, 13, 15 or 17.</summary>
    public static int Size(int version) => 9 + (2 * version);

    /// <summary>
    /// Adds the terminator (3, 5, 7 or 9 zero bits, cut short at the end of the data) and the padding, splits the data into
    /// codewords (the last of M1 and M3 4 bits), and returns the data bits followed by the Reed-Solomon codewords.
    /// </summary>
    private static bool[] Stream(BitBuffer bits, int version, int level)
    {
        int capacity = DataBits(version, level), dataCount = DataCodewords(version, level);
        bool shortLast = capacity % 8 != 0;
        bits.Append(0, Math.Min((2 * version) + 1, capacity - bits.Count));
        if (bits.Count < capacity)
        {
            // Whole codewords, then pad codewords; a short last codeword takes zeros.
            int dataEnd = shortLast ? capacity - 4 : capacity;
            if (bits.Count < dataEnd)
            {
                bits.Append(0, (8 - (bits.Count % 8)) % 8);
                for (int pad = 0; bits.Count < dataEnd; pad++)
                {
                    bits.Append(pad % 2 == 0 ? 0xEC : 0x11, 8);
                }
            }

            bits.Append(0, capacity - bits.Count);
        }

        // The codewords: the short last one in the high 4 bits.
        int[] data = new int[dataCount];
        for (int i = 0; i < capacity; i++)
        {
            if (bits[i])
            {
                data[i / 8] |= 0x80 >> (i % 8);
            }
        }

        int checkCount = Total(version) - dataCount;
        int[] check = new int[checkCount];
        ReedSolomon.Encode(GaloisField.Qr, data, check);
        bool[] stream = new bool[capacity + (8 * checkCount)];
        for (int i = 0; i < capacity; i++)
        {
            stream[i] = bits[i];
        }

        for (int i = 0; i < 8 * checkCount; i++)
        {
            stream[capacity + i] = ((check[i / 8] >> (7 - (i % 8))) & 1) != 0;
        }

        return stream;
    }

    /// <summary>Builds the symbol: its modules row by row and the mask used.</summary>
    internal static (bool[] Modules, int Mask) Build(int version, int level, bool[] stream, int fixedMask)
    {
        int size = Size(version);
        bool[] dark = new bool[size * size];
        bool[] function = FunctionMask(size, dark);
        Place(dark, function, size, stream);

        int mask = fixedMask, best = -1;
        for (int m = 0; fixedMask < 0 && m < 4; m++)
        {
            ApplyMask(dark, function, size, m);
            int score = Score(dark, size);
            if (score > best)
            {
                (best, mask) = (score, m);
            }

            ApplyMask(dark, function, size, m);
        }

        ApplyMask(dark, function, size, mask);
        int format = FormatBits(SymbolNumber(version, level), mask);
        for (int i = 1; i <= 8; i++)
        {
            dark[(8 * size) + i] = ((format >> (15 - i)) & 1) != 0;
        }

        for (int i = 1; i <= 7; i++)
        {
            dark[(i * size) + 8] = ((format >> (i - 1)) & 1) != 0;
        }

        return (dark, mask);
    }

    /// <summary>Returns the 15-bit format word: symbol number and mask, a BCH(15, 5) remainder, masked with 0x4445.</summary>
    internal static int FormatBits(int symbolNumber, int mask)
    {
        int data = (symbolNumber << 2) | mask, remainder = data;
        for (int i = 0; i < 10; i++)
        {
            remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
        }

        return ((data << 10) | remainder) ^ 0x4445;
    }

    /// <summary>
    /// Returns which modules are function patterns or the format area, not data, and draws the patterns into
    /// <paramref name="dark"/> when it is given: the finder at the top left with its separator, and timing along row 0 and
    /// column 0.
    /// </summary>
    internal static bool[] FunctionMask(int size, bool[]? dark = null)
    {
        bool[] function = new bool[size * size];
        for (int i = 0; i < size; i++)
        {
            function[i] = function[i * size] = true;
            if (dark is not null)
            {
                dark[i] = dark[i * size] = i % 2 == 0;
            }
        }

        for (int y = 0; y <= 8; y++)
        {
            for (int x = 0; x <= 8; x++)
            {
                function[(y * size) + x] = true;
                int distance = Math.Max(Math.Abs(x - 3), Math.Abs(y - 3));
                if (dark is not null && x <= 7 && y <= 7)
                {
                    dark[(y * size) + x] = distance is not (2 or 4);
                }
            }
        }

        return function;
    }

    /// <summary>Places the bits in two-module columns from the bottom right, up and down in turn, over rows 1 to the last.</summary>
    internal static void Place(bool[] dark, bool[] function, int size, bool[] stream)
    {
        int i = 0;
        foreach (int index in Order(size, function))
        {
            if (i == stream.Length)
            {
                break;
            }

            dark[index] = stream[i++];
        }
    }

    /// <summary>The data modules in the order the bits fill them.</summary>
    internal static IEnumerable<int> Order(int size, bool[] function)
    {
        bool up = true;
        for (int x = size - 2; x >= 1; x -= 2)
        {
            for (int k = 0; k < size - 1; k++)
            {
                int y = up ? size - 1 - k : 1 + k;
                for (int j = 1; j >= 0; j--)
                {
                    int index = (y * size) + x + j;
                    if (!function[index])
                    {
                        yield return index;
                    }
                }
            }

            up = !up;
        }
    }

    /// <summary>Returns whether a data module is inverted by a mask (0 to 3: QR's masks 1, 4, 6 and 7).</summary>
    internal static bool Masked(int mask, int x, int y) => mask switch
    {
        0 => y % 2 == 0,
        1 => ((y / 2) + (x / 3)) % 2 == 0,
        2 => (((y * x) % 2) + ((y * x) % 3)) % 2 == 0,
        _ => (((y + x) % 2) + ((y * x) % 3)) % 2 == 0,
    };

    private static void ApplyMask(bool[] dark, bool[] function, int size, int mask)
    {
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (!function[(y * size) + x] && Masked(mask, x, y))
                {
                    dark[(y * size) + x] = !dark[(y * size) + x];
                }
            }
        }
    }

    /// <summary>The mask score: the dark modules along the right edge and along the bottom, the smaller count weighted 16.</summary>
    private static int Score(bool[] dark, int size)
    {
        int right = 0, bottom = 0;
        for (int i = 1; i < size; i++)
        {
            right += dark[(i * size) + size - 1] ? 1 : 0;
            bottom += dark[((size - 1) * size) + i] ? 1 : 0;
        }

        return right <= bottom ? (right * 16) + bottom : (bottom * 16) + right;
    }
}
