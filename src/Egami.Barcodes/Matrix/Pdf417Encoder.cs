using System.Numerics;

using Egami.Barcodes.ErrorCorrection;

namespace Egami.Barcodes.Matrix;

/// <summary>
/// PDF417 (ISO/IEC 15438): text, byte and numeric compaction chosen run by run as Annex P suggests, a length descriptor and
/// padding, Reed-Solomon error correction over GF(929) at a level from the standard's recommended minimum, columns picked for a
/// symbol about three times as wide as tall, and rows of start pattern, row indicators, data and stop pattern.
/// </summary>
internal static class Pdf417Encoder
{
    private const int LatchText = 900, LatchByte = 901, LatchNumeric = 902, ShiftByte = 913, LatchByte6 = 924, Eci = 927;

    /// <summary>How tall each row is, in modules.</summary>
    public const int RowHeight = 3;

    /// <summary>The symbol: rows, data columns, error level and modules.</summary>
    public readonly record struct Result(int Rows, int Columns, int Level, BarcodeModules Modules);

    // Text compaction sub-mode values. Alpha and Lower hold letters and space; Mixed and Punctuation the rest.
    private const string MixedSet = "0123456789&\r\t,:#-.$/+%*=^";
    private const string PunctuationSet = ";<>@[\\]_`~!\r\t,:\n-.$/\"|*()?{}'";
    private const int Alpha = 0, Lower = 1, Mixed = 2, Punctuation = 3;

    /// <summary>Encodes the bytes; returns <see langword="null"/> when they do not fit.</summary>
    public static Result? Encode(byte[] data, int eci, int? columns, int? level, bool compact)
    {
        List<int> codewords = HighLevel(data, eci);
        int dataCount = codewords.Count + 1;
        int ecLevel = level ?? (dataCount <= 40 ? 2 : dataCount <= 160 ? 3 : dataCount <= 320 ? 4 : 5);
        int ecCount = 2 << ecLevel;
        int total = dataCount + ecCount;
        if (total > 928)
        {
            return null;
        }

        (int rows, int cols)? shape = Shape(total, columns, compact);
        if (shape is not (int rows, int cols))
        {
            return null;
        }

        // Length descriptor, data, padding to fill the rows, then the check codewords.
        int[] all = new int[rows * cols];
        int padded = (rows * cols) - ecCount;
        all[0] = padded;
        codewords.CopyTo(all, 1);
        all.AsSpan(dataCount, padded - dataCount).Fill(LatchText);
        ReedSolomon.Encode(GaloisField.Pdf417, all.AsSpan(0, padded), all.AsSpan(padded));
        return new Result(rows, cols, ecLevel, Matrix(all, rows, cols, ecLevel, compact));
    }

    /// <summary>Picks the columns (1 to 30) and rows (3 to 90) for about 3:1 width to height, or the rows for given columns.</summary>
    private static (int Rows, int Columns)? Shape(int total, int? columns, bool compact)
    {
        (int, int)? best = null;
        double bestScore = double.MaxValue;
        for (int c = columns ?? 1; c <= (columns ?? 30); c++)
        {
            int r = Math.Max(3, (total + c - 1) / c);
            if (r > 90)
            {
                continue;
            }

            double width = ((c + (compact ? 2 : 4)) * 17) + 1, height = r * RowHeight;
            double score = Math.Abs(Math.Log(width / height / 3));
            if (score < bestScore - 1e-9)
            {
                (bestScore, best) = (score, (r, c));
            }
        }

        return best;
    }

