using System.Buffers.Binary;
using System.Runtime.CompilerServices;

using Egami.Formats.Tiff;

namespace Egami.Formats.Ccitt;

/// <summary>
/// CCITT fax decoding, one decoder for PDF's CCITTFaxDecode, TIFF and JBIG2's MMR regions: modified Huffman rows (TIFF
/// compression 2, and 32771 with rows on 16-bit words), Group 3 (ITU-T T.4, one- and two-dimensional, with EOLs) and Group 4
/// (ITU-T T.6), driven by PDF's eight parameters, which TIFF's compressions and JBIG2's MMR map onto. Rows come out packed most
/// significant bit first, a black run as 1 bits, a white run as 0 bits. Moved from Lars's ClassicMac (CcittFax.cs, MIT): the
/// same decoding, with the run and mode codes looked up in tables instead of one bit at a time.
/// </summary>
internal sealed class CcittDecoder
{
    // The run-length codes of T.4 tables 2 and 3: (bits, run).
    private static readonly (string Bits, int Run)[] WhiteCodes =
    [
        ("00110101", 0), ("000111", 1), ("0111", 2), ("1000", 3), ("1011", 4), ("1100", 5), ("1110", 6), ("1111", 7),
        ("10011", 8), ("10100", 9), ("00111", 10), ("01000", 11), ("001000", 12), ("000011", 13), ("110100", 14),
        ("110101", 15), ("101010", 16), ("101011", 17), ("0100111", 18), ("0001100", 19), ("0001000", 20),
        ("0010111", 21), ("0000011", 22), ("0000100", 23), ("0101000", 24), ("0101011", 25), ("0010011", 26),
        ("0100100", 27), ("0011000", 28), ("00000010", 29), ("00000011", 30), ("00011010", 31), ("00011011", 32),
        ("00010010", 33), ("00010011", 34), ("00010100", 35), ("00010101", 36), ("00010110", 37), ("00010111", 38),
        ("00101000", 39), ("00101001", 40), ("00101010", 41), ("00101011", 42), ("00101100", 43), ("00101101", 44),
        ("00000100", 45), ("00000101", 46), ("00001010", 47), ("00001011", 48), ("01010010", 49), ("01010011", 50),
        ("01010100", 51), ("01010101", 52), ("00100100", 53), ("00100101", 54), ("01011000", 55), ("01011001", 56),
        ("01011010", 57), ("01011011", 58), ("01001010", 59), ("01001011", 60), ("00110010", 61), ("00110011", 62),
        ("00110100", 63),
        ("11011", 64), ("10010", 128), ("010111", 192), ("0110111", 256), ("00110110", 320), ("00110111", 384),
        ("01100100", 448), ("01100101", 512), ("01101000", 576), ("01100111", 640), ("011001100", 704),
        ("011001101", 768), ("011010010", 832), ("011010011", 896), ("011010100", 960), ("011010101", 1024),
        ("011010110", 1088), ("011010111", 1152), ("011011000", 1216), ("011011001", 1280), ("011011010", 1344),
        ("011011011", 1408), ("010011000", 1472), ("010011001", 1536), ("010011010", 1600), ("011000", 1664),
        ("010011011", 1728),
    ];

