using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Egami.Video.Audio.Codecs.Aac;

/// <summary>
/// Reads an AAC raw data block most significant bit first, through a 64-bit cache so that a Huffman lookup can peek up to 32
/// bits. Reading past the end gives zeros and sets <see cref="Overrun"/>, which the decoder checks once per element.
/// </summary>
internal ref struct AacBitReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private ulong _cache;
    private int _cached;
    private int _next;

    /// <summary>Gets whether more bits were read than the block holds.</summary>
    public readonly bool Overrun => Position > (long)_data.Length * 8;

    /// <summary>Gets the bits read so far.</summary>
    public readonly long Position => ((long)_next * 8) - _cached;

    /// <summary>Gets the bits left, or a negative count after an overrun.</summary>
    public readonly long Remaining => ((long)_data.Length * 8) - Position;

    /// <summary>Reads 0 to 32 bits as an unsigned value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Read(int bits)
    {
        if (bits == 0)
        {
            return 0;
        }

        if (_cached < bits)
        {
            Refill();
        }

        _cached -= bits;
        return (uint)(_cache >> _cached) & (uint)((1UL << bits) - 1);
    }

    /// <summary>Reads one bit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadBit() => Read(1) != 0;

    /// <summary>Returns the next 32 bits without reading them, zeros past the end.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Peek32()
    {
        if (_cached < 32)
        {
            Refill();
        }

        return (uint)(_cache << (64 - _cached) >> 32);
    }

    /// <summary>Moves past bits already peeked.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Skip(int bits)
    {
        while (bits > 32)
        {
            Read(32);
            bits -= 32;
        }

        Read(bits);
    }

    /// <summary>Skips to the next byte boundary, counted from the start of the block.</summary>
    public void Align() => Skip((int)((8 - (Position & 7)) & 7));

    private void Refill()
    {
        // Keep the unread bits and append whole bytes, up to 63 bits held; past the end, zeros count as read.
        if (_next + 8 <= _data.Length)
        {
            int bytes = (63 - _cached) >> 3;
            ulong word = BinaryPrimitives.ReadUInt64BigEndian(_data[_next..]);
            _cache = (_cached == 0 ? 0 : _cache << (bytes * 8)) | (word >> (64 - (bytes * 8)));
            _cached += bytes * 8;
            _next += bytes;
            return;
        }

        while (_cached <= 55)
        {
            _cache = (_cache << 8) | (_next < _data.Length ? _data[_next] : 0u);
            _cached += 8;
            _next++;
        }
    }
}