    /// <summary>Builds the module rows: start, left row indicator, data, right row indicator and stop (or one bar when compact).</summary>
    private static BarcodeModules Matrix(int[] codewords, int rows, int cols, int level, bool compact)
    {
        int width = ((cols + (compact ? 2 : 4)) * 17) + 1;
        bool[] dark = new bool[width * rows * RowHeight];
        for (int row = 0; row < rows; row++)
        {
            int cluster = row % 3, start = row * RowHeight * width, x = start;
            x = Put(dark, x, 0b11111111010101000, 17);
            int block = 30 * (row / 3);
            int left = cluster switch { 0 => block + ((rows - 1) / 3), 1 => block + (level * 3) + ((rows - 1) % 3), _ => block + cols - 1 };
            int right = cluster switch { 0 => block + cols - 1, 1 => block + ((rows - 1) / 3), _ => block + (level * 3) + ((rows - 1) % 3) };
            x = Codeword(dark, x, cluster, left);
            for (int c = 0; c < cols; c++)
            {
                x = Codeword(dark, x, cluster, codewords[(row * cols) + c]);
            }

            if (compact)
            {
                Put(dark, x, 1, 1);
            }
            else
            {
                x = Codeword(dark, x, cluster, right);
                Put(dark, x, 0b111111101000101001, 18);
            }

            for (int copy = 1; copy < RowHeight; copy++)
            {
                Array.Copy(dark, start, dark, start + (copy * width), width);
            }
        }

        return BarcodeModules.Wrap(width, rows * RowHeight, dark);
    }

    /// <summary>Writes the low bits of a value, highest first, and returns the next position.</summary>
    private static int Put(bool[] dark, int x, int value, int bits)
    {
        for (int i = bits - 1; i >= 0; i--)
        {
            dark[x++] = ((value >> i) & 1) != 0;
        }

        return x;
    }

    /// <summary>Writes a codeword's 17 modules in a cluster (0, 1 or 2 for clusters 0, 3 and 6).</summary>
    private static int Codeword(bool[] dark, int x, int cluster, int value) => Put(dark, x, Pdf417Tables.Patterns[(cluster * 929) + value] << 1, 17);

    /// <summary>Chooses numeric, text or byte compaction run by run (ISO/IEC 15438 Annex P) and returns the data codewords.</summary>
    internal static List<int> HighLevel(byte[] data, int eci)
    {
        var codewords = new List<int>();
        if (eci > 0)
        {
            codewords.Add(Eci);
            codewords.Add(eci);
        }

        int mode = LatchText, subMode = Alpha, p = 0;
        while (p < data.Length)
        {
            int digits = CountDigits(data, p);
            if (digits >= 13)
            {
                codewords.Add(LatchNumeric);
                mode = LatchNumeric;
                subMode = Alpha;
                Numeric(data.AsSpan(p, digits), codewords);
                p += digits;
                continue;
            }

            int text = CountText(data, p);
            if (text >= 5 || digits == data.Length - p)
            {
                if (mode != LatchText)
                {
                    codewords.Add(LatchText);
                    mode = LatchText;
                    subMode = Alpha;
                }

                subMode = Text(data.AsSpan(p, Math.Max(text, 1)), subMode, codewords);
                p += Math.Max(text, 1);
                continue;
            }

            int bytes = Math.Max(1, CountBytes(data, p));
            if (bytes == 1 && mode == LatchText)
            {
                codewords.Add(ShiftByte);
                codewords.Add(data[p]);
            }
            else
            {
                Bytes(data.AsSpan(p, bytes), codewords);
                mode = LatchByte;
                subMode = Alpha;
            }

            p += bytes;
        }

        return codewords;
    }

    private static bool IsText(byte b) => b is (byte)'\t' or (byte)'\n' or (byte)'\r' or >= 32 and <= 126;

    private static int CountDigits(byte[] data, int p)
    {
        int count = 0;
        while (p + count < data.Length && data[p + count] is >= (byte)'0' and <= (byte)'9')
        {
            count++;
        }

        return count;
    }

    /// <summary>Text characters from p, stopping before a run of 13 digits.</summary>
    private static int CountText(byte[] data, int p)
    {
        int start = p;
        while (p < data.Length && IsText(data[p]))
        {
            int digits = CountDigits(data, p);
            if (digits >= 13)
            {
                break;
            }

            p += Math.Max(digits, 1);
        }

        return Math.Min(p, data.Length) - start;
    }

    /// <summary>Bytes from p, stopping before 13 digits or 5 text characters.</summary>
    private static int CountBytes(byte[] data, int p)
    {
        int start = p;
        while (p < data.Length)
        {
            if (CountDigits(data, p) >= 13 || CountText(data, p) >= 5)
            {
                break;
            }

            p++;
        }

        return p - start;
    }

