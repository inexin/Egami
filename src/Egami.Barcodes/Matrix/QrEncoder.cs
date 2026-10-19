using Egami.Barcodes.ErrorCorrection;

namespace Egami.Barcodes.Matrix;

/// <summary>
/// QR Code (ISO/IEC 18004): splits the data into numeric, alphanumeric, byte and Kanji segments with the fewest bits for each
/// range of versions, picks the smallest version at the lowest allowed level, raises the level while the version holds, adds the
/// Reed-Solomon blocks and builds the symbol with the mask of lowest penalty.
/// </summary>
internal static class QrEncoder
{
    private const int Numeric = 0, Alphanumeric = 1, Byte = 2, Kanji = 3;

    /// <summary>The 45 characters of alphanumeric mode, in order of their values.</summary>
    private const string AlphanumericSet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    /// <summary>The data to encode: bytes, whether Kanji mode may take double-byte Shift JIS characters, an ECI, GS1.</summary>
    public readonly record struct Input(byte[] Bytes, bool Kanji, int Eci, bool Gs1);

    /// <summary>The symbol's version, level (0 L, 1 M, 2 Q, 3 H), mask and modules.</summary>
    public readonly record struct Result(int Version, int Level, int Mask, BarcodeModules Modules);

    /// <summary>Encodes the input.</summary>
    /// <param name="input">The data.</param>
    /// <param name="minimumLevel">The lowest level allowed.</param>
    /// <param name="raiseLevel">Whether to raise the level while the symbol stays the same version.</param>
    /// <param name="fixedVersion">A version to use, or 0 for the smallest that fits.</param>
    /// <param name="fixedMask">A mask to use, or -1 for the one of lowest penalty.</param>
    /// <returns>The symbol, or <see langword="null"/> when the data does not fit.</returns>
    public static Result? Encode(Input input, int minimumLevel, bool raiseLevel, int fixedVersion, int fixedMask)
    {
        // The bits for each range of versions, whose character count fields differ in length.
        BitBuffer?[] streams = new BitBuffer?[3];
        int version = 0;
        for (int v = fixedVersion == 0 ? 1 : fixedVersion; v <= (fixedVersion == 0 ? 40 : fixedVersion); v++)
        {
            int range = Range(v);
            streams[range] ??= Bits(input, range);
            if (streams[range]!.Count <= QrTables.DataCodewords(v, minimumLevel) * 8)
            {
                version = v;
                break;
            }
        }

        if (version == 0)
        {
            return null;
        }

        BitBuffer bits = streams[Range(version)]!;
        int level = minimumLevel;
        while (raiseLevel && level < 3 && bits.Count <= QrTables.DataCodewords(version, level + 1) * 8)
        {
            level++;
        }

        byte[] codewords = Codewords(bits, version, level);
        (bool[] modules, int mask) = QrMatrix.Build(version, level, codewords, fixedMask);
        int size = 17 + (4 * version);
        return new Result(version, level, mask, BarcodeModules.Wrap(size, size, modules));
    }

    /// <summary>Returns 0 for versions 1 to 9, 1 for 10 to 26 and 2 for 27 to 40.</summary>
    private static int Range(int version) => version <= 9 ? 0 : version <= 26 ? 1 : 2;

    private static int CountBits(int mode, int range) => mode switch
    {
        Numeric => 10 + (2 * range),
        Alphanumeric => 9 + (2 * range),
        Byte => range == 0 ? 8 : 16,
        _ => 8 + (2 * range),
    };

    /// <summary>Builds the data bits with the fewest segments' bits for a range of versions, without terminator or padding.</summary>
    internal static BitBuffer Bits(Input input, int range)
    {
        var bits = new BitBuffer();
        if (input.Eci > 0)
        {
            bits.Append(0b0111, 4);
            if (input.Eci < 128)
            {
                bits.Append(input.Eci, 8);
            }
            else if (input.Eci < 16384)
            {
                bits.Append(0b10 << 14 | input.Eci, 16);
            }
            else
            {
                bits.Append(0b110 << 21 | input.Eci, 24);
            }
        }

        if (input.Gs1)
        {
            bits.Append(0b0101, 4);
        }

        foreach ((int mode, int start, int end) in Segments(input, range))
        {
            AppendSegment(bits, input, mode, start, end, range);
        }

        return bits;
    }