    private static readonly (string Bits, int Run)[] BlackCodes =
    [
        ("0000110111", 0), ("010", 1), ("11", 2), ("10", 3), ("011", 4), ("0011", 5), ("0010", 6), ("00011", 7),
        ("000101", 8), ("000100", 9), ("0000100", 10), ("0000101", 11), ("0000111", 12), ("00000100", 13),
        ("00000111", 14), ("000011000", 15), ("0000010111", 16), ("0000011000", 17), ("0000001000", 18),
        ("00001100111", 19), ("00001101000", 20), ("00001101100", 21), ("00000110111", 22), ("00000101000", 23),
        ("00000010111", 24), ("00000011000", 25), ("000011001010", 26), ("000011001011", 27), ("000011001100", 28),
        ("000011001101", 29), ("000001101000", 30), ("000001101001", 31), ("000001101010", 32), ("000001101011", 33),
        ("000011010010", 34), ("000011010011", 35), ("000011010100", 36), ("000011010101", 37), ("000011010110", 38),
        ("000011010111", 39), ("000001101100", 40), ("000001101101", 41), ("000011011010", 42), ("000011011011", 43),
        ("000001010100", 44), ("000001010101", 45), ("000001010110", 46), ("000001010111", 47), ("000001100100", 48),
        ("000001100101", 49), ("000001010010", 50), ("000001010011", 51), ("000000100100", 52), ("000000110111", 53),
        ("000000111000", 54), ("000000100111", 55), ("000000101000", 56), ("000001011000", 57), ("000001011001", 58),
        ("000000101011", 59), ("000000101100", 60), ("000001011010", 61), ("000001100110", 62), ("000001100111", 63),
        ("0000001111", 64), ("000011001000", 128), ("000011001001", 192), ("000001011011", 256), ("000000110011", 320),
        ("000000110100", 384), ("000000110101", 448), ("0000001101100", 512), ("0000001101101", 576),
        ("0000001001010", 640), ("0000001001011", 704), ("0000001001100", 768), ("0000001001101", 832),
        ("0000001110010", 896), ("0000001110011", 960), ("0000001110100", 1024), ("0000001110101", 1088),
        ("0000001110110", 1152), ("0000001110111", 1216), ("0000001010010", 1280), ("0000001010011", 1344),
        ("0000001010100", 1408), ("0000001010101", 1472), ("0000001011010", 1536), ("0000001011011", 1600),
        ("0000001100100", 1664), ("0000001100101", 1728),
    ];

    // The extended makeup codes of T.4 table 3, shared by both colors.
    private static readonly (string Bits, int Run)[] ExtendedCodes =
    [
        ("00000001000", 1792), ("00000001100", 1856), ("00000001101", 1920), ("000000010010", 1984),
        ("000000010011", 2048), ("000000010100", 2112), ("000000010101", 2176), ("000000010110", 2240),
        ("000000010111", 2304), ("000000011100", 2368), ("000000011101", 2432), ("000000011110", 2496),
        ("000000011111", 2560),
    ];

    private const int Lookup = 13;

    // Each table maps the next 13 bits to run | (code length << 16), or 0 where no code starts.
    private static readonly int[] White = Table(WhiteCodes);
    private static readonly int[] Black = Table(BlackCodes);

    // The 2-D mode codes of T.4 table 4, looked up by the next 7 bits: mode | (length << 8), modes below.
    private static readonly short[] Modes = ModeTable();

    private const int Pass = 1;
    private const int Horizontal = 2;
    private const int Vertical0 = 10;

    private static int[] Table((string Bits, int Run)[] codes)
    {
        int[] table = new int[1 << Lookup];
        foreach ((string bits, int run) in codes.Concat(ExtendedCodes))
        {
            int code = Convert.ToInt32(bits, 2) << (Lookup - bits.Length);
            for (int i = 0; i < 1 << (Lookup - bits.Length); i++)
            {
                table[code | i] = run | (bits.Length << 16);
            }
        }

        return table;
    }

    private static short[] ModeTable()
    {
        short[] table = new short[128];
        (string Bits, int Mode)[] codes =
        [
            ("1", Vertical0), ("011", Vertical0 + 1), ("010", Vertical0 - 1), ("001", Horizontal), ("0001", Pass),
            ("000011", Vertical0 + 2), ("000010", Vertical0 - 2), ("0000011", Vertical0 + 3), ("0000010", Vertical0 - 3),
        ];
        foreach ((string bits, int mode) in codes)
        {
            int code = Convert.ToInt32(bits, 2) << (7 - bits.Length);
            for (int i = 0; i < 1 << (7 - bits.Length); i++)
            {
                table[code | i] = (short)(mode | (bits.Length << 8));
            }
        }

        return table;
    }

