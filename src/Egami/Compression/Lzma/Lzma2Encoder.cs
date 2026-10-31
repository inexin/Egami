using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Compression.Lzma;

/// <summary>
/// An LZMA2 encoder with XZ Utils' presets: presets 0 to 3 pick each match greedily with a look one byte ahead (liblzma's fast
/// mode, on hash chains), 4 to 9 find the cheapest coding of each stretch by its price in bits (normal mode, on binary trees).
/// The output is LZMA2 chunks of at most 2 MB of input and 64 KB of output; a chunk that does not shrink goes out uncompressed.
/// </summary>
/// <remarks>
/// The probabilities and their layout are those of <see cref="Lzma2Decoder"/>, so each symbol is coded as that decoder reads it.
/// Prices are in sixteenths of a bit.
/// </remarks>
internal sealed class Lzma2Encoder
{
    private const int States = 12;
    private const int PositionBitsMax = 4;
    private const int Literals = 0x300;
    private const int IsMatch = 0;
    private const int IsRep = IsMatch + (States << PositionBitsMax);
    private const int IsRepG0 = IsRep + States;
    private const int IsRepG1 = IsRepG0 + States;
    private const int IsRepG2 = IsRepG1 + States;
    private const int IsRep0Long = IsRepG2 + States;
    private const int PosSlot = IsRep0Long + (States << PositionBitsMax);
    private const int SpecPos = PosSlot + (4 << 6);
    private const int Align = SpecPos + 115;
    private const int LenCoder = Align + 16;
    private const int RepLenCoder = LenCoder + 514;
    private const int Literal = RepLenCoder + 514;
    private const int LenChoice2 = 1;
    private const int LenLow = 2;
    private const int LenMid = LenLow + (16 << 3);
    private const int LenHigh = LenMid + (16 << 3);

    private const int Lc = 3;
    private const int Pb = 2;
    private const int PositionMask = (1 << Pb) - 1;
    private const int MaxLength = LzmaMatchFinder.MaxLength;
    private const int ChunkInput = (1 << 21) - MaxLength;
    private const int ChunkOutput = 1 << 16;
    private const int Infinite = int.MaxValue / 4;
    private const int Window = 4096;

    private static readonly int[] BitPrices = BuildBitPrices();

    private readonly ushort[] _p = new ushort[Literal + (Literals << Lc)];
    private readonly byte[] _chunk = new byte[ChunkOutput + 1024];
    private readonly LzmaMatchFinder _finder = new();
    private readonly int[] _lenPrices = new int[16 * (MaxLength - 1)];
    private readonly int[] _repLenPrices = new int[16 * (MaxLength - 1)];
    private readonly int[] _slotPrices = new int[4 * 64];
    private readonly int[] _distancePrices = new int[4 * 128];
    private readonly int[] _alignPrices = new int[16];
    private readonly Node[] _nodes = new Node[Window + MaxLength + 2];
    private readonly (int Length, int Back)[] _steps = new (int, int)[Window + MaxLength + 2];
    private readonly int[] _nextLengths = new int[MaxLength + 1];
    private readonly int[] _nextDistances = new int[MaxLength + 1];

    // The range encoder.
    private ulong _low;
    private uint _range;
    private byte _cache;
    private long _cacheSize;
    private int _out;

    // The LZMA state, as the decoder will have it.
    private uint _state;
    private uint _rep0;
    private uint _rep1;
    private uint _rep2;
    private uint _rep3;

    // The parser.
    private int _finderPos;
    private int _nextPos = -1;
    private int _nextCount;
    private int _stepCount;
    private int _stepAt;
    private int _sincePrices;
    private bool _normal;
    private int _nice;

    /// <summary>The most bytes <see cref="Encode"/> writes for <paramref name="length"/> bytes of input.</summary>
    public static int MaxEncodedLength(int length) => length + (3 * ((length / ChunkOutput) + 1)) + 6;