    /// <summary>
    /// Splits the bytes into segments by dynamic programming over byte positions, in sixths of a bit so that numeric (10 bits per
    /// 3 digits) and alphanumeric (11 bits per 2 characters) cost whole units, with a segment header for each change of mode.
    /// </summary>
    private static List<(int Mode, int Start, int End)> Segments(Input input, int range)
    {
        byte[] data = input.Bytes;
        int n = data.Length;
        const int Infinite = int.MaxValue / 2;
        int[,] cost = new int[n + 1, 4];
        int[,] from = new int[n + 1, 4];
        for (int i = 0; i <= n; i++)
        {
            for (int m = 0; m < 4; m++)
            {
                cost[i, m] = Infinite;
            }
        }

        for (int i = 0; i < n; i++)
        {
            int best = Infinite, bestMode = -1;
            if (i == 0)
            {
                best = 0;
            }
            else
            {
                for (int m = 0; m < 4; m++)
                {
                    if (cost[i, m] < best)
                    {
                        (best, bestMode) = (cost[i, m], m);
                    }
                }
            }

            for (int m = 0; m < 4; m++)
            {
                (int unit, int step) = Unit(input, m, i);
                if (unit == 0)
                {
                    continue;
                }

                // Continue the segment in this mode, or start a new one here (the header in sixths of a bit).
                int keep = cost[i, m] + unit;
                int fresh = best + ((4 + CountBits(m, range)) * 6) + unit;
                int target = i + step;
                int value = keep <= fresh ? keep : fresh;
                if (value < cost[target, m])
                {
                    cost[target, m] = value;
                    from[target, m] = keep <= fresh ? (m << 4) | 1 : (bestMode + 1) << 4;
                }
            }
        }

        int mode = 0;
        for (int m = 1; m < 4; m++)
        {
            if (cost[n, m] < cost[n, mode])
            {
                mode = m;
            }
        }

        // Walk back, closing a segment wherever one was started.
        var segments = new List<(int, int, int)>();
        int end = n, position = n;
        while (position > 0)
        {
            int step = mode == Kanji ? 2 : 1;
            int link = from[position, mode];
            position -= step;
            if ((link & 1) == 0)
            {
                segments.Add((mode, position, end));
                end = position;
                mode = (link >> 4) - 1;
            }
        }

        segments.Reverse();
        return segments;
    }

    /// <summary>The cost in sixths of a bit of the unit at a position in a mode, and how many bytes it takes; 0 when the mode cannot.</summary>
    private static (int Cost, int Step) Unit(Input input, int mode, int i)
    {
        byte b = input.Bytes[i];
        switch (mode)
        {
            case Numeric:
                return b is >= (byte)'0' and <= (byte)'9' ? (20, 1) : (0, 0);
            case Alphanumeric:
                if (input.Gs1 && b == 0x1D)
                {
                    return (33, 1);
                }

                return AlphanumericSet.Contains((char)b, StringComparison.Ordinal) ? (input.Gs1 && b == '%' ? 66 : 33, 1) : (0, 0);
            case Byte:
                return (48, 1);
            default:
                return input.Kanji && i + 1 < input.Bytes.Length && IsKanji(b, input.Bytes[i + 1]) ? (78, 2) : (0, 0);
        }
    }

    /// <summary>Returns whether two bytes are a Shift JIS character Kanji mode holds: 0x8140 to 0x9FFC or 0xE040 to 0xEBBF.</summary>
    private static bool IsKanji(byte first, byte second)
    {
        int code = (first << 8) | second;
        return second is >= 0x40 and not 0x7F and <= 0xFC && (code is >= 0x8140 and <= 0x9FFC or >= 0xE040 and <= 0xEBBF);
    }

