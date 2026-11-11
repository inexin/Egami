using System.Runtime.CompilerServices;

namespace Egami.Video.Audio.Codecs.Aac;

/// <summary>
/// Writes an AAC raw data block most significant bit first into a growable buffer, through a 64-bit accumulator. The encoder
/// writes each frame once, after its bit count is known, so the buffer rarely grows past its first size.
/// </summary>
internal sealed class AacBitWriter
{
    private byte[] _buffer;
    private ulong _cache;
    private int _cached;
    private int _bytes;

    /// <summary>Initializes a writer with room for <paramref name="capacity"/> bytes.</summary>
    /// <param name="capacity">The starting size of the buffer.</param>
    public AacBitWriter(int capacity) => _buffer = new byte[Math.Max(capacity, 16)];

    /// <summary>Gets the bits written since the last <see cref="Reset"/>.</summary>
    public long Position => ((long)_bytes * 8) + _cached;

    /// <summary>Starts a new block.</summary>
    public void Reset()
    {
        _cache = 0;
        _cached = 0;
        _bytes = 0;
    }

    /// <summary>Writes the low <paramref name="bits"/> bits of <paramref name="value"/>, 0 to 32 bits.</summary>
    /// <param name="value">The value.</param>
    /// <param name="bits">The bit count.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(uint value, int bits)
    {
        if (bits == 0)
        {
            return;
        }

        _cache = (_cache << bits) | (value & (uint)((1UL << bits) - 1));
        _cached += bits;
        if (_cached >= 32)
        {
            Spill();
        }
    }

    /// <summary>Writes one bit.</summary>
    /// <param name="bit">The bit.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBit(bool bit) => Write(bit ? 1u : 0u, 1);

    /// <summary>Pads with zero bits to the next byte boundary.</summary>
    public void Align() => Write(0, (int)((8 - (Position & 7)) & 7));

    /// <summary>Gets the bytes written, after padding the last one with zeros.</summary>
    /// <returns>The block, valid until the next write.</returns>
    public ReadOnlySpan<byte> Finish()
    {
        Align();
        while (_cached > 0)
        {
            Ensure(1);
            _cached -= 8;
            _buffer[_bytes++] = (byte)(_cache >> _cached);
        }

        return _buffer.AsSpan(0, _bytes);
    }

    private void Spill()
    {
        Ensure(4);
        while (_cached >= 8)
        {
            _cached -= 8;
            _buffer[_bytes++] = (byte)(_cache >> _cached);
        }
    }

    private void Ensure(int count)
    {
        if (_bytes + count > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _bytes + count));
        }
    }
}
