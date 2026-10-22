// Follows the Vorbis I specification (Xiph.Org), section 2 "Bitpacking Convention": fields are packed least significant bit
// first, and reading past the end of a packet raises the end-of-packet condition (2.1.8).
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Egami.Video.Audio.Codecs.Vorbis;

/// <summary>Reads Vorbis's least-significant-bit-first fields through a 64-bit cache.</summary>
/// <remarks>
/// In strict mode (headers) reading past the end throws <see cref="CorruptDataException"/>. Otherwise it sets
/// <see cref="Eop"/>, which stays set, and returns zero: an end of packet in audio decode is a nominal occurrence.
/// </remarks>
internal ref struct VorbisBitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly bool _strict;
    private int _next;
    private ulong _cache;
    private int _bits;

    public VorbisBitReader(ReadOnlySpan<byte> data, bool strict)
    {
        _data = data;
        _strict = strict;
    }

    /// <summary>Gets whether a read went past the end of the packet.</summary>
    public bool Eop { get; private set; }

    /// <summary>Gets the number of bits left in the packet.</summary>
    public readonly long BitsLeft => _bits + ((long)(_data.Length - _next) * 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int count)
    {
        if (_bits < count)
        {
            Refill();
            if (_bits < count)
            {
                return EndOfPacket();
            }
        }

        uint value = (uint)(_cache & ((1UL << count) - 1));
        _cache >>= count;
        _bits -= count;
        return value;
    }

    public bool ReadBit() => ReadBits(1) != 0;

    /// <summary>Returns the next 32 bits without consuming them; bits past the end read as zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Peek32()
    {
        if (_bits < 32)
        {
            Refill();
        }

        return _bits >= 32 ? (uint)_cache : (uint)(_cache & ((1UL << _bits) - 1));
    }

    /// <summary>Consumes bits after a <see cref="Peek32"/>; returns false (and sets <see cref="Eop"/>) when fewer are left.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Skip(int count)
    {
        if (count > _bits)
        {
            EndOfPacket();
            return false;
        }

        _cache >>= count;
        _bits -= count;
        return true;
    }

    /// <summary>Marks the end of packet: consumes everything, so any further read also fails.</summary>
    public uint EndOfPacket()
    {
        if (_strict)
        {
            throw Vorbis.Corrupt("A header ends early.");
        }

        Eop = true;
        _bits = 0;
        _cache = 0;
        _next = _data.Length;
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Refill()
    {
        if (Eop)
        {
            return;
        }

        if (_next + 8 <= _data.Length)
        {
            // Bits above _bits already hold the next data, so OR-ing the same bytes in again is harmless.
            _cache |= BinaryPrimitives.ReadUInt64LittleEndian(_data[_next..]) << _bits;
            int bytes = (63 - _bits) >> 3;
            _next += bytes;
            _bits += bytes * 8;
            return;
        }

        while (_bits <= 56 && _next < _data.Length)
        {
            _cache |= (ulong)_data[_next++] << _bits;
            _bits += 8;
        }

        // Clear stale bits above the valid ones so a peek near the end sees zeros.
        _cache = _bits == 64 ? _cache : _cache & ((1UL << _bits) - 1);
    }
}
