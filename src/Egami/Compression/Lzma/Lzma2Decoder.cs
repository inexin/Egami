using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Compression.Lzma;

/// <summary>
/// LZMA2 (the xz file format's filter 0x21) and the LZMA it carries, from the LZMA SDK's specification (lzma-specification.txt,
/// public domain): chunks of LZMA data, each with its own range coder and optionally new properties, a state reset or a
/// dictionary reset, and uncompressed chunks. The dictionary is the output itself, so matches copy straight from it.
/// </summary>
internal sealed class Lzma2Decoder
{
    private const int States = 12;
    private const int PositionBitsMax = 4;
    private const int Literals = 0x300;

    // Where each group of probabilities starts in one array.
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

    // A length coder: two choice bits, then 3-bit trees per position state, low and middle, and one 8-bit tree.
    private const int LenChoice2 = 1;
    private const int LenLow = 2;
    private const int LenMid = LenLow + (16 << 3);
    private const int LenHigh = LenMid + (16 << 3);

    private readonly ushort[] _probabilities = new ushort[Literal + (Literals << 4)];

    private int _lc;
    private int _lp;
    private int _pb;
    private bool _hasProperties;
    private uint _state;
    private uint _rep0;
    private uint _rep1;
    private uint _rep2;
    private uint _rep3;

    /// <summary>
    /// Decodes the LZMA2 data at the start of <paramref name="input"/> into <paramref name="output"/> from
    /// <paramref name="start"/>; returns the input bytes it took, and the new end of the output in <paramref name="end"/>.
    /// </summary>
    public int Decode(ReadOnlySpan<byte> input, Span<byte> output, int start, out int end)
    {
        int at = 0;
        int written = start;
        int dictionaryStart = start;
        bool needDictionaryReset = true;
        _hasProperties = false;
        while (true)
        {
            if (at >= input.Length)
            {
                throw XzDecoder.Corrupt("LZMA2 data ends without its end marker.");
            }

            int control = input[at++];
            if (control == 0)
            {
                end = written;
                return at;
            }

            if (control < 0x80)
            {
                // An uncompressed chunk, 1 with a dictionary reset.
                if (control > 2)
                {
                    throw XzDecoder.Corrupt($"An LZMA2 chunk has the invalid control byte {control}.");
                }

                if (input.Length - at < 2)
                {
                    throw XzDecoder.Corrupt("An LZMA2 chunk header is cut short.");
                }

                int size = ((input[at] << 8) | input[at + 1]) + 1;
                at += 2;
                if (control == 1)
                {
                    dictionaryStart = written;
                    needDictionaryReset = false;
                }
                else if (needDictionaryReset)
                {
                    throw XzDecoder.Corrupt("The first LZMA2 chunk does not reset the dictionary.");
                }

                if (input.Length - at < size || output.Length - written < size)
                {
                    throw XzDecoder.Corrupt(input.Length - at < size ? "An uncompressed LZMA2 chunk is cut short." : "The data holds more than the output has room for.");
                }

                input.Slice(at, size).CopyTo(output[written..]);
                at += size;
                written += size;
                continue;
            }

            if (input.Length - at < 4)
            {
                throw XzDecoder.Corrupt("An LZMA2 chunk header is cut short.");
            }

            int unpacked = ((control & 0x1F) << 16) + (input[at] << 8) + input[at + 1] + 1;
            int packed = (input[at + 2] << 8) + input[at + 3] + 1;
            at += 4;
            int reset = (control >> 5) & 3;
            if (reset == 3)
            {
                dictionaryStart = written;
                needDictionaryReset = false;
            }
            else if (needDictionaryReset)
            {
                throw XzDecoder.Corrupt("The first LZMA2 chunk does not reset the dictionary.");
            }

            if (reset >= 2)
            {
                if (at >= input.Length)
                {
                    throw XzDecoder.Corrupt("An LZMA2 chunk header is cut short.");
                }

                SetProperties(input[at++]);
            }
            else if (!_hasProperties)
            {
                throw XzDecoder.Corrupt("An LZMA2 chunk has no properties before it.");
            }

            if (reset >= 1)
            {
                ResetState();
            }

            if (input.Length - at < packed)
            {
                throw XzDecoder.Corrupt("An LZMA2 chunk runs past the end of the data.");
            }

            if (output.Length - written < unpacked)
            {
                throw XzDecoder.Corrupt("The data holds more than the output has room for.");
            }

            DecodeChunk(input.Slice(at, packed), output, written, written + unpacked, dictionaryStart);
            at += packed;
            written += unpacked;
        }
    }