    private readonly int _k;
    private readonly bool _endOfLine;
    private readonly bool _align;
    private readonly int _alignBits;
    private readonly bool _eolsPossible;
    private readonly int _columns;
    private readonly int _rows;
    private readonly int _damagedRowsAllowed;
    private readonly int _rowBytes;
    private int[] _a;
    private int[] _b;
    private int _referenceCount;
    private long _position;
    private int _damagedRows;
    private byte[]? _previous;

    /// <summary>Starts decoding with PDF's parameters; each <see cref="NextRow"/> is given the same coded data.</summary>
    /// <param name="k">PDF's K: below 0 Group 4 (T.6), 0 one-dimensional Group 3, above 0 mixed Group 3 (T.4).</param>
    /// <param name="endOfLine">Whether EOLs are required; they are accepted where present either way.</param>
    /// <param name="align">PDF's EncodedByteAlign: Group 4 rows, or Group 3 rows without an EOL, start on a byte boundary (16-bit words with <paramref name="alignBits"/> 16).</param>
    /// <param name="columns">The row width in pixels.</param>
    /// <param name="rows">The rows to decode, or 0 to decode until the data or its end-of-block code ends.</param>
    /// <param name="damagedRowsAllowed">PDF's DamagedRowsBeforeError: with EOLs and K 0 or above, how many bad rows are skipped at the next EOL.</param>
    /// <param name="eolsPossible">False for TIFF's modified Huffman rows, which have no EOLs, so zeros before a row are never skipped.</param>
    /// <param name="alignBits">8, or 16 for TIFF compression 32771.</param>
    internal CcittDecoder(int k, bool endOfLine, bool align, int columns, int rows, int damagedRowsAllowed, bool eolsPossible = true, int alignBits = 8)
    {
        _k = k;
        _endOfLine = endOfLine;
        _align = align;
        _alignBits = alignBits;
        _eolsPossible = eolsPossible;
        _columns = columns;
        _rows = rows;
        _damagedRowsAllowed = damagedRowsAllowed;
        _rowBytes = (columns + 7) / 8;
        _a = new int[columns + 4];
        _b = new int[columns + 4];
        _b[0] = columns;
        _b[1] = columns;
        _referenceCount = 2;
    }

    /// <summary>Gets the rows decoded so far, damaged rows that were skipped included.</summary>
    internal int RowsDone { get; private set; }

    /// <summary>Gets whether decoding has ended: the rows asked for are done, an end-of-block code was read, or the data ended or is damaged.</summary>
    internal bool Ended { get; private set; }

    /// <summary>Gets what was wrong when the data is damaged (a bad row the decoder could not skip), or null.</summary>
    internal string? Damage { get; private set; }

    /// <summary>Gets how many damaged rows were skipped, each a copy of the row before it.</summary>
    internal int SkippedRows => _damagedRows;

    /// <summary>Gets the bytes read so far, rounded up to a whole byte.</summary>
    internal int BytesRead => (int)((_position + 7) >> 3);