    private static void AppendSegment(BitBuffer bits, Input input, int mode, int start, int end, int range)
    {
        int countBits = CountBits(mode, range), maxCount = (1 << countBits) - 1;
        switch (mode)
        {
            case Numeric:
                for (int at = start; at < end; at += maxCount)
                {
                    int count = Math.Min(maxCount, end - at);
                    bits.Append(0b0001, 4);
                    bits.Append(count, countBits);
                    for (int i = 0; i < count; i += 3)
                    {
                        int digits = Math.Min(3, count - i), value = 0;
                        for (int k = 0; k < digits; k++)
                        {
                            value = (value * 10) + (input.Bytes[at + i + k] - '0');
                        }

                        bits.Append(value, (digits * 3) + 1);
                    }
                }

                break;
            case Alphanumeric:
                // GS1: the FNC1 separator (GS) is written as %, and a % itself as %%.
                var chars = new List<int>();
                for (int i = start; i < end; i++)
                {
                    byte b = input.Bytes[i];
                    if (input.Gs1 && b == 0x1D)
                    {
                        chars.Add(38);
                    }
                    else
                    {
                        int value = AlphanumericSet.IndexOf((char)b, StringComparison.Ordinal);
                        chars.Add(value);
                        if (input.Gs1 && b == '%')
                        {
                            chars.Add(value);
                        }
                    }
                }

                for (int at = 0; at < chars.Count; at += maxCount)
                {
                    int count = Math.Min(maxCount, chars.Count - at);
                    bits.Append(0b0010, 4);
                    bits.Append(count, countBits);
                    for (int i = 0; i + 1 < count; i += 2)
                    {
                        bits.Append((chars[at + i] * 45) + chars[at + i + 1], 11);
                    }

                    if (count % 2 == 1)
                    {
                        bits.Append(chars[at + count - 1], 6);
                    }
                }

                break;
            case Byte:
                for (int at = start; at < end; at += maxCount)
                {
                    int count = Math.Min(maxCount, end - at);
                    bits.Append(0b0100, 4);
                    bits.Append(count, countBits);
                    for (int i = 0; i < count; i++)
                    {
                        bits.Append(input.Bytes[at + i], 8);
                    }
                }

                break;
            default:
                for (int at = start; at < end; at += maxCount * 2)
                {
                    int count = Math.Min(maxCount, (end - at) / 2);
                    bits.Append(0b1000, 4);
                    bits.Append(count, countBits);
                    for (int i = 0; i < count; i++)
                    {
                        int code = (input.Bytes[at + (2 * i)] << 8) | input.Bytes[at + (2 * i) + 1];
                        code -= code <= 0x9FFC ? 0x8140 : 0xC140;
                        bits.Append(((code >> 8) * 0xC0) + (code & 0xFF), 13);
                    }
                }

                break;
        }
    }

    /// <summary>Adds the terminator and padding, splits into blocks, adds Reed-Solomon and interleaves.</summary>
    internal static byte[] Codewords(BitBuffer bits, int version, int level)
    {
        int dataCount = QrTables.DataCodewords(version, level), capacity = dataCount * 8;
        bits.Append(0, Math.Min(4, capacity - bits.Count));
        bits.Append(0, (8 - (bits.Count % 8)) % 8);
        byte[] data = new byte[dataCount];
        bits.ToBytes().CopyTo(data, 0);
        for (int i = (bits.Count / 8), pad = 0; i < dataCount; i++, pad++)
        {
            data[i] = (byte)(pad % 2 == 0 ? 0xEC : 0x11);
        }

        int total = QrTables.TotalCodewords[version - 1], blocks = QrTables.Blocks(version, level);
        int checkCount = (total - dataCount) / blocks, shortBlocks = blocks - (total % blocks);
        int shortData = (total / blocks) - checkCount;
        int[][] blockData = new int[blocks][], blockCheck = new int[blocks][];
        for (int b = 0, at = 0; b < blocks; b++)
        {
            int length = shortData + (b < shortBlocks ? 0 : 1);
            blockData[b] = [.. data.AsSpan(at, length).ToArray().Select(x => (int)x)];
            blockCheck[b] = new int[checkCount];
            ReedSolomon.Encode(GaloisField.Qr, blockData[b], blockCheck[b]);
            at += length;
        }

        byte[] result = new byte[total];
        int k = 0;
        for (int i = 0; i <= shortData; i++)
        {
            for (int b = 0; b < blocks; b++)
            {
                if (i < blockData[b].Length)
                {
                    result[k++] = (byte)blockData[b][i];
                }
            }
        }

        for (int i = 0; i < checkCount; i++)
        {
            for (int b = 0; b < blocks; b++)
            {
                result[k++] = (byte)blockCheck[b][i];
            }
        }

        return result;
    }
}
