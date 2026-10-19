using Egami.Barcodes.ErrorCorrection;
using Egami.Barcodes.Matrix;

namespace Egami.Barcodes.Reading;

/// <summary>
/// Reads a sampled Aztec Code grid (ISO/IEC 24778): compact or full-range from the bullseye, the mode message (any of the four
/// rotations, mirrored too), the data layers, Reed-Solomon, unstuffing, and the mode stream with FLG(n) for ECI and FNC1. An
/// 11 x 11 grid is read as a rune.
/// </summary>
internal static class AztecDecoder
{
    private const int Upper = 0, Lower = 1, Mixed = 2, Punct = 3, Digit = 4;

    private static readonly string[] s_tables = BuildTables();

    public static DecodedData? Decode(BarcodeModules grid)
    {
        int size = grid.Width;
        if (size != grid.Height || size % 2 == 0)
        {
            return null;
        }

        for (int orientation = 0; orientation < 8; orientation++)
        {
            BarcodeModules oriented = Orient(grid, orientation);
            DecodedData? data = size == 11 ? Rune(oriented) : DecodeUpright(oriented);
            if (data is not null)
            {
                return orientation >= 4 ? data with { Mirrored = true } : data;
            }
        }

        return null;
    }

    /// <summary>
    /// The symbol size from the mode message on a sampled core (11 x 11 compact, 15 x 15 full-range) at any orientation, or
    /// null when it does not correct; 11 for a rune.
    /// </summary>
    public static int? SymbolSize(BarcodeModules core, bool compact)
    {
        for (int orientation = 0; orientation < 8; orientation++)
        {
            BarcodeModules oriented = Orient(core, orientation);
            if (ReadMode(oriented, core.Width, compact, rune: false) is int[] mode)
            {
                int layers = compact ? (((mode[0] << 4) | mode[1]) >> 6) + 1 : (((mode[0] << 12) | (mode[1] << 8) | (mode[2] << 4) | mode[3]) >> 11) + 1;
                return AztecEncoder.Size(layers, compact);
            }

            // A rune has no data layers to confirm it, so its mode message must read without errors.
            if (compact && ReadMode(oriented, core.Width, compact: true, rune: true, exact: true) is not null)
            {
                return 11;
            }
        }

        return null;
    }

    /// <summary>Rotates the grid by quarter turns (0 to 3), then mirrors it for 4 to 7.</summary>
    private static BarcodeModules Orient(BarcodeModules grid, int orientation)
    {
        if (orientation == 0)
        {
            return grid;
        }

        int n = grid.Width;
        bool[] cells = new bool[n * n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int sx = orientation >= 4 ? n - 1 - x : x, sy = y;
                for (int r = 0; r < orientation % 4; r++)
                {
                    (sx, sy) = (sy, n - 1 - sx);
                }

                cells[(y * n) + x] = grid[sx, sy];
            }
        }

