using System.Numerics;

using Egami.Barcodes.ErrorCorrection;
using Egami.Barcodes.Matrix;

namespace Egami.Barcodes.Reading;

/// <summary>
/// Reads PDF417 (ISO/IEC 15438) from scan lines across its rows: start pattern, codewords looked up from their bar and space
/// widths, the row indicators that give each line's row and the symbol's rows, columns and error level, a vote per cell over all
/// lines, Reed-Solomon with the unread cells as erasures, and text, byte and numeric compaction.
/// </summary>
internal static class Pdf417Decoder
{
    private static readonly Lazy<Dictionary<int, int>> s_lookup = new(() =>
    {
        // 16-bit pattern to (cluster index << 10) | value.
        var lookup = new Dictionary<int, int>(2787);
        ReadOnlySpan<ushort> patterns = Pdf417Tables.Patterns;
        for (int i = 0; i < patterns.Length; i++)
        {
            lookup[patterns[i]] = ((i / 929) << 10) | (i % 929);
        }

        return lookup;
    });

    /// <summary>The codewords read from one scan line: its cluster index (0 to 2) and the codewords after the start pattern.</summary>
    internal readonly record struct Line(int Cluster, int[] Codewords, float Y, float Left, float Right);

    /// <summary>Reads the codewords on one line of dark and light runs (bar first after a light run), left to right.</summary>
    internal static Line? ReadLine(ReadOnlySpan<int> runs, int firstDark, float y, ReadOnlySpan<int> positions)
    {
        // runs[i] is dark when (i - firstDark) is even.
        for (int s = firstDark; s + 8 <= runs.Length; s += 2)
        {
            if (!IsStart(runs.Slice(s, 8)))
            {
                continue;
            }

            int at = s + 8, cluster = -1;
            var codewords = new List<int>();
            while (at + 8 <= runs.Length)
            {
                if (IsStop(runs[at..Math.Min(runs.Length, at + 9)]))
                {
                    break;
                }

                if (Codeword(runs.Slice(at, 8)) is not int value)
                {
                    break;
                }

                int c = value >> 10;
                if (cluster < 0)
                {
                    cluster = c;
                }
                else if (c != cluster)
                {
                    break;
                }

                codewords.Add(value & 0x3FF);
                at += 8;
            }

            if (codewords.Count >= 2)
            {
                return new Line(cluster, [.. codewords], y, positions[s], positions[Math.Min(at, positions.Length - 1)]);
            }
        }

        return null;
    }

    private static bool IsStart(ReadOnlySpan<int> runs) => Matches(runs, [8, 1, 1, 1, 1, 1, 1, 3]);

    private static bool IsStop(ReadOnlySpan<int> runs) => runs.Length >= 9 && Matches(runs[..9], [7, 1, 1, 3, 1, 1, 1, 2, 1]);

