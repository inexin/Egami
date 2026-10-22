using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Egami.Video.Audio.Codecs.Flac;

/// <summary>Reads FLAC's big-endian, most-significant-bit-first fields through a 64-bit cache.</summary>
internal ref struct FlacBitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _next;
    private ulong _cache;
    private int _bits;

    public FlacBitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
    }

    /// <summary>Gets the number of whole bytes consumed so far.</summary>
    public readonly int BytePosition => _next - (_bits >> 3);

    /// <summary>Gets whether the reader is at a byte boundary.</summary>
    public readonly bool IsAligned => (_bits & 7) == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        if (_bits < count)
        {
            Refill();
            if (_bits < count)
            {
                ThrowEnd();
            }
        }

        uint value = (uint)(_cache >> (64 - count));
        _cache <<= count;
        _bits -= count;
        return value;
    }

    public ulong ReadBits64(int count) => count <= 32 ? ReadBits(count) : ((ulong)ReadBits(count - 32) << 32) | ReadBits(32);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadSigned(int count) => count == 0 ? 0 : (int)(ReadBits(count) << (32 - count)) >> (32 - count);

    public long ReadSigned64(int count) => count <= 32 ? ReadSigned(count) : ((long)ReadBits64(count) << (64 - count)) >> (64 - count);

    public bool ReadBit() => ReadBits(1) != 0;

    /// <summary>Reads a unary number: the count of zero bits before the next one bit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadUnary()
    {
        int count = 0;
        while (true)
        {
            if (_bits == 0)
            {
                Refill();
                if (_bits == 0)
                {
                    ThrowEnd();
                }
            }

            int zeros = BitOperations.LeadingZeroCount(_cache);
            if (zeros < _bits)
            {
                _cache = zeros == 63 ? 0 : _cache << (zeros + 1);
                _bits -= zeros + 1;
                return count + zeros;
            }

            count += _bits;
            _cache = 0;
            _bits = 0;
            if (count > (1 << 28))
            {
                ThrowEnd();
            }
        }
    }

    /// <summary>Reads <paramref name="destination"/>.Length zigzag Rice codes with parameter <paramref name="k"/>.</summary>
    public void ReadRice(Span<int> destination, int k)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            if (_bits < 32)
            {
                Refill();
            }

            uint value;
            int zeros = BitOperations.LeadingZeroCount(_cache);
            if (zeros < 32 && zeros + 1 + k <= _bits)
            {
                // The whole code is in the cache: the common case.
                _cache <<= zeros + 1;
                value = ((uint)zeros << k) | (k == 0 ? 0 : (uint)(_cache >> (64 - k)));
                _cache <<= k;
                _bits -= zeros + 1 + k;
            }
            else
            {
                value = ((uint)ReadUnary() << k) | ReadBits(k);
            }

            destination[i] = (int)(value >> 1) ^ -(int)(value & 1);
        }
    }

    /// <summary>Reads zigzag Rice codes into 64-bit values, for residuals that may not fit 32 bits.</summary>
    public void ReadRice(Span<long> destination, int k)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            ulong value = ((ulong)(uint)ReadUnary() << k) | ReadBits64(k);
            destination[i] = (long)(value >> 1) ^ -(long)(value & 1);
        }
    }

    /// <summary>Skips to the next byte boundary.</summary>
    public void AlignToByte()
    {
        int drop = _bits & 7;
        _cache <<= drop;
        _bits -= drop;
    }

    private static void ThrowEnd() => throw new CorruptDataException("FLAC", -1, "The frame ends early.");

    private void Refill()
    {
        if (_next + 8 <= _data.Length)
        {
            ulong word = BinaryPrimitives.ReadUInt64BigEndian(_data[_next..]);
            int take = (64 - _bits) >> 3;
            if (take == 0)
            {
                return;
            }

            _cache |= take == 8 ? word : (word >> (64 - (take * 8))) << (64 - (take * 8) - _bits);
            _next += take;
            _bits += take * 8;
            return;
        }

        while (_bits <= 56 && _next < _data.Length)
        {
            _cache |= (ulong)_data[_next++] << (56 - _bits);
            _bits += 8;
        }
    }
}
