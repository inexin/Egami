using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Egami.Video.Codecs.Ffv1;

/// <summary>
/// Reads FFV1's Golomb-Rice coded slices (RFC 9043 section 3.8.2) most significant bit first, through a 64-bit window. Bits past
/// the end read as zero.
/// </summary>
internal struct Ffv1BitReader
{
    private readonly byte[] _data;
    private readonly int _end;
    private int _pos;
    private ulong _window;
    private int _bits;

    public Ffv1BitReader(byte[] data, int start, int end)
    {
        _data = data;
        _pos = start;
        _end = end;
    }

    /// <summary>Reads <paramref name="count"/> bits, 0 to 32.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBits(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        if (_bits < count)
        {
            Refill();
        }

        int value = (int)(_window >> (64 - count));
        _window <<= count;
        _bits -= count;
        return value;
    }

    /// <summary>Reads an unsigned Golomb-Rice code with parameter <paramref name="k"/>; the escape reads <paramref name="bits"/> bits (get_ur_golomb).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadUnsignedGolomb(int k, int bits)
    {
        if (_bits < 32)
        {
            Refill();
        }

        int zeros = BitOperations.LeadingZeroCount(_window | 1);
        if (zeros < 12)
        {
            _window <<= zeros + 1;
            _bits -= zeros + 1;
            return ReadBits(k) + (zeros << k);
        }

        _window <<= 12;
        _bits -= 12;
        return ReadBits(bits) + 11;
    }

    private void Refill()
    {
        if (_pos + 8 <= _end)
        {
            // Eight bytes at once: take as many whole bytes as fit below the bits still in the window.
            ulong next = BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan(_pos, 8));
            int take = (63 - _bits) >> 3;
            _window |= (next >> _bits) & (ulong.MaxValue << (64 - _bits - (take * 8)));
            _pos += take;
            _bits += take * 8;
            return;
        }

        while (_bits <= 56)
        {
            ulong b = _pos < _end ? _data[_pos] : 0UL;
            _pos++;
            _window |= b << (56 - _bits);
            _bits += 8;
        }
    }
}