        return BarcodeModules.Wrap(n, n, cells);
    }

    private static DecodedData? Rune(BarcodeModules grid)
    {
        if (ReadMode(grid, 11, compact: true, rune: true) is not int[] words)
        {
            return null;
        }

        int value = (words[0] << 4) | words[1];
        string text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new DecodedData(BarcodeFormat.Aztec, text, [(byte)value]);
    }

    private static DecodedData? DecodeUpright(BarcodeModules grid)
    {
        // A full-range symbol has a dark ring 6 modules from the center; a compact one does not, but try both.
        int size = grid.Width, center = size / 2;
        bool full = size >= 19;
        for (int i = -6; i <= 6 && full; i++)
        {
            full = grid[center + i, center - 6] && grid[center + i, center + 6] && grid[center - 6, center + i] && grid[center + 6, center + i];
        }

        return DecodeUpright(grid, full) ?? (size >= 19 ? DecodeUpright(grid, !full) : null);
    }

    private static DecodedData? DecodeUpright(BarcodeModules grid, bool full)
    {
        int size = grid.Width;
        if (ReadMode(grid, size, !full, rune: false) is not int[] mode)
        {
            return null;
        }

        int layers, dataWords;
        if (full)
        {
            int bits = (mode[0] << 12) | (mode[1] << 8) | (mode[2] << 4) | mode[3];
            (layers, dataWords) = ((bits >> 11) + 1, (bits & 0x7FF) + 1);
        }
        else
        {
            int bits = (mode[0] << 4) | mode[1];
            (layers, dataWords) = ((bits >> 6) + 1, (bits & 0x3F) + 1);
        }

        if (AztecEncoder.Size(layers, !full) != size)
        {
            return null;
        }

        int wordSize = AztecEncoder.WordSize(layers, !full), totalBits = AztecEncoder.TotalBits(layers, !full);
        int totalWords = totalBits / wordSize, skip = totalBits % wordSize;
        if (dataWords >= totalWords)
        {
            return null;
        }

        (int X, int Y)[] positions = AztecEncoder.DataPositions(layers, !full);
        int[] words = new int[totalWords];
        for (int w = 0; w < totalWords; w++)
        {
            int word = 0;
            for (int b = 0; b < wordSize; b++)
            {
                (int x, int y) = positions[skip + (w * wordSize) + b];
                word = (word << 1) | (grid[x, y] ? 1 : 0);
            }

            words[w] = word;
        }

        int corrected = ReedSolomon.Decode(AztecEncoder.Field(wordSize), words, totalWords - dataWords);
        if (corrected < 0)
        {
            return null;
        }

        // Unstuff: a word whose first bits are all 0 or all 1 carries only those bits.
        var stream = new BitBuffer();
        int allOnes = (1 << wordSize) - 1;
        for (int w = 0; w < dataWords; w++)
        {
            int word = words[w];
            if (word == 0 || word == allOnes)
            {
                return null;
            }

            if (word == 1 || word == allOnes - 1)
            {
                stream.Append(word >> 1, wordSize - 1);
            }
            else
            {
                stream.Append(word, wordSize);
            }
        }

        return Parse(stream) is DecodedData parsed ? parsed with { ErrorsCorrected = corrected } : null;
    }

    /// <summary>Reads and corrects the mode message; returns its data words, or null when it does not correct (or, when exact, needs correcting).</summary>
    private static int[]? ReadMode(BarcodeModules grid, int size, bool compact, bool rune, bool exact = false)
    {
        (int X, int Y)[] positions = AztecEncoder.ModePositions(size, compact);
        int count = positions.Length / 4;
        int[] words = new int[count];
        for (int w = 0; w < count; w++)
        {
            int word = 0;
            for (int b = 0; b < 4; b++)
            {
                (int x, int y) = positions[(w * 4) + b];
                word = (word << 1) | (grid[x, y] ? 1 : 0);
            }

            words[w] = rune ? word ^ 0b1010 : word;
        }

        int data = compact ? 2 : 4;
        int corrected = ReedSolomon.Decode(GaloisField.Aztec4, words, count - data);
        return corrected < 0 || (exact && corrected > 0) ? null : words[..data];
    }

    /// <summary>Decodes the mode stream: latches and shifts between Upper, Lower, Mixed, Punctuation and Digit, Binary Shift, FLG(n).</summary>
    private static DecodedData? Parse(BitBuffer stream)
    {
        var text = new TextBuilder();
        int latch = Upper, shift = -1, at = 0, n = stream.Count;
        int Read(int bits)
        {
            int value = 0;
            for (int i = 0; i < bits; i++)
            {
                value = (value << 1) | (stream[at++] ? 1 : 0);
            }

            return value;
        }

        bool first = true;
        while (true)
        {
            int mode = shift >= 0 ? shift : latch;
            int bits = mode == Digit ? 4 : 5;
            if (at + bits > n)
            {
                break;
            }

            int code = Read(bits);
            shift = -1;
            switch (mode)
            {
                case Upper or Lower:
                    if (code == 0)
                    {
                        shift = Punct;
                    }
                    else if (code >= 28)
                    {
                        if (code == 28)
                        {
                            if (mode == Upper)
                            {
                                latch = Lower;
                            }
                            else
                            {
                                shift = Upper;
                            }
                        }
                        else if (code == 29)
                        {
                            latch = Mixed;
                        }
                        else if (code == 30)
                        {
                            latch = Digit;
                        }
                        else if (!BinaryShift(stream, ref at, text))
                        {
                            return null;
                        }
                        else
                        {
                            // After the bytes, the mode B/S was given in, even when that was a shift (U/S, B/S from Digit).
                            latch = mode;
                        }
                    }
                    else
                    {
                        text.Append((byte)s_tables[mode][code]);
                    }

                    break;
                case Mixed:
                    if (code == 0)
                    {
                        shift = Punct;
                    }
                    else if (code == 28)
                    {
                        latch = Lower;
                    }
                    else if (code == 29)
                    {
                        latch = Upper;
                    }
                    else if (code == 30)
                    {
                        latch = Punct;
                    }
                    else if (code == 31)
                    {
                        if (!BinaryShift(stream, ref at, text))
                        {
                            return null;
                        }

                        latch = Mixed;
                    }
                    else
                    {
                        text.Append((byte)s_tables[Mixed][code]);
                    }

                    break;
                case Punct:
                    if (code == 0)
                    {
                        // FLG(n): 0 is FNC1, 1 to 6 an ECI of that many digits.
                        if (at + 3 > n)
                        {
                            return null;
                        }

                        int digits = Read(3);
                        if (digits == 0)
                        {
                            if (first)
                            {
                                text.Gs1 = true;
                            }
                            else
                            {
                                text.Append(0x1D);
                            }
                        }
                        else if (digits == 7 || at + (4 * digits) > n)
                        {
                            return null;
                        }
                        else
                        {
                            int eci = 0;
                            for (int d = 0; d < digits; d++)
                            {
                                int value = Read(4) - 2;
                                if (value is < 0 or > 9)
                                {
                                    return null;
                                }

                                eci = (eci * 10) + value;
                            }

                            text.SetEci(eci);
                        }

                        first = false;
                        continue;
                    }

                    if (code == 31)
                    {
                        latch = Upper;
                    }
                    else
                    {
                        foreach (char c in code switch { 1 => "\r", 2 => "\r\n", 3 => ". ", 4 => ", ", 5 => ": ", _ => s_tables[Punct][code].ToString() })
                        {
                            text.Append((byte)c);
                        }
                    }

                    break;
                default:
                    if (code == 0)
                    {
                        shift = Punct;
                    }
                    else if (code == 14)
                    {
                        latch = Upper;
                    }
                    else if (code == 15)
                    {
                        shift = Upper;
                    }
                    else
                    {
                        text.Append((byte)s_tables[Digit][code]);
                    }

                    break;
            }

            if (shift < 0)
            {
                first = false;
            }
        }

        (string result, IReadOnlyList<Gs1ElementString>? elements) = text.Result();
        return new DecodedData(BarcodeFormat.Aztec, result, text.Bytes) { Eci = text.Eci, Gs1 = elements };
    }

    private static bool BinaryShift(BitBuffer stream, ref int at, TextBuilder text)
    {
        int n = stream.Count;
        int Read(ref int position, int bits)
        {
            int value = 0;
            for (int i = 0; i < bits; i++)
            {
                value = (value << 1) | (stream[position++] ? 1 : 0);
            }

            return value;
        }

        if (at + 5 > n)
        {
            return true;
        }

        int length = Read(ref at, 5);
        if (length == 0)
        {
            if (at + 11 > n)
            {
                return true;
            }

            length = Read(ref at, 11) + 31;
        }

        for (int i = 0; i < length; i++)
        {
            if (at + 8 > n)
            {
                return true;
            }

            text.Append((byte)Read(ref at, 8));
        }

        return true;
    }

    private static string[] BuildTables()
    {
        char[] upper = new char[32], lower = new char[32], mixed = new char[32], punct = new char[32], digit = new char[16];
        upper[1] = lower[1] = mixed[1] = digit[1] = ' ';
        for (int i = 0; i < 26; i++)
        {
            upper[2 + i] = (char)('A' + i);
            lower[2 + i] = (char)('a' + i);
        }

        for (int i = 1; i <= 13; i++)
        {
            mixed[i + 1] = (char)i;
        }

        for (int i = 27; i <= 31; i++)
        {
            mixed[i - 12] = (char)i;
        }

        const string mixedTail = "@\\^_`|~\x7F";
        for (int i = 0; i < mixedTail.Length; i++)
        {
            mixed[20 + i] = mixedTail[i];
        }

        const string punctuation = "!\"#$%&'()*+,-./:;<=>?[]{}";
        for (int i = 0; i < punctuation.Length; i++)
        {
            punct[6 + i] = punctuation[i];
        }

        for (int i = 0; i < 10; i++)
        {
            digit[2 + i] = (char)('0' + i);
        }

        digit[12] = ',';
        digit[13] = '.';
        return [new string(upper), new string(lower), new string(mixed), new string(punct), new string(digit)];
    }
}
