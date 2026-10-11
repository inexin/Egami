using System.Runtime.CompilerServices;

namespace Egami.Formats.Jbig2;

/// <summary>Bits most significant first from a byte array, for the Huffman variants of JBIG2 (T.88 5.4.1). Bits past the end read as 0.</summary>
internal sealed class Jbig2BitReader
{
    private readonly byte[] _data;
    private readonly int _end;
    private long _bit;

    internal Jbig2BitReader(byte[] data, int start, int end)
    {
        _data = data;
        _end = end;
        _bit = (long)start * 8;
    }

    /// <summary>Gets the offset of the byte holding the next bit.</summary>
    internal int BytePosition => (int)(_bit >> 3);

    /// <summary>Gets whether a read has gone past the end of the data.</summary>
    internal bool Overrun => _bit > (long)_end * 8;

    internal int End => _end;

    internal byte[] Data => _data;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int ReadBit()
    {
        int at = (int)(_bit >> 3);
        int bit = at < _end ? (_data[at] >> (7 - (int)(_bit & 7))) & 1 : 0;
        _bit++;
        return bit;
    }

    /// <summary>Reads up to 32 bits as an unsigned value.</summary>
    internal uint ReadBits(int count)
    {
        uint value = 0;
        for (int i = 0; i < count; i++)
        {
            value = (value << 1) | (uint)ReadBit();
        }

        return value;
    }

    /// <summary>Skips to the next byte boundary.</summary>
    internal void Align()
    {
        _bit = (_bit + 7) & ~7L;
    }

    /// <summary>Moves to a byte offset.</summary>
    internal void Seek(int bytePosition)
    {
        _bit = (long)bytePosition * 8;
    }
}

/// <summary>
/// A JBIG2 Huffman table (T.88 Annex B): table lines with prefix lengths, range lengths and range lows, the lower and upper
/// range lines and an optional out-of-band line, with the prefix codes of B.3.
/// </summary>
internal sealed class Jbig2HuffmanTable
{
    /// <summary>The value <see cref="Decode"/> returns for the out-of-band code.</summary>
    internal const long Oob = long.MinValue;

    private readonly int[] _rangeLength;
    private readonly long[] _rangeLow;
    private readonly bool[] _lower;
    private readonly int _oobLine;

    // Canonical decoding: for each prefix length, the first code and the lines with that length in table order.
    private readonly int[] _firstCode;
    private readonly int[] _countByLength;
    private readonly int[] _startByLength;
    private readonly int[] _linesByCode;
    private readonly int _maxLength;

    private Jbig2HuffmanTable(int[] prefixLength, int[] rangeLength, long[] rangeLow, bool[] lower, int oobLine)
    {
        _rangeLength = rangeLength;
        _rangeLow = rangeLow;
        _lower = lower;
        _oobLine = oobLine;
        HasOob = oobLine >= 0;
        int n = prefixLength.Length;
        int max = 0;
        foreach (int length in prefixLength)
        {
            max = Math.Max(max, length);
        }

        _maxLength = max;
        _countByLength = new int[max + 1];
        foreach (int length in prefixLength)
        {
            _countByLength[length]++;
        }

        // B.3: FIRSTCODE[len] = (FIRSTCODE[len - 1] + LENCOUNT[len - 1]) * 2, LENCOUNT[0] = 0; codes in line order per length.
        _firstCode = new int[max + 1];
        _startByLength = new int[max + 2];
        long first = 0;
        for (int length = 1; length <= max; length++)
        {
            first = (first + (length == 1 ? 0 : _countByLength[length - 1])) * 2;
            if (first + _countByLength[length] > (1L << length))
            {
                throw new InvalidDataException("The Huffman table's prefix lengths over-subscribe the code space.");
            }

            _firstCode[length] = (int)first;
            _startByLength[length + 1] = _startByLength[length] + _countByLength[length];
        }

        _linesByCode = new int[n];
        int[] next = (int[])_startByLength.Clone();
        for (int line = 0; line < n; line++)
        {
            if (prefixLength[line] > 0)
            {
                _linesByCode[next[prefixLength[line]]++] = line;
            }
        }
    }

    /// <summary>Gets whether the table can code the out-of-band value.</summary>
    internal bool HasOob { get; }

    /// <summary>Decodes a value (B.4), or returns <see cref="Oob"/>. Throws <see cref="InvalidDataException"/> for a code no line has.</summary>
    internal long Decode(Jbig2BitReader bits)
    {
        int code = 0;
        for (int length = 1; length <= _maxLength; length++)
        {
            code = (code << 1) | bits.ReadBit();
            int index = code - _firstCode[length];
            if (index >= 0 && index < _countByLength[length])
            {
                int line = _linesByCode[_startByLength[length] + index];
                if (line == _oobLine)
                {
                    return Oob;
                }

                long offset = bits.ReadBits(_rangeLength[line]);
                return _lower[line] ? _rangeLow[line] - offset : _rangeLow[line] + offset;
            }
        }

        throw new InvalidDataException("A Huffman code matches no table line.");
    }