    /// <summary>
    /// Decodes the next row into <paramref name="row"/>, packed most significant bit first with black runs as 1 bits; returns
    /// false, leaving <paramref name="row"/> alone, when decoding has ended.
    /// </summary>
    internal bool NextRow(ReadOnlySpan<byte> data, Span<byte> row)
    {
        if (Ended)
        {
            return false;
        }

        if (_rows > 0 && RowsDone >= _rows)
        {
            Ended = true;
            return false;
        }

        BitReader bits = new(data, _position);
        long start = bits.Position;
        bool twoDimensional = _k < 0;
        if (_k < 0)
        {
            if (_align && RowsDone > 0)
            {
                bits.Align(_alignBits);
            }

            if (_endOfLine)
            {
                SkipEol(ref bits);
            }

            // EOFB: two EOLs (T.6 2.4.2).
            if (bits.Left >= 24 && bits.Peek(24) == 0x001001)
            {
                return End(ref bits, null);
            }
        }
        else
        {
            bool eol = _eolsPossible && (_align ? SkipAlignedEol(ref bits) : SkipEol(ref bits));
            if (eol && EolFollows(bits))
            {
                // RTC: six EOLs end the page (T.4 4.1.4); the first ended the last row, the next starts no row.
                return End(ref bits, null);
            }

            if (!eol && _align && RowsDone > 0)
            {
                bits.Align(_alignBits);
            }

            if (_k > 0)
            {
                twoDimensional = bits.Read(1) == 0;
            }
        }

        int count = bits.Left <= 0 ? -1
            : twoDimensional ? TwoDimensional(ref bits, _columns, _b, _referenceCount, _a) : OneDimensional(ref bits, _columns, _a);
        if (count < 0)
        {
            return Damaged(ref bits, start, row);
        }

        Fill(row[.._rowBytes], _a, count, _columns);
        (_a, _b) = (_b, _a);
        _b[count] = _columns;
        _b[count + 1] = _columns;
        _referenceCount = count + 2;
        _position = bits.Position;
        RowsDone++;
        if (_damagedRowsAllowed > 0)
        {
            _previous ??= new byte[_rowBytes];
            row[.._rowBytes].CopyTo(_previous);
        }

        return true;
    }

    /// <summary>
    /// A row that did not decode. With EOLs, K 0 or above and damaged rows still allowed, the decoder finds the next EOL and the
    /// row repeats the one before it; otherwise decoding ends, as damage unless only zeros or nothing were left.
    /// </summary>
    private bool Damaged(ref BitReader bits, long start, Span<byte> row)
    {
        bits.Seek(start);
        if (bits.Left < 12 || bits.OnlyZeros())
        {
            return End(ref bits, null);
        }

        if (_endOfLine && _k >= 0 && _damagedRows < _damagedRowsAllowed && FindEol(ref bits, start + 1))
        {
            _damagedRows++;
            if (_previous is null)
            {
                row[.._rowBytes].Clear();
            }
            else
            {
                _previous.CopyTo(row);
            }

            // The reference row of a 2-D row after this one stays the row before, which this one repeats.
            _position = bits.Position;
            RowsDone++;
            return true;
        }

        return End(ref bits, $"Row {RowsDone} of the CCITT data is damaged at bit {start}.");
    }

    private bool End(ref BitReader bits, string? damage)
    {
        _position = bits.Position;
        Ended = true;
        Damage = damage;
        return false;
    }

    /// <summary>Skips fill (12 or more zeros) and an EOL if one follows; returns whether an EOL was read.</summary>
    private static bool SkipEol(ref BitReader bits)
    {
        while (bits.Left >= 12 && bits.Peek(12) == 0)
        {
            bits.Skip(1);
        }

        if (bits.Left >= 12 && bits.Peek(12) == 1)
        {
            bits.Skip(12);
            return true;
        }

        return false;
    }

    /// <summary>
    /// With EncodedByteAlign, an EOL ends on a byte boundary after its fill bits (T.4 4.1.3), so zeros are an EOL only when 11
    /// or more of them and a 1 end exactly there; any other zeros belong to the row, which then starts on the next boundary.
    /// Returns whether an EOL was read.
    /// </summary>
    private static bool SkipAlignedEol(ref BitReader bits)
    {
        long start = bits.Position;
        while (bits.Left > 0 && bits.Peek(1) == 0)
        {
            bits.Skip(1);
        }

        if (bits.Left > 0 && bits.Position - start >= 11 && ((bits.Position + 1) & 7) == 0)
        {
            bits.Skip(1);
            return true;
        }

        bits.Seek(start);
        return false;
    }

    /// <summary>Whether another EOL comes next, after the tag bit with K above 0 and any fill: the rest of RTC or EOFB.</summary>
    private bool EolFollows(BitReader bits)
    {
        if (_k > 0)
        {
            bits.Skip(1);
        }

        return _align ? SkipAlignedEol(ref bits) : SkipEol(ref bits);
    }

