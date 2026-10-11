using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Egami.Formats.Webp;

/// <summary>
/// The VP8 boolean entropy decoder (RFC 6386 section 7) over bytes in memory. The value window holds up to 56 unread bits; the
/// range is kept as range − 1, so a split is (range − 1) × probability / 256 and a 1 is decoded when the value is above it. Past the
/// end of the data the decoder reads zero bits and says so.
/// </summary>
internal struct Vp8BoolDecoder
{
    private readonly byte[] _data;
    private int _position;
    private readonly int _end;
    private ulong _value;
    private int _bits;
    private uint _range;
    private bool _eof;

    /// <summary>Starts decoding <paramref name="length"/> bytes of <paramref name="data"/> from <paramref name="offset"/>.</summary>
    internal Vp8BoolDecoder(byte[] data, int offset, int length)
    {
        _data = data;
        _position = offset;
        _end = offset + length;
        _value = 0;
        _bits = -8;
        _range = 255 - 1;
        _eof = false;
        Load();
    }

    /// <summary>Gets how far into the data the decoder has loaded bytes (up to seven ahead of the bits it has used).</summary>
    internal readonly int Position => _position;

    /// <summary>Gets whether decoding has gone past the end of the data (zero bits were read in its place).</summary>
    internal readonly bool PastEnd => _eof && _bits < 0;

    /// <summary>Decodes one bit whose probability of being 0 is <paramref name="probability"/> / 256.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetBit(int probability)
    {
        uint range = _range;
        if (_bits < 0)
        {
            Load();
        }

        int position = _bits;
        uint split = (range * (uint)probability) >> 8;
        uint value = (uint)(_value >> position);
        int bit;
        if (value > split)
        {
            range -= split;
            _value -= (ulong)(split + 1) << position;
            bit = 1;
        }
        else
        {
            range = split + 1;
            bit = 0;
        }

        // Normalise so the range is 128 to 255 again.
        int shift = 7 ^ BitOperations.Log2(range);
        range <<= shift;
        _bits -= shift;
        _range = range - 1;
        return bit;
    }

    /// <summary>Hands the bit state to a hot loop that keeps it in locals; <see cref="Put"/> takes it back.</summary>
    internal readonly void Take(out ulong value, out int bits, out uint range)
    {
        value = _value;
        bits = _bits;
        range = _range;
    }

    /// <summary>Takes back the bit state a hot loop kept in locals.</summary>
    internal void Put(ulong value, int bits, uint range)
    {
        _value = value;
        _bits = bits;
        _range = range;
    }

    /// <summary>
    /// Decodes one bit with the state in locals: as <see cref="GetBit"/>, refilling through the decoder only when the value window
    /// runs dry.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Bit(ref Vp8BoolDecoder decoder, ref ulong value, ref int bits, ref uint range, int probability)
    {
        if (bits < 0)
        {
            decoder.Refill(ref value, ref bits);
        }

        uint split = (range * (uint)probability) >> 8;
        int bit;
        if ((uint)(value >> bits) > split)
        {
            range -= split;
            value -= (ulong)(split + 1) << bits;
            bit = 1;
        }
        else
        {
            range = split + 1;
            bit = 0;
        }

        int shift = 7 ^ BitOperations.Log2(range);
        range = (range << shift) - 1;
        bits -= shift;
        return bit;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Refill(ref ulong value, ref int bits)
    {
        _value = value;
        _bits = bits;
        Load();
        value = _value;
        bits = _bits;
    }

    /// <summary>Decodes an unsigned <paramref name="bits"/>-bit number, most significant bit first, each bit at probability 1/2.</summary>
    internal int GetValue(int bits)
    {
        int v = 0;
        while (bits-- > 0)
        {
            v |= GetBit(0x80) << bits;
        }

        return v;
    }

    /// <summary>Decodes a magnitude of <paramref name="bits"/> bits followed by a sign bit.</summary>
    internal int GetSignedValue(int bits)
    {
        int value = GetValue(bits);
        return GetBit(0x80) != 0 ? -value : value;
    }

    /// <summary>Decodes a flag and, if set, a signed value of <paramref name="bits"/> bits; else 0.</summary>
    internal int GetOptionalSigned(int bits) => GetBit(0x80) != 0 ? GetSignedValue(bits) : 0;

    private void Load()
    {
        if (_position + 8 <= _end)
        {
            // Seven bytes, big-endian, under the bits not yet used.
            ulong bytes = BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan(_position)) >> 8;
            _position += 7;
            _value = (_value << 56) | bytes;
            _bits += 56;
            return;
        }

        if (_position < _end)
        {
            _value = (_value << 8) | _data[_position++];
            _bits += 8;
        }
        else if (!_eof)
        {
            _value <<= 8;
            _bits += 8;
            _eof = true;
        }
        else
        {
            // Past the end: keep reading zeros.
            _value <<= 8;
            _bits += 8;
        }
    }
}
