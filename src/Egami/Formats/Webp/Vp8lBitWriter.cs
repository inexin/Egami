using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Egami.Formats.Webp;

/// <summary>
/// VP8L's bit stream (RFC 9649 section 3.3): values packed least significant bit first into bytes, gathered in a 64-bit word and
/// written 32 bits at a time into a pooled buffer that grows as needed.
/// </summary>
internal sealed class Vp8lBitWriter : IDisposable
{
    private byte[] _bytes;
    private int _length;
    private ulong _pending;
    private int _used;

    internal Vp8lBitWriter(int capacity = 4096)
    {
        _bytes = ArrayPool<byte>.Shared.Rent(Math.Max(capacity, 64));
    }

    /// <summary>Gets the number of bits written so far.</summary>
    internal long BitCount => ((long)_length * 8) + _used;

    /// <summary>Writes the low <paramref name="count"/> bits of <paramref name="value"/>, at most 32.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Write(uint value, int count)
    {
        _pending |= (ulong)(value & (uint)((1UL << count) - 1)) << _used;
        _used += count;
        if (_used >= 32)
        {
            Flush32();
        }
    }

    /// <summary>Appends whole bytes; the stream must be at a byte boundary.</summary>
    internal void Write(ReadOnlySpan<byte> bytes)
    {
        if ((_used & 7) != 0)
        {
            throw new InvalidOperationException("The bit stream is not at a byte boundary.");
        }

        while (_used > 0)
        {
            Reserve(1);
            _bytes[_length++] = (byte)_pending;
            _pending >>= 8;
            _used -= 8;
        }

        Reserve(bytes.Length);
        bytes.CopyTo(_bytes.AsSpan(_length));
        _length += bytes.Length;
    }

    /// <summary>Gets the bytes written, the last one padded with zero bits; valid until the next write.</summary>
    internal ReadOnlySpan<byte> Finish()
    {
        while (_used > 0)
        {
            Reserve(1);
            _bytes[_length++] = (byte)_pending;
            _pending >>= 8;
            _used = Math.Max(0, _used - 8);
        }

        return _bytes.AsSpan(0, _length);
    }

    /// <summary>Forgets everything written, keeping the buffer.</summary>
    internal void Reset()
    {
        _length = 0;
        _pending = 0;
        _used = 0;
    }

    public void Dispose()
    {
        byte[] bytes = _bytes;
        _bytes = [];
        if (bytes.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private void Flush32()
    {
        Reserve(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_bytes.AsSpan(_length), (uint)_pending);
        _length += 4;
        _pending >>= 32;
        _used -= 32;
    }

    private void Reserve(int count)
    {
        if (_length + count <= _bytes.Length)
        {
            return;
        }

        byte[] larger = ArrayPool<byte>.Shared.Rent(Math.Max(_bytes.Length * 2, _length + count));
        _bytes.AsSpan(0, _length).CopyTo(larger);
        ArrayPool<byte>.Shared.Return(_bytes);
        _bytes = larger;
    }
}
