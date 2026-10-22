using System.Numerics;
using System.Text;

using Egami.Barcodes.ErrorCorrection;
using Egami.Barcodes.Matrix;

namespace Egami.Barcodes.Reading;

/// <summary>
/// Reads a sampled QR Code grid (ISO/IEC 18004): the format information (the nearest valid word to either copy), the version
/// information from version 7, the unmasked codewords in zigzag order, the Reed-Solomon blocks, and the segments.
/// </summary>
internal static class QrDecoder
{
    private const string Alphanumeric = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    /// <summary>Decodes the grid, then its mirror image when the grid itself does not decode.</summary>
    public static DecodedData? Decode(BarcodeModules grid)
    {
        if (DecodeOnce(grid) is DecodedData data)
        {
            return data;
        }

        return DecodeOnce(Transpose(grid)) is DecodedData mirrored ? mirrored with { Mirrored = true } : null;
    }

    internal static BarcodeModules Transpose(BarcodeModules grid)
    {
        int n = grid.Width;
        bool[] cells = new bool[n * n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                cells[(x * n) + y] = grid[x, y];
            }
        }

        return BarcodeModules.Wrap(n, n, cells);
    }

    /// <summary>Returns the version written in the grid's version information (from version 7), or the one its size gives.</summary>
    internal static int Version(BarcodeModules grid)
    {
        int size = grid.Width, version = (size - 17) / 4;
        if (version < 7)
        {
            return version;
        }

        int best = version, bestDistance = int.MaxValue;
        int topRight = 0, bottomLeft = 0;
        for (int i = 17; i >= 0; i--)
        {
            topRight = (topRight << 1) | (grid[size - 11 + (i % 3), i / 3] ? 1 : 0);
            bottomLeft = (bottomLeft << 1) | (grid[i / 3, size - 11 + (i % 3)] ? 1 : 0);
        }

        for (int v = 7; v <= 40; v++)
        {
            int word = QrMatrix.VersionBits(v);
            int distance = Math.Min(BitOperations.PopCount((uint)(word ^ topRight)), BitOperations.PopCount((uint)(word ^ bottomLeft)));
            if (distance < bestDistance)
            {
                (best, bestDistance) = (v, distance);
            }
        }

        return bestDistance <= 3 ? best : version;
    }

    private static readonly int[]?[] s_placements = new int[]?[41];

    /// <summary>The data modules of a version (y · 256 + x) in placement order: the zigzag of column pairs from the right.</summary>
    private static int[] Placement(int version)
    {
        int[]? placement = Volatile.Read(ref s_placements[version]);
        if (placement is not null)
        {
            return placement;
        }

        int size = 17 + (4 * version), total = QrTables.TotalCodewords[version - 1] * 8;
        bool[] function = QrMatrix.FunctionMask(version);
        placement = new int[total];
        int bit = 0;
        for (int right = size - 1; right >= 1 && bit < total; right -= 2)
        {
            if (right == 6)
            {
                right = 5;
            }

            for (int vertical = 0; vertical < size; vertical++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    int y = ((right + 1) & 2) == 0 ? size - 1 - vertical : vertical;
                    if (!function[(y * size) + x] && bit < total)
                    {
                        placement[bit++] = (y << 8) | x;
                    }
                }
            }
        }

        Interlocked.CompareExchange(ref s_placements[version], placement, null);
        return s_placements[version]!;
    }

    private static DecodedData? DecodeOnce(BarcodeModules grid)
    {
        int size = grid.Width;
        if (size != grid.Height || size < 21 || size > 177 || (size - 17) % 4 != 0)
        {
            return null;
        }

        int version = Version(grid);
        if (17 + (4 * version) != size)
        {
            return null;
        }

        (int level, int mask)? format = Format(grid);
        if (format is not (int level, int mask))
        {
            return null;
        }

        // The data modules in placement order, unmasked.
        int total = QrTables.TotalCodewords[version - 1];
        byte[] codewords = new byte[total];
        int[] placement = Placement(version);
        for (int bit = 0; bit < placement.Length; bit++)
        {
            int x = placement[bit] & 0xFF, y = placement[bit] >> 8;
            if (grid[x, y] != QrMatrix.Masked(mask, x, y))
            {
                codewords[bit >> 3] |= (byte)(0x80 >> (bit & 7));
            }
        }

        // Undo the interleaving, correct each block.
        int dataCount = QrTables.DataCodewords(version, level), blocks = QrTables.Blocks(version, level);
        int checkCount = (total - dataCount) / blocks, shortBlocks = blocks - (total % blocks);
        int shortData = (total / blocks) - checkCount;
        int[][] block = new int[blocks][];
        for (int b = 0; b < blocks; b++)
        {
            block[b] = new int[shortData + (b < shortBlocks ? 0 : 1) + checkCount];
        }

        int k = 0;
        for (int i = 0; i <= shortData; i++)
        {
            for (int b = 0; b < blocks; b++)
            {
                if (i < shortData + (b < shortBlocks ? 0 : 1))
                {
                    block[b][i] = codewords[k++];
                }
            }
        }

        for (int i = 0; i < checkCount; i++)
        {
            for (int b = 0; b < blocks; b++)
            {
                block[b][block[b].Length - checkCount + i] = codewords[k++];
            }
        }

        byte[] data = new byte[dataCount];
        int corrected = 0, d = 0;
        foreach (int[] b in block)
        {
            int fixedCount = ReedSolomon.Decode(GaloisField.Qr, b, checkCount);
            if (fixedCount < 0)
            {
                return null;
            }

            corrected += fixedCount;
            for (int i = 0; i < b.Length - checkCount; i++)
            {
                data[d++] = (byte)b[i];
            }
        }

        return Parse(data, version) is DecodedData parsed ? parsed with { ErrorsCorrected = corrected } : null;
    }

    /// <summary>Reads both copies of the format information and returns the level and mask of the nearest valid word (at most 3 bits off).</summary>
    private static (int Level, int Mask)? Format(BarcodeModules grid)
    {
        int size = grid.Width;
        int first = 0, second = 0;
        foreach ((int x, int y) in (ReadOnlySpan<(int, int)>)[(0, 8), (1, 8), (2, 8), (3, 8), (4, 8), (5, 8), (7, 8), (8, 8), (8, 7), (8, 5), (8, 4), (8, 3), (8, 2), (8, 1), (8, 0)])
        {
            first = (first << 1) | (grid[x, y] ? 1 : 0);
        }

        for (int i = 14; i >= 0; i--)
        {
            bool value = i >= 8 ? grid[8, size - 15 + i] : grid[size - 1 - i, 8];
            second = (second << 1) | (value ? 1 : 0);
        }

        (int, int)? best = null;
        int bestDistance = 4;
        for (int level = 0; level < 4; level++)
        {
            for (int mask = 0; mask < 8; mask++)
            {
                int word = QrMatrix.FormatBits(level, mask);
                int distance = Math.Min(BitOperations.PopCount((uint)(word ^ first)), BitOperations.PopCount((uint)(word ^ second)));
                if (distance < bestDistance)
                {
                    (best, bestDistance) = ((level, mask), distance);
                }
            }
        }

        return best;
    }

    /// <summary>Parses the segments: numeric, alphanumeric, byte, Kanji, ECI, FNC1 and Structured Append headers.</summary>
    private static DecodedData? Parse(byte[] data, int version, bool assumeShiftJis = false)
    {
        var reader = new BitReader(data);
        var text = new TextBuilder { Fallback = assumeShiftJis ? TextBuilder.EncodingFor(20) : null };
        bool bytesBeforeKanji = false;
        int range = version <= 9 ? 0 : version <= 26 ? 1 : 2;
        bool kanjiSeen = false;
        while (reader.Available >= 4)
        {
            int mode = reader.Read(4);
            switch (mode)
            {
                case 0:
                    reader.Skip();
                    break;
                case 0b0111:
                    if (ReadEci(reader) is not int eci)
                    {
                        return null;
                    }

                    text.SetEci(eci);
                    break;
                case 0b0101:
                    text.Gs1 = true;
                    break;
                case 0b1001:
                    reader.Read(8);
                    break;
                case 0b0011:
                    reader.Read(16);
                    break;
                case 0b0001:
                    if (!Numeric(reader, reader.Read(10 + (2 * range)), text))
                    {
                        return null;
                    }

                    break;
                case 0b0010:
                    if (!AlphanumericSegment(reader, reader.Read(9 + (2 * range)), text))
                    {
                        return null;
                    }

                    break;
                case 0b0100:
                    int count = reader.Read(range == 0 ? 8 : 16);
                    if (reader.Available < count * 8)
                    {
                        return null;
                    }

                    for (int i = 0; i < count; i++)
                    {
                        text.Append((byte)reader.Read(8));
                    }

                    bytesBeforeKanji |= !kanjiSeen;

                    break;
                case 0b1000:
                    int characters = reader.Read(8 + (2 * range));
                    if (reader.Available < characters * 13)
                    {
                        return null;
                    }

                    byte[] shiftJis = new byte[characters * 2];
                    for (int i = 0; i < characters; i++)
                    {
                        int value = reader.Read(13);
                        int code = ((value / 0xC0) << 8) | (value % 0xC0);
                        code += code < 0x1F00 ? 0x8140 : 0xC140;
                        shiftJis[2 * i] = (byte)(code >> 8);
                        shiftJis[(2 * i) + 1] = (byte)code;
                    }

                    if (!kanjiSeen && text.Eci is null)
                    {
                        // Kanji mode means Shift JIS: bytes read before it are Shift JIS too, so read them again.
                        if (bytesBeforeKanji && !assumeShiftJis)
                        {
                            return Parse(data, version, assumeShiftJis: true);
                        }

                        text.Fallback = TextBuilder.EncodingFor(20);
                    }

                    kanjiSeen = true;
                    text.Append(shiftJis);
                    break;
                default:
                    return null;
            }
        }

        (string result, IReadOnlyList<Gs1ElementString>? elements) = text.Result();
        return new DecodedData(BarcodeFormat.Qr, result, text.Bytes) { Eci = text.Eci, Gs1 = elements };
    }

    internal static int? ReadEci(BitReader reader)
    {
        int first = reader.Read(8);
        if ((first & 0x80) == 0)
        {
            return first;
        }

        if ((first & 0xC0) == 0x80)
        {
            return ((first & 0x3F) << 8) | reader.Read(8);
        }

        return (first & 0xE0) == 0xC0 ? ((first & 0x1F) << 16) | reader.Read(16) : null;
    }

    private static bool Numeric(BitReader reader, int count, TextBuilder text)
    {
        var digits = new StringBuilder(count);
        while (count > 0)
        {
            int take = Math.Min(3, count), value = reader.Read((take * 3) + 1);
            if (value >= (take == 3 ? 1000 : take == 2 ? 100 : 10))
            {
                return false;
            }

            digits.Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(take, '0'));
            count -= take;
        }

        text.AppendText(digits.ToString());
        return reader.Available >= 0;
    }

    private static bool AlphanumericSegment(BitReader reader, int count, TextBuilder text)
    {
        var chars = new StringBuilder(count);
        while (count >= 2)
        {
            int value = reader.Read(11);
            if (value >= 45 * 45)
            {
                return false;
            }

            chars.Append(Alphanumeric[value / 45]).Append(Alphanumeric[value % 45]);
            count -= 2;
        }

        if (count == 1)
        {
            int value = reader.Read(6);
            if (value >= 45)
            {
                return false;
            }

            chars.Append(Alphanumeric[value]);
        }

        // In GS1 data, % stands for the separator GS and %% for %.
        string s = chars.ToString();
        if (text.Gs1)
        {
            s = s.Replace("%%", "\0", StringComparison.Ordinal).Replace('%', '\x1D').Replace('\0', '%');
        }

        text.AppendText(s);
        return true;
    }
}

/// <summary>Reads bits from bytes, most significant first.</summary>
internal sealed class BitReader(byte[] bytes)
{
    private int _position;

    public int Available => (bytes.Length * 8) - _position;

    public int Read(int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++, _position++)
        {
            int bit = _position < bytes.Length * 8 ? (bytes[_position >> 3] >> (7 - (_position & 7))) & 1 : 0;
            value = (value << 1) | bit;
        }

        return value;
    }

    /// <summary>Skips to the end.</summary>
    public void Skip() => _position = bytes.Length * 8;
}