    /// <summary>Moves to the next EOL at or after bit <paramref name="from"/>, leaving it to be read; returns false when none is left.</summary>
    private static bool FindEol(ref BitReader bits, long from)
    {
        bits.Seek(from);
        while (bits.Left >= 12)
        {
            if (bits.Peek(12) == 1)
            {
                return true;
            }

            bits.Skip(1);
        }

        return false;
    }

    /// <summary>
    /// Decodes TIFF's CCITT strips (compressions 2, 3, 4 and 32771): <paramref name="rows"/> rows of <paramref name="width"/>
    /// pixels into <paramref name="output"/>; returns the bytes of the rows decoded whole (fewer when the data ends or is damaged).
    /// T4Options bit 0 asks for mixed 1-D and 2-D rows; the fill bits of bit 2 are skipped with the EOL they come before.
    /// </summary>
    public static int DecodeTiff(ReadOnlySpan<byte> input, Span<byte> output, int width, int rows, int compression, uint t4Options)
    {
        CcittDecoder decoder = compression switch
        {
            TiffCodes.CcittRle => new(0, false, true, width, rows, 0, eolsPossible: false),
            TiffCodes.CcittRleWord => new(0, false, true, width, rows, 0, eolsPossible: false, alignBits: 16),
            TiffCodes.CcittGroup3 => new((t4Options & 1) != 0 ? 1 : 0, false, false, width, rows, 0),
            _ => new(-1, false, false, width, rows, 0),
        };
        int rowBytes = (width + 7) / 8;
        while (decoder.RowsDone < rows && decoder.NextRow(input, output.Slice(decoder.RowsDone * rowBytes, rowBytes)))
        {
        }

        return decoder.RowsDone * rowBytes;
    }

    /// <summary>
    /// Decodes MMR as JBIG2 uses it (T.88 6.2.6): Group 4 rows, then an optional EOFB, ending on a byte boundary. Rows that the
    /// data does not reach stay white. Returns the rows decoded and sets <paramref name="consumed"/> to the bytes used.
    /// </summary>
    public static int DecodeMmr(ReadOnlySpan<byte> input, Span<byte> output, int width, int rows, out int consumed)
    {
        CcittDecoder decoder = new(-1, false, false, width, rows, 0);
        int rowBytes = (width + 7) / 8;
        while (decoder.RowsDone < rows && decoder.NextRow(input, output.Slice(decoder.RowsDone * rowBytes, rowBytes)))
        {
        }

        BitReader bits = new(input, decoder._position);
        if (bits.Left >= 24 && bits.Peek(24) == 0x001001)
        {
            bits.Skip(24);
        }

        bits.Align(8);
        consumed = (int)(bits.Position >> 3);
        return decoder.RowsDone;
    }

    /// <summary>Sets the bits of a row's black runs: changes alternate white to black, black to white.</summary>
    private static void Fill(Span<byte> row, int[] changes, int count, int width)
    {
        row.Clear();
        for (int i = 0; i < count; i += 2)
        {
            int start = changes[i];
            int end = Math.Min(i + 1 < count ? changes[i + 1] : width, width);
            if (end <= start)
            {
                continue;
            }

            int first = start >> 3;
            int last = (end - 1) >> 3;
            byte head = (byte)(0xFF >> (start & 7));
            byte tail = (byte)(0xFF << (7 - ((end - 1) & 7)));
            if (first == last)
            {
                row[first] |= (byte)(head & tail);
                continue;
            }

            row[first] |= head;
            row.Slice(first + 1, last - first - 1).Fill(0xFF);
            row[last] |= tail;
        }
    }

    /// <summary>A modified Huffman row: runs alternating white and black, from white, until the width is filled. Returns the changes, or -1.</summary>
    private static int OneDimensional(ref BitReader bits, int width, int[] changes)
    {
        int at = 0;
        int count = 0;
        bool white = true;
        while (at < width)
        {
            int run = Run(ref bits, white);
            if (run < 0)
            {
                return -1;
            }

            at += run;
            if (at > width)
            {
                return -1;
            }

            if (at < width)
            {
                changes[count++] = at;
            }

            white = !white;
        }

        return count;
    }

