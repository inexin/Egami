using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Egami.Compression.Zstd;

/// <summary>
/// Reads a Zstandard bit stream backwards (RFC 8878 section 4.1): the last byte's highest set bit marks the end, and fields are
/// read from just below it towards the first byte. Eight bytes are held at a time, most significant bit first.
/// </summary>
/// <remarks>
/// After <see cref="Reload"/> at least 57 unread bits are held, or every bit still unread. Reading past the start gives
/// meaningless bits rather than failing; <see cref="Overflowed"/> and <see cref="Finished"/> tell the caller afterwards, which
/// keeps the hot loops free of checks.
/// </remarks>
internal struct ZstdBitReader
{
    /// <summary>The eight bytes from <see cref="Position"/>, as a little-endian number.</summary>
    public ulong Container;

    /// <summary>How many of the container's bits, from the most significant, are read.</summary>
    public int Consumed;

    /// <summary>Where the container's first byte is in the stream.</summary>
    public int Position;

    /// <summary>Gets a value indicating whether, after a reload, more bits were read than the stream holds.</summary>
    public readonly bool Overflowed => Consumed > 64;

    /// <summary>Gets a value indicating whether, after a reload, every bit of the stream is read and no more.</summary>
    public readonly bool Finished => Position == 0 && Consumed == 64;

    /// <summary>Starts reading <paramref name="stream"/>; false when it is empty or its last byte holds no end mark.</summary>
    public static bool TryStart(ReadOnlySpan<byte> stream, out ZstdBitReader reader)
    {
        reader = default;
        if (stream.IsEmpty || stream[^1] == 0)
        {
            return false;
        }

        if (stream.Length >= 8)
        {
            reader.Position = stream.Length - 8;
            reader.Container = BinaryPrimitives.ReadUInt64LittleEndian(stream[reader.Position..]);
        }
        else
        {
            // A short stream sits in the container's low bytes; the empty high bytes count as read.
            ulong container = 0;
            for (int i = 0; i < stream.Length; i++)
            {
                container |= (ulong)stream[i] << (8 * i);
            }

            reader.Container = container;
            reader.Consumed = (8 - stream.Length) * 8;
        }

        reader.Consumed += 8 - BitOperations.Log2(stream[^1]);
        return true;
    }

    /// <summary>Reads <paramref name="count"/> bits, 0 to 56.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int count)
    {
        // Two shifts, so a count of 0 gives 0 rather than the whole container.
        uint value = (uint)(((Container << Consumed) >> 1) >> (63 - count));
        Consumed += count;
        return value;
    }

    /// <summary>Reads up to 63 bits as a 64-bit number.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadLong(int count)
    {
        ulong value = ((Container << Consumed) >> 1) >> (63 - count);
        Consumed += count;
        return value;
    }

    /// <summary>Peeks at the next <paramref name="count"/> bits, 1 to 63, without reading them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int Peek(int count) => (int)((Container << Consumed) >> (64 - count));

    /// <summary>Moves the container back over the bytes read; <paramref name="start"/> is the stream's first byte.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reload(ref byte start)
    {
        if (Position >= 8)
        {
            // Callers read at most 57 bits between reloads, so this stays inside the stream.
            System.Diagnostics.Debug.Assert(Consumed <= 64, "More bits read than a reload holds.");
            Position -= Consumed >> 3;
            Consumed &= 7;
        }
        else if (Position > 0)
        {
            int back = Math.Min(Consumed >> 3, Position);
            Position -= back;
            Consumed -= back << 3;
        }
        else
        {
            return;
        }

        ulong container = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, Position));
        Container = BitConverter.IsLittleEndian ? container : BinaryPrimitives.ReverseEndianness(container);
    }
}