    private void SetProperties(int properties)
    {
        if (properties >= 9 * 5 * 5)
        {
            throw XzDecoder.Corrupt("LZMA properties are out of range.");
        }

        _lc = properties % 9;
        properties /= 9;
        _lp = properties % 5;
        _pb = properties / 5;
        if (_lc + _lp > 4)
        {
            throw XzDecoder.Corrupt("LZMA2 properties have lc + lp over 4.");
        }

        _hasProperties = true;
    }

    private void ResetState()
    {
        _probabilities.AsSpan(0, Literal + (Literals << (_lc + _lp))).Fill(1024);
        _state = 0;
        _rep0 = _rep1 = _rep2 = _rep3 = 0;
    }

    /// <summary>Decodes one LZMA chunk from <paramref name="written"/> to <paramref name="stop"/>.</summary>
    private void DecodeChunk(ReadOnlySpan<byte> chunk, Span<byte> output, int written, int stop, int dictionaryStart)
    {
        if (chunk.Length < 5 || chunk[0] != 0)
        {
            throw XzDecoder.Corrupt("An LZMA chunk's range coder does not start right.");
        }

        ref byte input = ref MemoryMarshal.GetReference(chunk);
        int inputEnd = chunk.Length;
        uint code = ((uint)chunk[1] << 24) | ((uint)chunk[2] << 16) | ((uint)chunk[3] << 8) | chunk[4];
        int inAt = 5;
        uint range = uint.MaxValue;
        ref ushort p = ref MemoryMarshal.GetArrayDataReference(_probabilities);
        ref byte o = ref MemoryMarshal.GetReference(output);
        uint state = _state;
        uint rep0 = _rep0;
        uint rep1 = _rep1;
        uint rep2 = _rep2;
        uint rep3 = _rep3;
        int lc = _lc;
        int literalPositionMask = (1 << _lp) - 1;
        int positionMask = (1 << _pb) - 1;
        int pos = written;
        int outputLength = output.Length;
        while (pos < stop)
        {
            int relative = pos - dictionaryStart;
            int positionState = relative & positionMask;
            if (Bit(ref Unsafe.Add(ref p, IsMatch + (int)(state << PositionBitsMax) + positionState), ref range, ref code, ref input, ref inAt, inputEnd) == 0)
            {
                // A literal, coded against the byte at the last match distance after a match.
                int previous = relative > 0 ? Unsafe.Add(ref o, pos - 1) : 0;
                ref ushort literal = ref Unsafe.Add(ref p, Literal + (Literals * (((relative & literalPositionMask) << lc) + (previous >> (8 - lc)))));
                uint symbol = 1;
                if (state >= 7)
                {
                    if (rep0 >= (uint)relative)
                    {
                        throw XzDecoder.Corrupt("An LZMA match reaches back before the dictionary.");
                    }

                    uint matchByte = Unsafe.Add(ref o, pos - (int)rep0 - 1);
                    do
                    {
                        uint matchBit = (matchByte >> 7) & 1;
                        matchByte <<= 1;
                        uint bit = Bit(ref Unsafe.Add(ref literal, (int)(((1 + matchBit) << 8) + symbol)), ref range, ref code, ref input, ref inAt, inputEnd);
                        symbol = (symbol << 1) | bit;
                        if (matchBit != bit)
                        {
                            break;
                        }
                    }
                    while (symbol < 0x100);
                }

                while (symbol < 0x100)
                {
                    symbol = (symbol << 1) | BitWithoutBranch(ref Unsafe.Add(ref literal, (int)symbol), ref range, ref code, ref input, ref inAt, inputEnd);
                }

                Unsafe.Add(ref o, pos++) = (byte)symbol;
                state = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
                continue;
            }

            int length;
            if (Bit(ref Unsafe.Add(ref p, IsRep + (int)state), ref range, ref code, ref input, ref inAt, inputEnd) != 0)
            {
                if (relative == 0)
                {
                    throw XzDecoder.Corrupt("An LZMA repeat match comes before any data.");
                }

                if (Bit(ref Unsafe.Add(ref p, IsRepG0 + (int)state), ref range, ref code, ref input, ref inAt, inputEnd) == 0)
                {
                    if (Bit(ref Unsafe.Add(ref p, IsRep0Long + (int)(state << PositionBitsMax) + positionState), ref range, ref code, ref input, ref inAt, inputEnd) == 0)
                    {
                        // A short repeat: one byte at the last distance.
                        if (rep0 >= (uint)relative)
                        {
                            throw XzDecoder.Corrupt("An LZMA match reaches back before the dictionary.");
                        }

                        state = state < 7 ? 9u : 11u;
                        Unsafe.Add(ref o, pos) = Unsafe.Add(ref o, pos - (int)rep0 - 1);
                        pos++;
                        continue;
                    }
                }
                else
                {
                    uint distance;
                    if (Bit(ref Unsafe.Add(ref p, IsRepG1 + (int)state), ref range, ref code, ref input, ref inAt, inputEnd) == 0)
                    {
                        distance = rep1;
                    }
                    else
                    {
                        if (Bit(ref Unsafe.Add(ref p, IsRepG2 + (int)state), ref range, ref code, ref input, ref inAt, inputEnd) == 0)
                        {
                            distance = rep2;
                        }
                        else
                        {
                            distance = rep3;
                            rep3 = rep2;
                        }

                        rep2 = rep1;
                    }

                    rep1 = rep0;
                    rep0 = distance;
                }

                length = Length(ref Unsafe.Add(ref p, RepLenCoder), positionState, ref range, ref code, ref input, ref inAt, inputEnd);
                state = state < 7 ? 8u : 11u;
            }
            else
            {
                rep3 = rep2;
                rep2 = rep1;
                rep1 = rep0;
                length = Length(ref Unsafe.Add(ref p, LenCoder), positionState, ref range, ref code, ref input, ref inAt, inputEnd);
                state = state < 7 ? 7u : 10u;
                rep0 = Distance(ref p, length, ref range, ref code, ref input, ref inAt, inputEnd);
                if (rep0 == uint.MaxValue)
                {
                    throw XzDecoder.Corrupt("An LZMA2 chunk holds an end marker.");
                }
            }

            length += 2;
            if (rep0 >= (uint)relative)
            {
                throw XzDecoder.Corrupt("An LZMA match reaches back before the dictionary.");
            }

            if (length > stop - pos)
            {
                throw XzDecoder.Corrupt("An LZMA match runs past the end of its chunk.");
            }

            Copy(ref Unsafe.Add(ref o, pos), (int)rep0 + 1, length, outputLength - pos - length >= 8);
            pos += length;
        }

        if (inAt > inputEnd)
        {
            throw XzDecoder.Corrupt("An LZMA chunk reads past its end.");
        }

        if (inAt != inputEnd || code != 0)
        {
            throw XzDecoder.Corrupt("An LZMA chunk's size does not match its data.");
        }

        _state = state;
        _rep0 = rep0;
        _rep1 = rep1;
        _rep2 = rep2;
        _rep3 = rep3;
    }