    /// <summary>One run: makeup codes, then a terminating code (below 64). Returns -1 for a code that is not one.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Run(ref BitReader bits, bool white)
    {
        int[] table = white ? White : Black;
        int total = 0;
        while (true)
        {
            if (bits.Left <= 0)
            {
                return -1;
            }

            int entry = table[bits.Peek(Lookup)];
            if (entry == 0)
            {
                return -1;
            }

            bits.Skip(entry >> 16);
            int run = entry & 0xFFFF;
            total += run;
            if (run < 64)
            {
                return total;
            }
        }
    }

    /// <summary>A two-dimensional row (T.4 section 4.2, T.6): each change coded against the row above. Returns the changes, or -1.</summary>
    private static int TwoDimensional(ref BitReader bits, int width, int[] reference, int referenceCount, int[] changes)
    {
        int a0 = -1;
        int count = 0;
        bool white = true;
        int bi = 0;
        while (a0 < width)
        {
            // b1: the first change on the reference row right of a0 that turns to the color opposite the current one (changes at
            // even indexes turn to black); b2 the next.
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
            if (bits.Left <= 0)
            {
                return -1;
            }

            int entry = Modes[bits.Peek(7)];
            if (entry == 0)
            {
                // Extensions (uncompressed mode) and EOLs inside a row are not read.
                return -1;
            }

            bits.Skip(entry >> 8);
            int mode = entry & 0xFF;
            if (mode == Pass)
            {
                a0 = b2;
                continue;
            }

            if (mode == Horizontal)
            {
                int start = Math.Max(a0, 0);
                int first = Run(ref bits, white);
                int second = first < 0 ? -1 : Run(ref bits, !white);
                if (second < 0)
                {
                    return -1;
                }

                int a1 = start + first;
                int a2 = a1 + second;
                if (a2 > width || count > width)
                {
                    return -1;
                }

                if (a1 < width)
                {
                    changes[count++] = a1;
                }

                if (a2 < width)
                {
                    changes[count++] = a2;
                }

                a0 = a2;
                continue;
            }

            int to = b1 + mode - Vertical0;
            if (to < Math.Max(a0, 0) || to > width || count > width)
            {
                return -1;
            }

            if (to < width)
            {
                changes[count++] = to;
            }

            a0 = to;
            white = !white;
        }

        return count;
    }

    /// <summary>Bits most significant first, read through a 32-bit window; bits past the end read as zeros.</summary>
    private ref struct BitReader(ReadOnlySpan<byte> data, long position = 0)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private long _position = position;

        public readonly long Left => (_data.Length * 8L) - _position;

        public readonly long Position => _position;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int Peek(int count)
        {
            int at = (int)(_position >> 3);
            uint window;
            if (at + 4 <= _data.Length)
            {
                window = BinaryPrimitives.ReadUInt32BigEndian(_data[at..]);
            }
            else
            {
                window = 0;
                for (int i = 0; i < 4; i++)
                {
                    window = (window << 8) | (at + i < _data.Length ? _data[at + i] : 0u);
                }
            }

            return (int)((window << (int)(_position & 7)) >> (32 - count));
        }

        public int Read(int count)
        {
            int value = Peek(count);
            Skip(count);
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Skip(int count) => _position = Math.Min(_position + count, _data.Length * 8L);

        public void Align(int bits) => _position = Math.Min((_position + bits - 1) / bits * bits, _data.Length * 8L);

        public void Seek(long position) => _position = Math.Min(position, _data.Length * 8L);

        /// <summary>Whether every bit left is 0.</summary>
        public readonly bool OnlyZeros()
        {
            int at = (int)(_position >> 3);
            if (at >= _data.Length)
            {
                return true;
            }

            return (_data[at] & (0xFF >> (int)(_position & 7))) == 0 && !_data[(at + 1)..].ContainsAnyExcept((byte)0);
        }
    }
}