    private static void Numeric(ReadOnlySpan<byte> digits, List<int> codewords)
    {
        Span<int> group = stackalloc int[15];
        for (int at = 0; at < digits.Length; at += 44)
        {
            int length = Math.Min(44, digits.Length - at);
            BigInteger value = BigInteger.One;
            for (int i = 0; i < length; i++)
            {
                value = (value * 10) + (digits[at + i] - '0');
            }

            int count = 0;
            while (value > 0)
            {
                group[count++] = (int)(value % 900);
                value /= 900;
            }

            for (int i = count - 1; i >= 0; i--)
            {
                codewords.Add(group[i]);
            }
        }
    }

    internal static void Bytes(ReadOnlySpan<byte> bytes, List<int> codewords)
    {
        codewords.Add(bytes.Length % 6 == 0 ? LatchByte6 : LatchByte);
        int at = 0;
        Span<int> group = stackalloc int[5];
        for (; at + 6 <= bytes.Length; at += 6)
        {
            long value = 0;
            for (int i = 0; i < 6; i++)
            {
                value = (value << 8) | bytes[at + i];
            }

            for (int i = 4; i >= 0; i--)
            {
                group[i] = (int)(value % 900);
                value /= 900;
            }

            foreach (int c in group)
            {
                codewords.Add(c);
            }
        }

        for (; at < bytes.Length; at++)
        {
            codewords.Add(bytes[at]);
        }
    }

    /// <summary>Text compaction: sub-mode values in pairs, with latches and shifts between Alpha, Lower, Mixed and Punctuation.</summary>
    private static int Text(ReadOnlySpan<byte> text, int subMode, List<int> codewords)
    {
        var values = new List<int>();
        for (int i = 0; i < text.Length; i++)
        {
            char c = (char)text[i];
            bool upper = c is >= 'A' and <= 'Z' or ' ', lower = c is >= 'a' and <= 'z' or ' ';
            int mixed = c == ' ' ? 26 : MixedSet.IndexOf(c, StringComparison.Ordinal), punctuation = PunctuationSet.IndexOf(c, StringComparison.Ordinal);
            switch (subMode)
            {
                case Alpha:
                    if (upper)
                    {
                        values.Add(c == ' ' ? 26 : c - 'A');
                    }
                    else if (lower)
                    {
                        values.Add(27);
                        subMode = Lower;
                        i--;
                    }
                    else if (mixed >= 0)
                    {
                        values.Add(28);
                        subMode = Mixed;
                        i--;
                    }
                    else
                    {
                        values.Add(29);
                        values.Add(punctuation);
                    }

                    break;
                case Lower:
                    if (lower)
                    {
                        values.Add(c == ' ' ? 26 : c - 'a');
                    }
                    else if (upper)
                    {
                        values.Add(27);
                        values.Add(c - 'A');
                    }
                    else if (mixed >= 0)
                    {
                        values.Add(28);
                        subMode = Mixed;
                        i--;
                    }
                    else
                    {
                        values.Add(29);
                        values.Add(punctuation);
                    }

                    break;
                case Mixed:
                    if (mixed >= 0)
                    {
                        values.Add(mixed);
                    }
                    else if (upper)
                    {
                        values.Add(28);
                        subMode = Alpha;
                        i--;
                    }
                    else if (lower)
                    {
                        values.Add(27);
                        subMode = Lower;
                        i--;
                    }
                    else if (i + 1 < text.Length && PunctuationSet.Contains((char)text[i + 1], StringComparison.Ordinal))
                    {
                        values.Add(25);
                        subMode = Punctuation;
                        i--;
                    }
                    else
                    {
                        values.Add(29);
                        values.Add(punctuation);
                    }

                    break;
                default:
                    if (punctuation >= 0)
                    {
                        values.Add(punctuation);
                    }
                    else
                    {
                        values.Add(29);
                        subMode = Alpha;
                        i--;
                    }

                    break;
            }
        }

        // An odd count is padded with 29: a shift to Punctuation that nothing follows, or in Punctuation a latch to Alpha.
        if (values.Count % 2 == 1)
        {
            values.Add(29);
            if (subMode == Punctuation)
            {
                subMode = Alpha;
            }
        }

        for (int i = 0; i < values.Count; i += 2)
        {
            codewords.Add((values[i] * 30) + values[i + 1]);
        }

        return subMode;
    }
}