    /// <summary>
    /// Copies a match <paramref name="distance"/> bytes back, which may overlap the bytes it writes; with
    /// <paramref name="roomy"/>, eight bytes at a time, writing up to seven bytes past the match, which later output replaces.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Copy(ref byte target, int distance, int length, bool roomy)
    {
        ref byte source = ref Unsafe.Subtract(ref target, distance);
        if (roomy && distance >= 8)
        {
            int i = 0;
            do
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref target, i), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, i)));
                i += 8;
            }
            while (i < length);
        }
        else if (distance == 1)
        {
            Unsafe.InitBlockUnaligned(ref target, source, (uint)length);
        }
        else
        {
            for (int i = 0; i < length; i++)
            {
                Unsafe.Add(ref target, i) = Unsafe.Add(ref source, i);
            }
        }
    }

    /// <summary>Decodes one bit with its probability, which it updates, keeping the range at 24 bits or more.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Bit(ref ushort probability, ref uint range, ref uint code, ref byte input, ref int at, int end)
    {
        uint p = probability;
        uint bound = (range >> 11) * p;
        uint bit;
        if (code < bound)
        {
            range = bound;
            probability = (ushort)(p + ((2048 - p) >> 5));
            bit = 0;
        }
        else
        {
            range -= bound;
            code -= bound;
            probability = (ushort)(p - (p >> 5));
            bit = 1;
        }

        if (range < 1u << 24)
        {
            range <<= 8;
            code = (code << 8) | (at < end ? Unsafe.Add(ref input, at) : 0u);
            at++;
        }

        return bit;
    }

    /// <summary>
    /// <see cref="Bit"/> with selects in place of a branch, for literal bits, which come out close to random and would mispredict.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint BitWithoutBranch(ref ushort probability, ref uint range, ref uint code, ref byte input, ref int at, int end)
    {
        uint p = probability;
        uint bound = (range >> 11) * p;

        // All ones when the bit is 0 (code below bound), else zero.
        uint zero = (uint)(((long)code - bound) >> 63);
        range = (bound & zero) | ((range - bound) & ~zero);
        code -= bound & ~zero;
        uint up = p + ((2048 - p) >> 5);
        uint down = p - (p >> 5);
        probability = (ushort)((up & zero) | (down & ~zero));
        if (range < 1u << 24)
        {
            range <<= 8;
            code = (code << 8) | (at < end ? Unsafe.Add(ref input, at) : 0u);
            at++;
        }

        return zero + 1;
    }

    /// <summary>Decodes <paramref name="bits"/> bits through a binary tree of probabilities, most significant first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Tree(ref ushort probabilities, int bits, ref uint range, ref uint code, ref byte input, ref int at, int end)
    {
        uint m = 1;
        for (int i = 0; i < bits; i++)
        {
            m = (m << 1) | Bit(ref Unsafe.Add(ref probabilities, (int)m), ref range, ref code, ref input, ref at, end);
        }

        return (int)m - (1 << bits);
    }

    /// <summary>Decodes <paramref name="bits"/> bits through a binary tree of probabilities, least significant first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReverseTree(ref ushort probabilities, int bits, ref uint range, ref uint code, ref byte input, ref int at, int end)
    {
        uint m = 1;
        uint symbol = 0;
        for (int i = 0; i < bits; i++)
        {
            uint bit = Bit(ref Unsafe.Add(ref probabilities, (int)m), ref range, ref code, ref input, ref at, end);
            m = (m << 1) | bit;
            symbol |= bit << i;
        }

        return symbol;
    }

    /// <summary>A match length less 2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Length(ref ushort coder, int positionState, ref uint range, ref uint code, ref byte input, ref int at, int end)
    {
        if (Bit(ref coder, ref range, ref code, ref input, ref at, end) == 0)
        {
            return Tree(ref Unsafe.Add(ref coder, LenLow + (positionState << 3)), 3, ref range, ref code, ref input, ref at, end);
        }

        if (Bit(ref Unsafe.Add(ref coder, LenChoice2), ref range, ref code, ref input, ref at, end) == 0)
        {
            return 8 + Tree(ref Unsafe.Add(ref coder, LenMid + (positionState << 3)), 3, ref range, ref code, ref input, ref at, end);
        }

        return 16 + Tree(ref Unsafe.Add(ref coder, LenHigh), 8, ref range, ref code, ref input, ref at, end);
    }

    /// <summary>A match distance less 1, from its slot and the slot's extra bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Distance(ref ushort p, int length, ref uint range, ref uint code, ref byte input, ref int at, int end)
    {
        int lengthState = Math.Min(length, 3);
        int slot = Tree(ref Unsafe.Add(ref p, PosSlot + (lengthState << 6)), 6, ref range, ref code, ref input, ref at, end);
        if (slot < 4)
        {
            return (uint)slot;
        }

        int directBits = (slot >> 1) - 1;
        uint distance = (2u | ((uint)slot & 1)) << directBits;
        if (slot < 14)
        {
            return distance + ReverseTree(ref Unsafe.Add(ref p, SpecPos + (int)distance - slot), directBits, ref range, ref code, ref input, ref at, end);
        }

        // Direct bits, then four bits through the align tree.
        uint direct = 0;
        for (int i = directBits - 4; i > 0; i--)
        {
            range >>= 1;
            uint bit = code >= range ? 1u : 0u;
            code -= range & (0u - bit);
            direct = (direct << 1) | bit;
            if (range < 1u << 24)
            {
                range <<= 8;
                code = (code << 8) | (at < end ? Unsafe.Add(ref input, at) : 0u);
                at++;
            }
        }

        return distance + (direct << 4) + ReverseTree(ref Unsafe.Add(ref p, Align), 4, ref range, ref code, ref input, ref at, end);
    }
}
