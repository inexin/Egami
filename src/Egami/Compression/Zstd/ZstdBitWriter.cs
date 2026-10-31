using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Compression.Zstd;

/// <summary>
/// Writes a bit stream forwards, least significant bit first, for <see cref="ZstdBitReader"/> to read backwards: the last field
/// written is the first read. <see cref="Close"/> adds the end mark.
/// </summary>
/// <remarks>The target needs eight bytes of room past the last byte the stream can reach, for the whole-word stores.</remarks>
internal ref struct ZstdBitWriter
{
    private readonly Span<byte> _target;
    private ulong _container;
    private int _bits;
    private int _at;

    public ZstdBitWriter(Span<byte> target)
    {
        _target = target;
    }

    /// <summary>Gets a value indicating whether the stream has run into the last eight bytes of the target.</summary>
    public readonly bool Overflowed => _at > _target.Length - 8;

    /// <summary>Adds the low <paramref name="count"/> bits of <paramref name="value"/>; at most 56 bits may wait unflushed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(ulong value, int count)
    {
        _container |= (value & ((1UL << count) - 1)) << _bits;
        _bits += count;
    }

    /// <summary>Adds bits that are known to be clear above <paramref name="count"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddClean(ulong value, int count)
    {
        _container |= value << _bits;
        _bits += count;
    }

    /// <summary>Stores the whole bytes waiting; stops storing once the target is full, which <see cref="Overflowed"/> tells.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Flush()
    {
        int bytes = _bits >> 3;
        if (_at <= _target.Length - 8)
        {
            ulong value = BitConverter.IsLittleEndian ? _container : BinaryPrimitives.ReverseEndianness(_container);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(_target), _at), value);
            _at += bytes;
        }
        else
        {
            _at = _target.Length;
        }

        _bits &= 7;
        _container = bytes == 8 ? 0 : _container >> (bytes * 8);
    }

    /// <summary>Adds the end mark and stores the rest; returns the stream's length in bytes, or -1 when it did not fit.</summary>
    public int Close()
    {
        Add(1, 1);
        Flush();
        int length = _at + (_bits > 0 ? 1 : 0);
        return length > _target.Length - 8 ? -1 : length;
    }
}