    /// <summary>Decodes a value the caller needs as an int, failing for OOB or a value out of range.</summary>
    internal int DecodeInt(Jbig2BitReader bits)
    {
        long value = Decode(bits);
        return value == Oob || value < int.MinValue || value > int.MaxValue
            ? throw new InvalidDataException("A Huffman-coded value is out of range.")
            : (int)value;
    }

    /// <summary>Builds a table from its lines: (range low, prefix length, range length) in table order, then the special lines.</summary>
    /// <param name="lines">The normal table lines.</param>
    /// <param name="lowerPrefix">The lower range line's prefix length, 0 if unused; its range low is the first line's low minus 1.</param>
    /// <param name="upper">The upper range line's prefix length and range low, prefix length 0 if unused.</param>
    /// <param name="oobPrefix">The out-of-band line's prefix length, or -1 if the table has none.</param>
    internal static Jbig2HuffmanTable Create((long Low, int Prefix, int Range)[] lines, int lowerPrefix, (int Prefix, long Low) upper, int oobPrefix)
    {
        int n = lines.Length + 2 + (oobPrefix >= 0 ? 1 : 0);
        int[] prefix = new int[n];
        int[] range = new int[n];
        long[] low = new long[n];
        bool[] lower = new bool[n];
        for (int i = 0; i < lines.Length; i++)
        {
            (low[i], prefix[i], range[i]) = lines[i];
        }

        int at = lines.Length;
        prefix[at] = lowerPrefix;
        range[at] = 32;
        low[at] = lines.Length > 0 ? lines[0].Low - 1 : 0;
        lower[at] = true;
        prefix[at + 1] = upper.Prefix;
        range[at + 1] = 32;
        low[at + 1] = upper.Low;
        int oob = -1;
        if (oobPrefix >= 0)
        {
            oob = at + 2;
            prefix[oob] = oobPrefix;
        }

        return new Jbig2HuffmanTable(prefix, range, low, lower, oob);
    }