    /// <summary>Returns whether runs match a pattern of module widths within a third of a module each.</summary>
    private static bool Matches(ReadOnlySpan<int> runs, ReadOnlySpan<int> pattern)
    {
        int total = 0, modules = 0;
        for (int i = 0; i < pattern.Length; i++)
        {
            total += runs[i];
            modules += pattern[i];
        }

        float unit = (float)total / modules;
        if (unit < 1)
        {
            return false;
        }

        for (int i = 0; i < pattern.Length; i++)
        {
            if (MathF.Abs(runs[i] - (pattern[i] * unit)) > Math.Max(unit * 0.6f, 1.2f) + (pattern[i] * unit * 0.15f))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Rounds 8 run widths to module widths summing to 17 and looks the pattern up; returns the cluster index times 1024 plus the value.</summary>
    private static int? Codeword(ReadOnlySpan<int> runs)
    {
        int total = 0;
        foreach (int r in runs)
        {
            total += r;
        }

        if (total < 17 * 0.8f)
        {
            return null;
        }

        Span<int> widths = stackalloc int[8];
        Span<float> error = stackalloc float[8];
        int sum = 0;
        for (int i = 0; i < 8; i++)
        {
            float exact = runs[i] * 17f / total;
            widths[i] = Math.Clamp((int)MathF.Round(exact), 1, 6);
            error[i] = exact - widths[i];
            sum += widths[i];
        }

        while (sum != 17)
        {
            int pick = -1;
            for (int i = 0; i < 8; i++)
            {
                bool can = sum < 17 ? widths[i] < 6 : widths[i] > 1;
                if (can && (pick < 0 || (sum < 17 ? error[i] > error[pick] : error[i] < error[pick])))
                {
                    pick = i;
                }
            }

            if (pick < 0)
            {
                return null;
            }

            int step = sum < 17 ? 1 : -1;
            widths[pick] += step;
            error[pick] -= step;
            sum += step;
        }

        int pattern = 0;
        for (int i = 0; i < 8; i++)
        {
            for (int k = 0; k < widths[i]; k++)
            {
                pattern = (pattern << 1) | (i % 2 == 0 ? 1 : 0);
            }
        }

        return s_lookup.Value.TryGetValue(pattern >> 1, out int found) ? found : null;
    }

    /// <summary>Puts lines together into a symbol and decodes it.</summary>
    internal static DecodedData? Decode(IReadOnlyList<Line> lines, out Vector2[] corners)
    {
        corners = [];
        if (lines.Count == 0)
        {
            return null;
        }

        // Row count, columns and error level from the left row indicators: votes per field.
        var rowsVotes = new Dictionary<int, int>();
        var columnVotes = new Dictionary<int, int>();
        var levelVotes = new Dictionary<int, int>();
        foreach (Line line in lines)
        {
            int ri = line.Codewords[0], low = ri % 30;
            switch (line.Cluster)
            {
                case 0:
                    Vote(rowsVotes, (low * 3) + 1, 0);
                    break;
                case 1:
                    Vote(levelVotes, low / 3, 0);
                    Vote(rowsVotes, low % 3, 1);
                    break;
                default:
                    Vote(columnVotes, low + 1, 0);
                    break;
            }
        }

        if (columnVotes.Count == 0 || levelVotes.Count == 0)
        {
            return null;
        }

        int columns = Best(columnVotes), level = Best(levelVotes);
        int rows = RowCount(rowsVotes);
        if (columns is < 1 or > 30 || level is < 0 or > 8 || rows is < 3 or > 90)
        {
            return null;
        }

        // Each line's row: 3 times the indicator's group plus its cluster; vote per cell.
        var cells = new Dictionary<int, int>[rows * columns];
        float top = float.MaxValue, bottom = float.MinValue, left = float.MaxValue, right = float.MinValue;
        foreach (Line line in lines)
        {
            int row = (3 * (line.Codewords[0] / 30)) + line.Cluster;
            if (row >= rows)
            {
                continue;
            }

            for (int c = 0; c < columns && c + 1 < line.Codewords.Length; c++)
            {
                (cells[(row * columns) + c] ??= [])[line.Codewords[c + 1]] = cells[(row * columns) + c].GetValueOrDefault(line.Codewords[c + 1]) + 1;
            }

            top = MathF.Min(top, line.Y);
            bottom = MathF.Max(bottom, line.Y);
            left = MathF.Min(left, line.Left);
            right = MathF.Max(right, line.Right);
        }

        int[] codewords = new int[rows * columns];
        var erasures = new List<int>();
        for (int i = 0; i < codewords.Length; i++)
        {
            if (cells[i] is null)
            {
                erasures.Add(i);
            }
            else
            {
                codewords[i] = cells[i].MaxBy(kv => kv.Value).Key;
            }
        }

        int checkCount = 2 << level;
        if (checkCount >= codewords.Length || erasures.Count > checkCount)
        {
            return null;
        }

        int corrected = ReedSolomon.Decode(GaloisField.Pdf417, codewords, checkCount, [.. erasures]);
        if (corrected < 0)
        {
            return null;
        }

        int dataCount = codewords[0];
        if (dataCount < 1 || dataCount > codewords.Length - checkCount)
        {
            return null;
        }

        corners = [new(left, top), new(right, top), new(right, bottom), new(left, bottom)];
        return Parse(codewords.AsSpan(1, dataCount - 1)) is DecodedData parsed ? parsed with { ErrorsCorrected = corrected } : null;
    }

    private static void Vote(Dictionary<int, int> votes, int key, int kind) => votes[(kind << 16) | key] = votes.GetValueOrDefault((kind << 16) | key) + 1;

    private static int Best(Dictionary<int, int> votes) => votes.MaxBy(kv => kv.Value).Key & 0xFFFF;

    /// <summary>The row count from (rows - 1) / 3 (cluster 0) and (rows - 1) % 3 (cluster 3) votes.</summary>
    private static int RowCount(Dictionary<int, int> votes)
    {
        int third = -1, remainder = -1, bestThird = 0, bestRemainder = 0;
        foreach ((int key, int count) in votes)
        {
            if (key >> 16 == 0 && count > bestThird)
            {
                (third, bestThird) = ((key & 0xFFFF) - 1, count);
            }
            else if (key >> 16 == 1 && count > bestRemainder)
            {
                (remainder, bestRemainder) = (key & 0xFFFF, count);
            }
        }

        if (third < 0)
        {
            return -1;
        }

        return third + Math.Max(remainder, 0) + 1;
    }

    private const int TextMode = 900, ByteMode = 901, NumericMode = 902, ByteShift = 913, ByteMode6 = 924, Eci = 927;
    private const string Mixed = "0123456789&\r\t,:#-.$/+%*=^";
    private const string Punctuation = ";<>@[\\]_`~!\r\t,:\n-.$/\"|*()?{}'";

    /// <summary>Decodes the data codewords after the length descriptor.</summary>
    internal static DecodedData? Parse(ReadOnlySpan<int> codewords)
    {
        var text = new TextBuilder();
        int mode = TextMode, i = 0, subMode = 0;
        while (i < codewords.Length)
        {
            int cw = codewords[i];
            if (cw >= 900)
            {
                i++;
                switch (cw)
                {
                    case TextMode:
                        mode = TextMode;
                        subMode = 0;
                        continue;
                    case ByteMode or ByteMode6:
                        i = Bytes(codewords, i, cw == ByteMode6, text);
                        mode = ByteMode;
                        continue;
                    case NumericMode:
                        if (Numeric(codewords, ref i, text) is false)
                        {
                            return null;
                        }

                        mode = NumericMode;
                        continue;
                    case ByteShift:
                        if (i < codewords.Length)
                        {
                            text.Append((byte)codewords[i++]);
                        }

                        continue;
                    case Eci:
                        if (i < codewords.Length)
                        {
                            text.SetEci(codewords[i++]);
                        }

                        continue;
                    case 925:
                        i++;
                        continue;
                    case 926:
                        i += 2;
                        continue;
                    case 928 or 922 or 923:
                        i = codewords.Length;
                        continue;
                    default:
                        return null;
                }
            }

            if (mode != TextMode)
            {
                return null;
            }

            // Text compaction runs until the next mode codeword.
            int end = i;
            while (end < codewords.Length && codewords[end] < 900)
            {
                end++;
            }

            subMode = Text(codewords[i..end], subMode, text);
            i = end;
        }

        (string result, IReadOnlyList<Gs1ElementString>? elements) = text.Result();
        return new DecodedData(BarcodeFormat.Pdf417, result, text.Bytes) { Eci = text.Eci, Gs1 = elements };
    }

    private static int Bytes(ReadOnlySpan<int> codewords, int i, bool allGroups, TextBuilder text)
    {
        int end = i;
        while (end < codewords.Length && codewords[end] < 900)
        {
            end++;
        }

        // Groups of 5 codewords hold 6 bytes; with 901 the codewords left over are a byte each.
        Span<byte> bytes = stackalloc byte[6];
        while (i + 5 <= end && (allGroups || i + 5 < end))
        {
            long value = 0;
            for (int k = 0; k < 5; k++)
            {
                value = (value * 900) + codewords[i + k];
            }

            for (int k = 5; k >= 0; k--)
            {
                bytes[k] = (byte)(value & 0xFF);
                value >>= 8;
            }

            text.Append(bytes);
            i += 5;
        }

        for (; i < end; i++)
        {
            text.Append((byte)codewords[i]);
        }

        return end;
    }

    private static bool Numeric(ReadOnlySpan<int> codewords, ref int i, TextBuilder text)
    {
        while (i < codewords.Length && codewords[i] < 900)
        {
            int count = 0;
            BigInteger value = BigInteger.Zero;
            while (count < 15 && i < codewords.Length && codewords[i] < 900)
            {
                value = (value * 900) + codewords[i++];
                count++;
            }

            string digits = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (digits.Length < 2 || digits[0] != '1')
            {
                return false;
            }

            text.AppendText(digits[1..]);
        }

        return true;
    }

    /// <summary>Text compaction: two values per codeword in the Alpha, Lower, Mixed and Punctuation sub-modes.</summary>
    private static int Text(ReadOnlySpan<int> run, int subMode, TextBuilder text)
    {
        const int Alpha = 0, Lower = 1, MixedMode = 2, Punct = 3;
        int shift = -1;
        foreach (int cw in run)
        {
            foreach (int v in (ReadOnlySpan<int>)[cw / 30, cw % 30])
            {
                int mode = shift >= 0 ? shift : subMode;
                shift = -1;
                switch (mode)
                {
                    case Alpha or Lower:
                        if (v < 26)
                        {
                            text.Append((byte)((mode == Alpha ? 'A' : 'a') + v));
                        }
                        else if (v == 26)
                        {
                            text.Append((byte)' ');
                        }
                        else if (v == 27)
                        {
                            if (mode == Alpha)
                            {
                                subMode = Lower;
                            }
                            else
                            {
                                shift = Alpha;
                            }
                        }
                        else if (v == 28)
                        {
                            subMode = MixedMode;
                        }
                        else
                        {
                            shift = Punct;
                        }

                        break;
                    case MixedMode:
                        if (v < 25)
                        {
                            text.Append((byte)Mixed[v]);
                        }
                        else if (v == 25)
                        {
                            subMode = Punct;
                        }
                        else if (v == 26)
                        {
                            text.Append((byte)' ');
                        }
                        else if (v == 27)
                        {
                            subMode = Lower;
                        }
                        else if (v == 28)
                        {
                            subMode = Alpha;
                        }
                        else
                        {
                            shift = Punct;
                        }

                        break;
                    default:
                        if (v < 29)
                        {
                            text.Append((byte)Punctuation[v]);
                        }
                        else
                        {
                            subMode = Alpha;
                        }

                        break;
                }
            }
        }

        return subMode;
    }
}
