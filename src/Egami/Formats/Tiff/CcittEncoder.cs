using System.Buffers.Binary;
using System.Numerics;

namespace Egami.Formats.Tiff;

/// <summary>
/// CCITT fax coding for the writer: Group 3 one-dimensional (ITU-T T.4 modified Huffman rows, each after an EOL) and Group 4
/// (ITU-T T.6, every row coded against the one above, the first against an imaginary white row, then EOFB). Rows come in packed
/// most significant bit first, 1 bits black, as WhiteIsZero stores them.
/// </summary>
internal static class CcittEncoder
{
    // T.4 tables 2 and 3: (code, length) for terminating runs 0 to 63, then makeup runs 64 to 1728 in steps of 64.
    private static readonly (int Code, int Length)[] WhiteTerminating = Codes(
        "00110101 000111 0111 1000 1011 1100 1110 1111 10011 10100 00111 01000 001000 000011 110100 110101 101010 101011 0100111 0001100 0001000 0010111 0000011 0000100 0101000 0101011 0010011 0100100 0011000 00000010 00000011 00011010 00011011 00010010 00010011 00010100 00010101 00010110 00010111 00101000 00101001 00101010 00101011 00101100 00101101 00000100 00000101 00001010 00001011 01010010 01010011 01010100 01010101 00100100 00100101 01011000 01011001 01011010 01011011 01001010 01001011 00110010 00110011 00110100");

    private static readonly (int Code, int Length)[] WhiteMakeup = Codes(
        "11011 10010 010111 0110111 00110110 00110111 01100100 01100101 01101000 01100111 011001100 011001101 011010010 011010011 011010100 011010101 011010110 011010111 011011000 011011001 011011010 011011011 010011000 010011001 010011010 011000 010011011");

    private static readonly (int Code, int Length)[] BlackTerminating = Codes(
        "0000110111 010 11 10 011 0011 0010 00011 000101 000100 0000100 0000101 0000111 00000100 00000111 000011000 0000010111 0000011000 0000001000 00001100111 00001101000 00001101100 00000110111 00000101000 00000010111 00000011000 000011001010 000011001011 000011001100 000011001101 000001101000 000001101001 000001101010 000001101011 000011010010 000011010011 000011010100 000011010101 000011010110 000011010111 000001101100 000001101101 000011011010 000011011011 000001010100 000001010101 000001010110 000001010111 000001100100 000001100101 000001010010 000001010011 000000100100 000000110111 000000111000 000000100111 000000101000 000001011000 000001011001 000000101011 000000101100 000001011010 000001100110 000001100111");

    private static readonly (int Code, int Length)[] BlackMakeup = Codes(
        "0000001111 000011001000 000011001001 000001011011 000000110011 000000110100 000000110101 0000001101100 0000001101101 0000001001010 0000001001011 0000001001100 0000001001101 0000001110010 0000001110011 0000001110100 0000001110101 0000001110110 0000001110111 0000001010010 0000001010011 0000001010100 0000001010101 0000001011010 0000001011011 0000001100100 0000001100101");

    // T.4 table 3's extended makeup codes, 1792 to 2560, shared by both colors.
    private static readonly (int Code, int Length)[] ExtendedMakeup = Codes(
        "00000001000 00000001100 00000001101 000000010010 000000010011 000000010100 000000010101 000000010110 000000010111 000000011100 000000011101 000000011110 000000011111");

    private static (int, int)[] Codes(string codes) =>
        [.. codes.Split(' ').Select(c => (Convert.ToInt32(c, 2), c.Length))];

    /// <summary>Encodes <paramref name="rows"/> rows of <paramref name="width"/> pixels as Group 3 (1-D, an EOL before each row) or Group 4.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> packed, int width, int rows, bool group4)
    {
        int rowBytes = (width + 7) / 8;
        BitWriter bits = new(Math.Max(64, packed.Length / 2));
        int[] coding = new int[width + 4];
        int[] reference = new int[width + 4];
        int referenceCount = 0;
        reference[referenceCount++] = width;
        reference[referenceCount++] = width;
        for (int y = 0; y < rows; y++)
        {
            int count = Changes(packed.Slice(y * rowBytes, rowBytes), width, coding);
            if (group4)
            {
                TwoDimensional(ref bits, coding, count, reference, referenceCount, width);
                (coding, reference) = (reference, coding);
                referenceCount = count;
                reference[referenceCount++] = width;
                reference[referenceCount++] = width;
            }
            else
            {
                bits.Put(1, 12);
                OneDimensional(ref bits, coding, count, width);
            }
        }

        if (group4)
        {
            // EOFB: two EOLs.
            bits.Put(1, 12);
            bits.Put(1, 12);
        }

        return bits.ToArray();
    }