    /// <summary>The LZMA2 dictionary size property for a dictionary of at least <paramref name="size"/> bytes.</summary>
    public static int DictionaryProperty(int size)
    {
        for (int p = 0; p < 40; p++)
        {
            if ((long)(2 | (p & 1)) << ((p / 2) + 11) >= size)
            {
                return p;
            }
        }

        return 40;
    }

    /// <summary>Encodes <paramref name="data"/> as LZMA2 into <paramref name="output"/>; returns the bytes written.</summary>
    public int Encode(ReadOnlySpan<byte> data, Span<byte> output, int dictionary, bool normal, bool tree, bool three, int nice, int depth)
    {
        _normal = normal;
        _nice = Math.Min(nice, MaxLength);
        _finder.Reset(data.Length, dictionary, tree, three, _nice, depth);
        _finderPos = 0;
        _nextPos = -1;
        _stepCount = _stepAt = 0;
        ResetState();
        int at = 0;
        int pos = 0;
        bool first = true;
        bool needProperties = true;
        bool needStateReset = false;
        while (pos < data.Length)
        {
            int start = pos;
            StartRange();
            while (pos < data.Length && pos - start < ChunkInput && _out + _cacheSize + 64 < ChunkOutput)
            {
                (int length, int back) = Next(data, pos);
                Code(data, pos, length, back);
                pos += length;
            }

            FlushRange();
            int unpacked = pos - start;
            int packed = _out;
            if (packed >= unpacked)
            {
                // No gain: the input goes out as it is, and the next LZMA chunk starts from fresh state.
                for (int piece = start; piece < pos; piece += ChunkOutput)
                {
                    int size = Math.Min(ChunkOutput, pos - piece);
                    output[at++] = (byte)(first ? 1 : 2);
                    output[at++] = (byte)((size - 1) >> 8);
                    output[at++] = (byte)(size - 1);
                    data.Slice(piece, size).CopyTo(output[at..]);
                    at += size;
                    first = false;
                }

                ResetState();
                _stepCount = _stepAt = 0;
                needStateReset = true;
                continue;
            }

            int reset = first ? 3 : needProperties ? 2 : needStateReset ? 1 : 0;
            output[at++] = (byte)(0x80 | (reset << 5) | ((unpacked - 1) >> 16));
            output[at++] = (byte)((unpacked - 1) >> 8);
            output[at++] = (byte)(unpacked - 1);
            output[at++] = (byte)((packed - 1) >> 8);
            output[at++] = (byte)(packed - 1);
            if (reset >= 2)
            {
                output[at++] = (byte)((((Pb * 5) + 0) * 9) + Lc);
            }

            _chunk.AsSpan(0, packed).CopyTo(output[at..]);
            at += packed;
            first = false;
            needProperties = false;
            needStateReset = false;
        }

        output[at++] = 0;
        return at;
    }

    private void ResetState()
    {
        _p.AsSpan().Fill(1024);
        _state = 0;
        _rep0 = _rep1 = _rep2 = _rep3 = 0;
        _sincePrices = int.MaxValue;
    }

