using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Egami.Formats.Webp;

/// <summary>
/// The VP8 boolean entropy encoder (RFC 6386 section 7), the inverse of <see cref="Vp8BoolDecoder"/>: the range is kept as
/// range − 1, a 1 adds split + 1 to the value, and finished bytes are written out with a carry that may ripple back over a run of
/// 0xFF bytes held until it is known.
/// </summary>
internal sealed class Vp8BoolEncoder : IDisposable
{
    private byte[] _buffer;
    private int _length;
    private int _range = 255 - 1;
    private int _value;
    private int _bits = -8;
    private int _run;

    internal Vp8BoolEncoder(int capacity = 4096)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(capacity, 64));
    }

    /// <summary>Gets an estimate of the bytes written so far, the pending bits included.</summary>
    internal long Size => _length + _run + ((_bits + 8 + 7) >> 3);

    /// <summary>Codes <paramref name="bit"/> with probability <paramref name="probability"/> / 256 of being 0; returns the bit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int PutBit(int bit, int probability)
    {
        int split = (_range * probability) >> 8;
        if (bit != 0)
        {
            _value += split + 1;
            _range -= split + 1;
        }
        else
        {
            _range = split;
        }

        if (_range < 127)
        {
            int shift = 7 ^ BitOperations.Log2((uint)_range + 1);
            _range = ((_range + 1) << shift) - 1;
            _value <<= shift;
            _bits += shift;
            if (_bits > 0)
            {
                Flush();
            }
        }

        return bit;
    }

    /// <summary>
    /// Codes a run of bits: bit 15 of each token is the bit, the low 15 bits index <paramref name="probabilities"/>. The same as
    /// <see cref="PutBit"/> for each, with the coder's state kept in registers and no branch on the bit.
    /// </summary>
    internal void PutBits(ReadOnlySpan<ushort> tokens, ReadOnlySpan<byte> probabilities)
    {
        int range = _range, value = _value, bits = _bits;
        foreach (ushort token in tokens)
        {
            int split = (range * probabilities[token & 0x7FFF]) >> 8;
            int one = -(token >> 15);
            value += (split + 1) & one;
            range = split + ((range - (2 * split) - 1) & one);
            int shift = 7 ^ BitOperations.Log2((uint)range + 1);
            range = ((range + 1) << shift) - 1;
            value <<= shift;
            bits += shift;
            if (bits > 0)
            {
                (_value, _bits) = (value, bits);
                Flush();
                (value, bits) = (_value, _bits);
            }
        }

        (_range, _value, _bits) = (range, value, bits);
    }

    /// <summary>Codes a bit at probability 1/2.</summary>
    internal int PutBitUniform(int bit) => PutBit(bit, 0x80);

    /// <summary>Codes the low <paramref name="bits"/> bits of <paramref name="value"/>, most significant first.</summary>
    internal void PutValue(int value, int bits)
    {
        while (bits-- > 0)
        {
            PutBitUniform((value >> bits) & 1);
        }
    }

    /// <summary>Codes a magnitude of <paramref name="bits"/> bits and a sign bit.</summary>
    internal void PutSignedValue(int value, int bits)
    {
        PutValue(Math.Abs(value), bits);
        PutBitUniform(value < 0 ? 1 : 0);
    }

    /// <summary>Codes a flag, then the signed value when it is not 0.</summary>
    internal void PutOptionalSigned(int value, int bits)
    {
        if (PutBitUniform(value != 0 ? 1 : 0) != 0)
        {
            PutSignedValue(value, bits);
        }
    }

    /// <summary>Writes out the bits still held and returns every byte written.</summary>
    internal ReadOnlySpan<byte> Finish()
    {
        PutValue(0, 9 - _bits);
        _bits = 0;
        Flush();
        return _buffer.AsSpan(0, _length);
    }

    public void Dispose()
    {
        byte[] buffer = _buffer;
        _buffer = [];
        if (buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Flush()
    {
        int s = 8 + _bits;
        int bits = _value >> s;
        _value -= bits << s;
        _bits -= 8;
        if ((bits & 0xFF) != 0xFF)
        {
            Reserve(_run + 1);
            if ((bits & 0x100) != 0 && _length > 0)
            {
                // The carry ripples into the byte before the held 0xFF run, which then wraps to zeros.
                _buffer[_length - 1]++;
            }

            byte held = (bits & 0x100) != 0 ? (byte)0x00 : (byte)0xFF;
            for (; _run > 0; _run--)
            {
                _buffer[_length++] = held;
            }

            _buffer[_length++] = (byte)bits;
        }
        else
        {
            // A 0xFF byte waits: a later carry would turn it to 0x00.
            _run++;
        }
    }

    private void Reserve(int count)
    {
        if (_length + count <= _buffer.Length)
        {
            return;
        }

        byte[] larger = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _length + count));
        _buffer.AsSpan(0, _length).CopyTo(larger);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = larger;
    }
}