    /// <summary>Lists where a row changes color, starting from white; returns how many changes there are.</summary>
    private static int Changes(ReadOnlySpan<byte> row, int width, int[] changes)
    {
        // 64 pixels at a time: XOR with the current color leaves 1 bits where the color changes; runs of one color skip a word.
        int count = 0;
        ulong color = 0;
        Span<byte> tail = stackalloc byte[8];
        for (int at = 0; at < row.Length; at += 8)
        {
            ulong word;
            if (at + 8 <= row.Length)
            {
                word = BinaryPrimitives.ReadUInt64BigEndian(row[at..]);
            }
            else
            {
                tail.Clear();
                row[at..].CopyTo(tail);
                word = BinaryPrimitives.ReadUInt64BigEndian(tail);
            }

            ulong differs = word ^ color;
            while (differs != 0)
            {
                int bit = BitOperations.LeadingZeroCount(differs);
                int x = (at * 8) + bit;
                if (x >= width)
                {
                    return count;
                }

                changes[count++] = x;
                color = ~color;

                // Past this change, compare with the new color.
                differs = ~differs & (bit == 63 ? 0 : ulong.MaxValue >> (bit + 1));
            }
        }

        return count;
    }

    private static void OneDimensional(ref BitWriter bits, int[] changes, int count, int width)
    {
        int at = 0;
        bool white = true;
        for (int i = 0; i <= count; i++)
        {
            int end = i < count ? changes[i] : width;
            Run(ref bits, end - at, white);
            at = end;
            white = !white;
        }
    }

    /// <summary>Writes one run: makeup codes, then a terminating code.</summary>
    private static void Run(ref BitWriter bits, int run, bool white)
    {
        while (run >= 2624)
        {
            (int code, int length) = ExtendedMakeup[^1];
            bits.Put(code, length);
            run -= 2560;
        }

        if (run >= 64)
        {
            int makeup = run / 64;
            (int code, int length) = makeup <= 27 ? (white ? WhiteMakeup : BlackMakeup)[makeup - 1] : ExtendedMakeup[makeup - 28];
            bits.Put(code, length);
            run -= makeup * 64;
        }

        (int term, int termLength) = (white ? WhiteTerminating : BlackTerminating)[run];
        bits.Put(term, termLength);
    }

    /// <summary>Codes a row against the one above (T.6 section 2.2): pass, vertical or horizontal mode at each change.</summary>
    private static void TwoDimensional(ref BitWriter bits, int[] coding, int count, int[] reference, int referenceCount, int width)
    {
        int a0 = -1;
        bool white = true;
        int ai = 0;
        int bi = 0;
        while (true)
        {
            // a1: the next change on the coding row after a0; b1: the first change on the reference row after a0 that turns to
            // the color opposite a0's (changes at even indexes turn to black), b2 the one after it.
            while (ai < count && coding[ai] <= a0)
            {
                ai++;
            }

            int a1 = ai < count ? coding[ai] : width;
            while (bi > 0 && reference[bi - 1] > a0)
            {
                bi--;
            }

            while (bi < referenceCount - 2 && (reference[bi] <= a0 || ((bi & 1) == 0) != white))
            {
                bi++;
            }

            int b1 = reference[bi];
            int b2 = reference[Math.Min(bi + 1, referenceCount - 1)];
            if (b2 < a1)
            {
                bits.Put(0b0001, 4);
                a0 = b2;
                continue;
            }

            int delta = a1 - b1;
            if (delta is >= -3 and <= 3)
            {
                (int code, int length) = delta switch
                {
                    0 => (1, 1),
                    1 => (0b011, 3),
                    -1 => (0b010, 3),
                    2 => (0b000011, 6),
                    -2 => (0b000010, 6),
                    3 => (0b0000011, 7),
                    _ => (0b0000010, 7),
                };
                bits.Put(code, length);
                a0 = a1;
                white = !white;
            }
            else
            {
                int a2 = ai + 1 < count ? coding[ai + 1] : width;
                bits.Put(0b001, 3);
                Run(ref bits, a1 - Math.Max(a0, 0), white);
                Run(ref bits, a2 - a1, !white);
                a0 = a2;
            }

            if (a0 >= width)
            {
                return;
            }
        }
    }

    /// <summary>Bits most significant first into a growing buffer.</summary>
    private struct BitWriter(int capacity)
    {
        private byte[] _bytes = new byte[capacity];
        private int _count;
        private ulong _buffer;
        private int _bits;

        public void Put(int code, int length)
        {
            _buffer = (_buffer << length) | (uint)code;
            _bits += length;
            while (_bits >= 8)
            {
                _bits -= 8;
                if (_count == _bytes.Length)
                {
                    Array.Resize(ref _bytes, _bytes.Length * 2);
                }

                _bytes[_count++] = (byte)(_buffer >> _bits);
            }
        }

        public byte[] ToArray()
        {
            if (_bits > 0)
            {
                Put(0, 8 - _bits);
            }

            return _bytes.AsSpan(0, _count).ToArray();
        }
    }
}