    private static int[] BuildBitPrices()
    {
        // The price of a bit coded with a probability of (i + 0.5) / 128 for it, in sixteenths of a bit.
        int[] prices = new int[128];
        for (int i = 0; i < 128; i++)
        {
            prices[i] = (int)Math.Round(-Math.Log2(((i * 16) + 8) / 2048.0) * 16);
        }

        return prices;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Price(uint probability, uint bit) => BitPrices[(bit == 0 ? probability : 2048 - probability) >> 4];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Price(int index, uint bit) => Price((uint)_p[index], bit);

    // ---- The range encoder ----------------------------------------------------------------------------------------------------

    private void StartRange()
    {
        _low = 0;
        _range = uint.MaxValue;
        _cache = 0;
        _cacheSize = 1;
        _out = 0;
    }

    private void ShiftLow()
    {
        if ((uint)_low < 0xFF000000u || (_low >> 32) != 0)
        {
            byte carry = (byte)(_low >> 32);
            byte temp = _cache;
            do
            {
                _chunk[_out++] = (byte)(temp + carry);
                temp = 0xFF;
            }
            while (--_cacheSize != 0);

            _cache = (byte)((uint)_low >> 24);
        }

        _cacheSize++;
        _low = (uint)_low << 8;
    }

    private void FlushRange()
    {
        for (int i = 0; i < 5; i++)
        {
            ShiftLow();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Bit(int index, uint bit)
    {
        ref ushort probability = ref _p[index];
        uint p = probability;
        uint bound = (_range >> 11) * p;
        if (bit == 0)
        {
            _range = bound;
            probability = (ushort)(p + ((2048 - p) >> 5));
        }
        else
        {
            _low += bound;
            _range -= bound;
            probability = (ushort)(p - (p >> 5));
        }

        while (_range < 1u << 24)
        {
            _range <<= 8;
            ShiftLow();
        }
    }

    private void Tree(int index, int bits, uint value)
    {
        uint m = 1;
        for (int i = bits - 1; i >= 0; i--)
        {
            uint bit = (value >> i) & 1;
            Bit(index + (int)m, bit);
            m = (m << 1) | bit;
        }
    }

    private void ReverseTree(int index, int bits, uint value)
    {
        uint m = 1;
        for (int i = 0; i < bits; i++)
        {
            uint bit = (value >> i) & 1;
            Bit(index + (int)m, bit);
            m = (m << 1) | bit;
        }
    }

    // ---- Coding symbols ---------------------------------------------------------------------------------------------------------

    /// <summary>Codes one step: a literal (back -1), a repeat match (back 0 to 3; back 0 of length 1 is the short repeat) or a match.</summary>
    private void Code(ReadOnlySpan<byte> data, int pos, int length, int back)
    {
        int positionState = pos & PositionMask;
        int stateIndex = (int)(_state << PositionBitsMax) + positionState;
        _sincePrices++;
        if (back < 0)
        {
            Bit(IsMatch + stateIndex, 0);
            int previous = pos > 0 ? data[pos - 1] : 0;
            int literal = Literal + (Literals * (previous >> (8 - Lc)));
            uint symbol = 1;
            uint value = data[pos];
            if (_state >= 7)
            {
                uint matchByte = data[pos - (int)_rep0 - 1];
                bool same = true;
                for (int i = 7; i >= 0; i--)
                {
                    uint bit = (value >> i) & 1;
                    if (same)
                    {
                        uint matchBit = (matchByte >> i) & 1;
                        Bit(literal + (int)(((1 + matchBit) << 8) + symbol), bit);
                        same = matchBit == bit;
                    }
                    else
                    {
                        Bit(literal + (int)symbol, bit);
                    }

                    symbol = (symbol << 1) | bit;
                }
            }
            else
            {
                Tree(literal, 8, value);
            }

            _state = _state < 4 ? 0 : _state < 10 ? _state - 3 : _state - 6;
            return;
        }

        Bit(IsMatch + stateIndex, 1);
        if (back < 4)
        {
            Bit(IsRep + (int)_state, 1);
            if (back == 0)
            {
                Bit(IsRepG0 + (int)_state, 0);
                Bit(IsRep0Long + stateIndex, length == 1 ? 0u : 1u);
                if (length == 1)
                {
                    _state = _state < 7 ? 9u : 11u;
                    return;
                }
            }
            else
            {
                Bit(IsRepG0 + (int)_state, 1);
                uint distance;
                if (back == 1)
                {
                    Bit(IsRepG1 + (int)_state, 0);
                    distance = _rep1;
                }
                else
                {
                    Bit(IsRepG1 + (int)_state, 1);
                    Bit(IsRepG2 + (int)_state, back == 2 ? 0u : 1u);
                    if (back == 3)
                    {
                        distance = _rep3;
                        _rep3 = _rep2;
                    }
                    else
                    {
                        distance = _rep2;
                    }

                    _rep2 = _rep1;
                }

                _rep1 = _rep0;
                _rep0 = distance;
            }

            Length(RepLenCoder, length - 2, positionState);
            _state = _state < 7 ? 8u : 11u;
            return;
        }

        Bit(IsRep + (int)_state, 0);
        Length(LenCoder, length - 2, positionState);
        uint dist = (uint)(back - 4);
        int lengthState = Math.Min(length - 2, 3);
        int slot = Slot(dist);
        Tree(PosSlot + (lengthState << 6), 6, (uint)slot);
        if (slot >= 4)
        {
            int directBits = (slot >> 1) - 1;
            uint baseDistance = (2u | ((uint)slot & 1)) << directBits;
            uint reduced = dist - baseDistance;
            if (slot < 14)
            {
                ReverseTree(SpecPos + (int)baseDistance - slot, directBits, reduced);
            }
            else
            {
                for (int i = directBits - 5; i >= 0; i--)
                {
                    _range >>= 1;
                    _low += _range & (0u - ((reduced >> (i + 4)) & 1));
                    while (_range < 1u << 24)
                    {
                        _range <<= 8;
                        ShiftLow();
                    }
                }

                ReverseTree(Align, 4, reduced & 15);
            }
        }

        _rep3 = _rep2;
        _rep2 = _rep1;
        _rep1 = _rep0;
        _rep0 = dist;
        _state = _state < 7 ? 7u : 10u;
    }

    private void Length(int coder, int length, int positionState)
    {
        if (length < 8)
        {
            Bit(coder, 0);
            Tree(coder + LenLow + (positionState << 3), 3, (uint)length);
        }
        else if (length < 16)
        {
            Bit(coder, 1);
            Bit(coder + LenChoice2, 0);
            Tree(coder + LenMid + (positionState << 3), 3, (uint)(length - 8));
        }
        else
        {
            Bit(coder, 1);
            Bit(coder + LenChoice2, 1);
            Tree(coder + LenHigh, 8, (uint)(length - 16));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Slot(uint distance)
    {
        if (distance < 4)
        {
            return (int)distance;
        }

        int high = 31 - System.Numerics.BitOperations.LeadingZeroCount(distance);
        return (high << 1) | (int)((distance >> (high - 1)) & 1);
    }

    // ---- Prices ---------------------------------------------------------------------------------------------------------------

    private void UpdatePrices()
    {
        _sincePrices = 0;
        for (int ps = 0; ps <= PositionMask; ps++)
        {
            for (int len = 0; len < MaxLength - 1; len++)
            {
                _lenPrices[(ps * (MaxLength - 1)) + len] = LengthPrice(LenCoder, len, ps);
                _repLenPrices[(ps * (MaxLength - 1)) + len] = LengthPrice(RepLenCoder, len, ps);
            }
        }

        for (int ls = 0; ls < 4; ls++)
        {
            for (int slot = 0; slot < 64; slot++)
            {
                int price = TreePrice(PosSlot + (ls << 6), 6, (uint)slot);
                if (slot >= 14)
                {
                    price += (((slot >> 1) - 1) - 4) * 16;
                }

                _slotPrices[(ls << 6) + slot] = price;
            }

            for (uint d = 0; d < 128; d++)
            {
                int slot = Slot(d);
                int price = _slotPrices[(ls << 6) + slot];
                if (slot >= 4)
                {
                    int directBits = (slot >> 1) - 1;
                    uint baseDistance = (2u | ((uint)slot & 1)) << directBits;
                    price += ReverseTreePrice(SpecPos + (int)baseDistance - slot, directBits, d - baseDistance);
                }

                _distancePrices[(ls << 7) + (int)d] = price;
            }
        }

        for (uint i = 0; i < 16; i++)
        {
            _alignPrices[i] = ReverseTreePrice(Align, 4, i);
        }
    }

    private int LengthPrice(int coder, int length, int positionState)
    {
        if (length < 8)
        {
            return Price(coder, 0) + TreePrice(coder + LenLow + (positionState << 3), 3, (uint)length);
        }

        if (length < 16)
        {
            return Price(coder, 1) + Price(coder + LenChoice2, 0) + TreePrice(coder + LenMid + (positionState << 3), 3, (uint)(length - 8));
        }

        return Price(coder, 1) + Price(coder + LenChoice2, 1) + TreePrice(coder + LenHigh, 8, (uint)(length - 16));
    }

    private int TreePrice(int index, int bits, uint value)
    {
        int price = 0;
        uint m = 1;
        for (int i = bits - 1; i >= 0; i--)
        {
            uint bit = (value >> i) & 1;
            price += Price(index + (int)m, bit);
            m = (m << 1) | bit;
        }

        return price;
    }

    private int ReverseTreePrice(int index, int bits, uint value)
    {
        int price = 0;
        uint m = 1;
        for (int i = 0; i < bits; i++)
        {
            uint bit = (value >> i) & 1;
            price += Price(index + (int)m, bit);
            m = (m << 1) | bit;
        }

        return price;
    }

    private int LiteralPrice(ReadOnlySpan<byte> data, int pos, uint state, uint rep0)
    {
        int previous = pos > 0 ? data[pos - 1] : 0;
        int literal = Literal + (Literals * (previous >> (8 - Lc)));
        uint value = data[pos];
        uint symbol = 1;
        int price = 0;
        if (state >= 7)
        {
            uint matchByte = data[pos - (int)rep0 - 1];
            bool same = true;
            for (int i = 7; i >= 0; i--)
            {
                uint bit = (value >> i) & 1;
                if (same)
                {
                    uint matchBit = (matchByte >> i) & 1;
                    price += Price(literal + (int)(((1 + matchBit) << 8) + symbol), bit);
                    same = matchBit == bit;
                }
                else
                {
                    price += Price(literal + (int)symbol, bit);
                }

                symbol = (symbol << 1) | bit;
            }

            return price;
        }

        return TreePrice(literal, 8, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int MatchPrice(uint distance, int length, int positionState)
    {
        int lengthState = Math.Min(length - 2, 3);
        int price = _lenPrices[(positionState * (MaxLength - 1)) + length - 2];
        if (distance < 128)
        {
            return price + _distancePrices[(lengthState << 7) + (int)distance];
        }

        return price + _slotPrices[(lengthState << 6) + Slot(distance)] + _alignPrices[distance & 15];
    }

    private int RepPrice(int index, uint state, int positionState)
    {
        if (index == 0)
        {
            return Price(IsRepG0 + (int)state, 0) + Price(IsRep0Long + (int)(state << PositionBitsMax) + positionState, 1);
        }

        int price = Price(IsRepG0 + (int)state, 1);
        if (index == 1)
        {
            return price + Price(IsRepG1 + (int)state, 0);
        }

        return price + Price(IsRepG1 + (int)state, 1) + Price(IsRepG2 + (int)state, index == 2 ? 0u : 1u);
    }

    private int ShortRepPrice(uint state, int positionState) =>
        Price(IsRepG0 + (int)state, 0) + Price(IsRep0Long + (int)(state << PositionBitsMax) + positionState, 0);

    // ---- Parsing ----------------------------------------------------------------------------------------------------------------

    /// <summary>The next step from <paramref name="pos"/>: its length and its back (-1 literal, 0 to 3 a repeat, 4 and up a match).</summary>
    private (int Length, int Back) Next(ReadOnlySpan<byte> data, int pos)
    {
        if (_stepAt < _stepCount)
        {
            (int Length, int Back) step = _steps[_stepAt++];
            Insert(data, pos + step.Length);
            return step;
        }

        if (pos < _finderPos && _nextPos != pos)
        {
            // Positions the finder has seen already, after an uncompressed chunk dropped the steps planned for them.
            return (1, -1);
        }

        if (_normal && _sincePrices >= 256)
        {
            UpdatePrices();
        }

        (int length, int back) = _normal ? Optimum(data, pos) : Fast(data, pos);
        Insert(data, pos + length);
        return (length, back);
    }

    /// <summary>Inserts every position before <paramref name="end"/> that the finder has not seen.</summary>
    private void Insert(ReadOnlySpan<byte> data, int end)
    {
        for (; _finderPos < end; _finderPos++)
        {
            _finder.Skip(data, _finderPos, data.Length);
        }
    }

    /// <summary>Finds the matches at <paramref name="pos"/>, which must be the finder's next position.</summary>
    private int Matches(ReadOnlySpan<byte> data, int pos)
    {
        Insert(data, pos);
        _finderPos = pos + 1;
        return _finder.Find(data, pos, data.Length);
    }

    private static int RepLength(ReadOnlySpan<byte> data, int pos, uint rep, int available)
    {
        int from = pos - (int)rep - 1;
        if (from < 0 || data[pos] != data[from] || data[pos + 1] != data[from + 1])
        {
            return 0;
        }

        return LzmaMatchFinder.Count(ref MemoryMarshal.GetReference(data), pos, from, available);
    }

    private uint Rep(int index) => index switch
    {
        0 => _rep0,
        1 => _rep1,
        2 => _rep2,
        _ => _rep3,
    };

    private static bool ChangePair(int small, int big) => (big >> 7) > small;

    /// <summary>liblzma's fast mode: the longest match, unless a repeat match is nearly as long or the next byte starts better.</summary>
    private (int Length, int Back) Fast(ReadOnlySpan<byte> data, int pos)
    {
        int available = Math.Min(data.Length - pos, MaxLength);
        int count;
        int[] lengths;
        int[] distances;
        if (_nextPos == pos)
        {
            count = _nextCount;
            lengths = _nextLengths;
            distances = _nextDistances;
        }
        else
        {
            count = Matches(data, pos);
            lengths = _finder.Lengths;
            distances = _finder.Distances;
        }

        _nextPos = -1;
        if (available < 2)
        {
            return (1, -1);
        }

        int repLength = 0;
        int repIndex = 0;
        for (int i = 0; i < 4; i++)
        {
            int length = RepLength(data, pos, Rep(i), available);
            if (length >= _nice)
            {
                return (length, i);
            }

            if (length > repLength)
            {
                repLength = length;
                repIndex = i;
            }
        }

        int mainLength = count > 0 ? lengths[count - 1] : 0;
        int mainBack = count > 0 ? distances[count - 1] : 0;
        if (mainLength >= _nice)
        {
            return (mainLength, mainBack + 4);
        }

        while (count > 1 && mainLength == lengths[count - 2] + 1 && !ChangePair(distances[count - 2], mainBack))
        {
            count--;
            mainLength = lengths[count - 1];
            mainBack = distances[count - 1];
        }

        if (mainLength == 2 && mainBack >= 0x80)
        {
            mainLength = 1;
        }

        if (repLength >= 2 && (repLength + 1 >= mainLength || (repLength + 2 >= mainLength && mainBack > 1 << 9)
            || (repLength + 3 >= mainLength && mainBack > 1 << 15)))
        {
            return (repLength, repIndex);
        }

        if (mainLength < 2 || available <= 2)
        {
            return (1, -1);
        }

        // A look at the next byte: a better match there makes this one a literal.
        _nextCount = Matches(data, pos + 1);
        _nextPos = pos + 1;
        Array.Copy(_finder.Lengths, _nextLengths, _nextCount);
        Array.Copy(_finder.Distances, _nextDistances, _nextCount);
        if (_nextCount > 0)
        {
            int nextLength = _nextLengths[_nextCount - 1];
            int nextBack = _nextDistances[_nextCount - 1];
            if ((nextLength >= mainLength && nextBack < mainBack) || (nextLength == mainLength + 1 && !ChangePair(mainBack, nextBack))
                || nextLength > mainLength + 1 || (nextLength + 1 >= mainLength && mainLength >= 3 && ChangePair(nextBack, mainBack)))
            {
                return (1, -1);
            }
        }

        int limit = Math.Max(mainLength - 1, 2);
        int nextAvailable = Math.Min(data.Length - pos - 1, MaxLength);
        for (int i = 0; i < 4 && nextAvailable >= 2; i++)
        {
            if (RepLength(data, pos + 1, Rep(i), nextAvailable) >= limit)
            {
                return (1, -1);
            }
        }

        _nextPos = -1;
        return (mainLength, mainBack + 4);
    }

    /// <summary>
    /// liblzma's normal mode, in its own words: from <paramref name="pos"/>, the cheapest way to code each position up to a
    /// window ahead, by prices, as a shortest path; the steps of the path to the window's end are queued and the first returned.
    /// </summary>
    private (int Length, int Back) Optimum(ReadOnlySpan<byte> data, int pos)
    {
        int available = Math.Min(data.Length - pos, MaxLength);
        int count = Matches(data, pos);
        if (available < 2)
        {
            return (1, -1);
        }

        Span<int> repLengths = stackalloc int[4];
        int bestRep = 0;
        for (int i = 0; i < 4; i++)
        {
            repLengths[i] = RepLength(data, pos, Rep(i), available);
            if (repLengths[i] > repLengths[bestRep])
            {
                bestRep = i;
            }
        }

        if (repLengths[bestRep] >= _nice)
        {
            return (repLengths[bestRep], bestRep);
        }

        int mainLength = count > 0 ? _finder.Lengths[count - 1] : 0;
        if (mainLength >= _nice)
        {
            return (mainLength, _finder.Distances[count - 1] + 4);
        }

        Node[] nodes = _nodes;
        nodes[0] = new Node { Price = 0, State = _state, Rep0 = _rep0, Rep1 = _rep1, Rep2 = _rep2, Rep3 = _rep3 };
        int end = Math.Max(1, Math.Max(mainLength, repLengths[bestRep]));
        for (int i = 1; i <= end; i++)
        {
            nodes[i].Price = Infinite;
        }

        Relax(data, pos, 0, count, repLengths, ref end);
        int cur = 0;
        int tail = -1;
        while (true)
        {
            cur++;
            if (cur >= end || cur >= Window)
            {
                break;
            }

            ref Node node = ref nodes[cur];
            Follow(ref node, nodes[node.Previous]);
            int at = pos + cur;
            int found = Matches(data, at);
            int left = Math.Min(data.Length - at, MaxLength);
            if (found > 0 && _finder.Lengths[found - 1] >= _nice)
            {
                // A long match here ends the window: take the path to here, then the match.
                tail = found;
                break;
            }

            for (int i = 0; i < 4; i++)
            {
                repLengths[i] = left >= 2 ? RepLength(data, at, node.Rep(i), left) : 0;
            }

            Relax(data, at, cur, left >= 2 ? found : 0, repLengths, ref end);
        }

        // The path back from where the window stopped.
        int last = cur;
        int n = 0;
        if (tail >= 0)
        {
            _steps[n++] = (_finder.Lengths[tail - 1], _finder.Distances[tail - 1] + 4);
        }

        for (int i = last; i > 0; i = nodes[i].Previous)
        {
            _steps[n++] = (i - nodes[i].Previous, nodes[i].Back);
        }

        Array.Reverse(_steps, 0, n);
        _stepCount = n;
        _stepAt = 1;
        return _steps[0];
    }

    /// <summary>Works out a node's state and repeat distances from the node before it and the step between them.</summary>
    private static void Follow(ref Node node, in Node previous)
    {
        uint state = previous.State;
        node.Rep0 = previous.Rep0;
        node.Rep1 = previous.Rep1;
        node.Rep2 = previous.Rep2;
        node.Rep3 = previous.Rep3;
        int back = node.Back;
        if (back < 0)
        {
            node.State = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
        }
        else if (back < 4)
        {
            if (node.Length == 1)
            {
                node.State = state < 7 ? 9u : 11u;
            }
            else
            {
                node.State = state < 7 ? 8u : 11u;
                uint distance = previous.Rep(back);
                if (back >= 1)
                {
                    if (back == 3)
                    {
                        node.Rep3 = previous.Rep2;
                    }

                    if (back >= 2)
                    {
                        node.Rep2 = previous.Rep1;
                    }

                    node.Rep1 = previous.Rep0;
                    node.Rep0 = distance;
                }
            }
        }
        else
        {
            node.State = state < 7 ? 7u : 10u;
            node.Rep3 = previous.Rep2;
            node.Rep2 = previous.Rep1;
            node.Rep1 = previous.Rep0;
            node.Rep0 = (uint)(back - 4);
        }
    }

    /// <summary>Offers every way to leave node <paramref name="cur"/> at <paramref name="at"/> to the nodes after it.</summary>
    private void Relax(ReadOnlySpan<byte> data, int at, int cur, int count, Span<int> repLengths, ref int end)
    {
        Node[] nodes = _nodes;
        ref Node node = ref nodes[cur];
        uint state = node.State;
        int positionState = at & PositionMask;
        int stateIndex = (int)(state << PositionBitsMax) + positionState;
        int matchPrice = node.Price + Price(IsMatch + stateIndex, 1);
        int repPrice = matchPrice + Price(IsRep + (int)state, 1);
        Grow(ref end, cur + 1);

        // A literal, or a short repeat of one byte.
        int literal = node.Price + Price(IsMatch + stateIndex, 0) + LiteralPrice(data, at, state, node.Rep0);
        Offer(cur + 1, literal, cur, -1, 1);
        int from = at - (int)node.Rep0 - 1;
        if (from >= 0 && data[from] == data[at])
        {
            Offer(cur + 1, repPrice + ShortRepPrice(state, positionState), cur, 0, 1);
        }

        for (int i = 0; i < 4; i++)
        {
            int length = repLengths[i];
            if (length < 2)
            {
                continue;
            }

            int price = repPrice + RepPrice(i, state, positionState);
            Grow(ref end, cur + length);
            for (int l = 2; l <= length; l++)
            {
                Offer(cur + l, price + _repLenPrices[(positionState * (MaxLength - 1)) + l - 2], cur, i, l);
            }
        }

        int normal = matchPrice + Price(IsRep + (int)state, 0);
        int start = Math.Max(2, repLengths[0] + 1);
        int[] lengths = _finder.Lengths;
        int[] distances = _finder.Distances;
        for (int k = 0, l = 2; k < count; k++)
        {
            int length = lengths[k];
            uint distance = (uint)distances[k];
            Grow(ref end, cur + length);
            for (l = Math.Max(l, start); l <= length; l++)
            {
                Offer(cur + l, normal + MatchPrice(distance, l, positionState), cur, (int)distance + 4, l);
            }
        }
    }

    private void Grow(ref int end, int to)
    {
        for (; end < to; end++)
        {
            _nodes[end + 1].Price = Infinite;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Offer(int to, int price, int from, int back, int length)
    {
        ref Node target = ref _nodes[to];
        if (price < target.Price)
        {
            target.Price = price;
            target.Previous = from;
            target.Back = back;
            target.Length = length;
        }
    }

    /// <summary>One position of the window: its cheapest price, how it is reached, and the state there.</summary>
    private struct Node
    {
        public int Price;
        public int Previous;
        public int Back;
        public int Length;
        public uint State;
        public uint Rep0;
        public uint Rep1;
        public uint Rep2;
        public uint Rep3;

        public readonly uint Rep(int index) => index switch
        {
            0 => Rep0,
            1 => Rep1,
            2 => Rep2,
            _ => Rep3,
        };
    }
}
