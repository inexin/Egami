using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Egami.Video.Audio.Codecs.Ac3;

/// <summary>
/// Reads an AC-3 frame most significant bit first. Reading past the end gives zeros and sets <see cref="Overrun"/>, so a damaged
/// frame stops cleanly (A/52 section 7.10.2, condition 21).
/// </summary>
internal ref struct Ac3BitReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private ulong _cache;
    private int _cached;
    private int _next;

    /// <summary>Gets whether a read went past the end of the data.</summary>
    public readonly bool Overrun => Position > (long)_data.Length * 8;

    /// <summary>Gets the bits read or skipped so far.</summary>
    public readonly long Position => ((long)_next * 8) - _cached;

    /// <summary>Gets the bytes being read.</summary>
    public readonly ReadOnlySpan<byte> Data => _data;

    /// <summary>Hands out the read state, for a hot loop that keeps it in locals; <see cref="SetState"/> puts it back.</summary>
    public readonly void GetState(out ulong cache, out int cached, out int next)
    {
        cache = _cache;
        cached = _cached;
        next = _next;
    }

    /// <summary>Puts back the read state <see cref="GetState"/> handed out.</summary>
    public void SetState(ulong cache, int cached, int next)
    {
        _cache = cache;
        _cached = cached;
        _next = next;
    }

    /// <summary>Reads 0 to 24 bits as an unsigned value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Read(int bits)
    {
        if (_cached < bits)
        {
            Refill();
        }

        _cached -= bits;
        return (int)(_cache >> _cached) & ((1 << bits) - 1);
    }

    /// <summary>Skips any number of bits.</summary>
    public void Skip(int bits)
    {
        while (bits > 24)
        {
            Read(24);
            bits -= 24;
        }

        Read(bits);
    }

    private void Refill()
    {
        // Keep the unread bits and append whole bytes, up to 63 bits held.
        int bytes = (63 - _cached) >> 3;
        if (_next + 8 <= _data.Length)
        {
            ulong word = BinaryPrimitives.ReadUInt64BigEndian(_data[_next..]);
            _cache = (_cache << (8 * bytes)) | (word >> (64 - (8 * bytes)));
            _next += bytes;
            _cached += 8 * bytes;
            return;
        }

        while (_cached <= 55)
        {
            byte b = _next < _data.Length ? _data[_next] : (byte)0;
            _next++;
            _cache = (_cache << 8) | b;
            _cached += 8;
        }
    }
}