    /// <summary>Reads a code table segment (B.2), whose data is <paramref name="length"/> bytes at <paramref name="start"/>.</summary>
    internal static Jbig2HuffmanTable Read(byte[] data, int start, int length)
    {
        if (length < 9)
        {
            throw new InvalidDataException("A code table segment is shorter than its header.");
        }

        int flags = data[start];
        bool oob = (flags & 1) != 0;
        int htps = ((flags >> 1) & 7) + 1;
        int htrs = ((flags >> 4) & 7) + 1;
        long htlow = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(start + 1));
        long hthigh = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(start + 5));
        if (hthigh < htlow)
        {
            throw new InvalidDataException("A code table's highest value is below its lowest.");
        }

        Jbig2BitReader bits = new(data, start + 9, start + length);
        List<(long, int, int)> lines = [];
        long current = htlow;
        while (current < hthigh)
        {
            int prefix = (int)bits.ReadBits(htps);
            int range = (int)bits.ReadBits(htrs);
            if (range > 32 || lines.Count > 65536 || bits.Overrun)
            {
                throw new InvalidDataException("A code table segment's lines are damaged.");
            }

            lines.Add((current, prefix, range));
            current += 1L << range;
        }

        int lowPrefix = (int)bits.ReadBits(htps);
        int highPrefix = (int)bits.ReadBits(htps);
        int oobPrefix = oob ? (int)bits.ReadBits(htps) : -1;
        if (bits.Overrun)
        {
            throw new InvalidDataException("A code table segment ends inside its lines.");
        }

        Jbig2HuffmanTable table = Create([.. lines], lowPrefix, (highPrefix, hthigh), oobPrefix);
        table._rangeLow[lines.Count] = htlow - 1;
        return table;
    }

    /// <summary>Gets standard table B.1 to B.15 by its number.</summary>
    internal static Jbig2HuffmanTable Standard(int number) => StandardTables[number - 1];

    private static readonly Jbig2HuffmanTable[] StandardTables =
    [
        // B.1 (A)
        Create([(0, 1, 4), (16, 2, 8), (272, 3, 16)], 0, (3, 65808), -1),

        // B.2 (B)
        Create([(0, 1, 0), (1, 2, 0), (2, 3, 0), (3, 4, 3), (11, 5, 6)], 0, (6, 75), 6),

        // B.3 (C)
        Create([(-256, 8, 8), (0, 1, 0), (1, 2, 0), (2, 3, 0), (3, 4, 3), (11, 5, 6)], 8, (7, 75), 6),

        // B.4 (D)
        Create([(1, 1, 0), (2, 2, 0), (3, 3, 0), (4, 4, 3), (12, 5, 6)], 0, (5, 76), -1),

        // B.5 (E)
        Create([(-255, 7, 8), (1, 1, 0), (2, 2, 0), (3, 3, 0), (4, 4, 3), (12, 5, 6)], 7, (6, 76), -1),

        // B.6 (F)
        Create(
            [(-2048, 5, 10), (-1024, 4, 9), (-512, 4, 8), (-256, 4, 7), (-128, 5, 6), (-64, 5, 5), (-32, 4, 5), (0, 2, 7), (128, 3, 7),
                (256, 3, 8), (512, 4, 9), (1024, 4, 10)],
            6,
            (6, 2048),
            -1),

        // B.7 (G)
        Create(
            [(-1024, 4, 9), (-512, 3, 8), (-256, 4, 7), (-128, 5, 6), (-64, 5, 5), (-32, 4, 5), (0, 4, 5), (32, 5, 5), (64, 5, 6),
                (128, 4, 7), (256, 3, 8), (512, 3, 9), (1024, 3, 10)],
            5,
            (5, 2048),
            -1),

        // B.8 (H)
        Create(
            [(-15, 8, 3), (-7, 9, 1), (-5, 8, 1), (-3, 9, 0), (-2, 7, 0), (-1, 4, 0), (0, 2, 1), (2, 5, 0), (3, 6, 0), (4, 3, 4),
                (20, 6, 1), (22, 4, 4), (38, 4, 5), (70, 5, 6), (134, 5, 7), (262, 6, 7), (390, 7, 8), (646, 6, 10)],
            9,
            (9, 1670),
            2),

        // B.9 (I)
        Create(
            [(-31, 8, 4), (-15, 9, 2), (-11, 8, 2), (-7, 9, 1), (-5, 7, 1), (-3, 4, 1), (-1, 3, 1), (1, 3, 1), (3, 5, 1), (5, 6, 1),
                (7, 3, 5), (39, 6, 2), (43, 4, 5), (75, 4, 6), (139, 5, 7), (267, 5, 8), (523, 6, 8), (779, 7, 9), (1291, 6, 11)],
            9,
            (9, 3339),
            2),

        // B.10 (J)
        Create(
            [(-21, 7, 4), (-5, 8, 0), (-4, 7, 0), (-3, 5, 0), (-2, 2, 2), (2, 5, 0), (3, 6, 0), (4, 7, 0), (5, 8, 0), (6, 2, 6),
                (70, 5, 5), (102, 6, 5), (134, 6, 6), (198, 6, 7), (326, 6, 8), (582, 6, 9), (1094, 6, 10), (2118, 7, 11)],
            8,
            (8, 4166),
            2),

        // B.11 (K)
        Create(
            [(1, 1, 0), (2, 2, 1), (4, 4, 0), (5, 4, 1), (7, 5, 1), (9, 5, 2), (13, 6, 2), (17, 7, 2), (21, 7, 3), (29, 7, 4), (45, 7, 5),
                (77, 7, 6)],
            0,
            (7, 141),
            -1),

        // B.12 (L)
        Create(
            [(1, 1, 0), (2, 2, 0), (3, 3, 1), (5, 5, 0), (6, 5, 1), (8, 6, 1), (10, 7, 0), (11, 7, 1), (13, 7, 2), (17, 7, 3), (25, 7, 4),
                (41, 8, 5)],
            0,
            (8, 73),
            -1),

        // B.13 (M)
        Create(
            [(1, 1, 0), (2, 3, 0), (3, 4, 0), (4, 5, 0), (5, 4, 1), (7, 3, 3), (15, 6, 1), (17, 6, 2), (21, 6, 3), (29, 6, 4), (45, 6, 5),
                (77, 7, 6)],
            0,
            (7, 141),
            -1),

        // B.14 (N)
        Create([(-2, 3, 0), (-1, 3, 0), (0, 1, 0), (1, 3, 0), (2, 3, 0)], 0, (0, 3), -1),

        // B.15 (O)
        Create(
            [(-24, 7, 4), (-8, 6, 2), (-4, 5, 1), (-2, 4, 0), (-1, 3, 0), (0, 1, 0), (1, 3, 0), (2, 4, 0), (3, 5, 1), (5, 6, 2), (9, 7, 4)],
            7,
            (7, 25),
            -1),
    ];

    /// <summary>Returns each line's prefix code as a bit string, for tests against the tables' printed encodings.</summary>
    internal IEnumerable<(long Low, bool Lower, bool IsOob, string Code)> Codes()
    {
        for (int length = 1; length <= _maxLength; length++)
        {
            for (int i = 0; i < _countByLength[length]; i++)
            {
                int line = _linesByCode[_startByLength[length] + i];
                string code = Convert.ToString(_firstCode[length] + i, 2).PadLeft(length, '0');
                yield return (_rangeLow[line], _lower[line], line == _oobLine, code);
            }
        }
    }
}
