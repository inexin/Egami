using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Egami.Formats.Webp;

/// <summary>
/// Reads a VP8L bit stream (RFC 9649 section 3.3), least significant bit first, from a chunk's payload in the input or from bytes in
/// memory. A 64-bit buffer holds the next bits at the bottom, refilled eight bytes at a time. Past the end of the payload the buffer
/// is padded with zero bits, and <see cref="PastEnd"/> says whether any padding was consumed, which means the data ended early. An
/// incremental decode marks a place and goes back to it when bytes run out (<see cref="Streaming.NeedsInputException"/>).
/// </summary>
internal sealed class Vp8lBitReader : IDisposable
{
    private const int ChunkSize = 16 * 1024;

    /// <summary>What <see cref="NextByte"/> returns when the next byte of an incremental decode has not arrived.</summary>
    private const int NotYet = -2;

    private readonly EgamiInput? _input;
    private readonly bool _pooled;
    private byte[] _chunk;
    private long _remaining;
    private int _position;
    private int _length;
    private int _mark = -1;
    private ulong _bits;
    private int _count;
    private int _padding;

    /// <summary>Reads <paramref name="payload"/> bytes from <paramref name="input"/>, the payload of the chunk being decoded.</summary>
    internal Vp8lBitReader(EgamiInput input, long payload)
    {
        _input = input;
        _remaining = payload;
        _chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
        _pooled = true;
    }

    /// <summary>Reads bytes held in memory, such as an ALPH chunk's payload.</summary>
    internal Vp8lBitReader(byte[] data, int offset, int length)
    {
        _chunk = data;
        _position = offset;
        _length = offset + length;
    }

    /// <summary>Gets whether decoding has used padding bits past the end of the data.</summary>
    internal bool PastEnd => _padding > 0 && _count < _padding;

    /// <summary>Gets whether every byte of the payload has been taken into the bit buffer or consumed.</summary>
    internal bool AllBytesRead => _remaining == 0 && _position == _length;

    /// <summary>Makes at least 32 bits available (padding with zeros past the end of the data).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Fill()
    {
        if (_count <= 32)
        {
            FillSlow(32);
        }
    }

    /// <summary>Returns the next <paramref name="n"/> bits (0 to 32) without consuming them; the caller has made them available.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint Peek(int n) => (uint)(_bits & ((1UL << n) - 1));

    /// <summary>Consumes <paramref name="n"/> bits the caller has looked at.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Skip(int n)
    {
        _bits >>= n;
        _count -= n;
    }

    /// <summary>Reads <paramref name="n"/> bits (0 to 32) as an unsigned number.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint Read(int n)
    {
        if (_count < n)
        {
            FillSlow(n);
        }

        uint value = (uint)(_bits & ((1UL << n) - 1));
        _bits >>= n;
        _count -= n;
        return value;
    }

    /// <summary>Gets the bits buffered and their count, for a hot loop that keeps them in locals.</summary>
    internal (ulong Bits, int Count) Save() => (_bits, _count);

    /// <summary>Puts back the bits a hot loop kept in locals.</summary>
    internal void Restore(ulong bits, int count)
    {
        _bits = bits;
        _count = count;
    }

    /// <summary>Refills the buffer a hot loop keeps in locals.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Fill(ref ulong bits, ref int count)
    {
        if (count <= 32)
        {
            _bits = bits;
            _count = count;
            FillSlow(32);
            bits = _bits;
            count = _count;
        }
    }

    /// <summary>Where the reader is, so an incremental decode that runs out of bytes can go back.</summary>
    internal readonly record struct Checkpoint(int Position, ulong Bits, int Count, int Padding);

    /// <summary>Marks the current place: the bytes from here on are kept until the next mark.</summary>
    internal Checkpoint Mark()
    {
        _mark = _position;
        return new Checkpoint(0, _bits, _count, _padding);
    }

    /// <summary>Goes back to the place <see cref="Mark"/> returned, whose bytes were kept.</summary>
    internal void Rewind(Checkpoint checkpoint)
    {
        _position = _mark + checkpoint.Position;
        _bits = checkpoint.Bits;
        _count = checkpoint.Count;
        _padding = checkpoint.Padding;
    }

    /// <summary>Stops keeping bytes for a rewind.</summary>
    internal void Unmark() => _mark = -1;

    /// <summary>
    /// Skips what is left of the payload in the input, so the next chunk can be read; stops at the end of the data. An incremental
    /// decode waits for the rest, and can call this again.
    /// </summary>
    internal void SkipRest()
    {
        _position = _length;
        _mark = -1;
        while (_input is not null && _remaining > 0)
        {
            int n = _input.Peek((int)Math.Min(_remaining, 64 * 1024)).Length;
            if (n == 0)
            {
                if (_input.Incremental && !_input.AtEndOfInput)
                {
                    throw new Streaming.NeedsInputException();
                }

                _remaining = 0;
                break;
            }

            _input.Skip(n);
            _remaining -= n;
        }
    }

    public void Dispose()
    {
        if (_pooled && _chunk.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_chunk);
        }

        _chunk = [];
    }

    /// <summary>
    /// Fills the buffer to more than 56 bits, or as far as the bytes go. In an incremental decode, bytes that have not arrived stop
    /// the fill once at least <paramref name="need"/> bits are there; with fewer, the decode waits.
    /// </summary>
    private void FillSlow(int need)
    {
        while (_count <= 56)
        {
            if (_length - _position >= 8)
            {
                // Whole bytes that fit: (64 - count) / 8 of them, taken from one 64-bit load.
                int take = (64 - _count) >> 3;
                _bits |= BinaryPrimitives.ReadUInt64LittleEndian(_chunk.AsSpan(_position)) << _count;
                _position += take;
                _count += take * 8;
                _bits &= _count == 64 ? ulong.MaxValue : (1UL << _count) - 1;
                return;
            }

            int next = NextByte();
            if (next == NotYet)
            {
                if (_count >= need)
                {
                    return;
                }

                throw new Streaming.NeedsInputException();
            }

            if (next < 0)
            {
                // Past the end: zero bits, counted, so a decode that reads them can tell.
                _padding += 8;
                _count += 8;
                continue;
            }

            _bits |= (ulong)(uint)next << _count;
            _count += 8;
        }
    }

    private int NextByte()
    {
        if (_position == _length)
        {
            if (_input is null || _remaining == 0)
            {
                return -1;
            }

            if (_mark >= 0)
            {
                // The bytes since the checkpoint stay, so the decode can go back to it.
                int kept = _length - _mark;
                if (kept + ChunkSize > _chunk.Length)
                {
                    byte[] larger = ArrayPool<byte>.Shared.Rent(Math.Max(_chunk.Length * 2, kept + ChunkSize));
                    _chunk.AsSpan(_mark, kept).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(_chunk);
                    _chunk = larger;
                }
                else
                {
                    _chunk.AsSpan(_mark, kept).CopyTo(_chunk);
                }

                _position = kept;
                _length = kept;
                _mark = 0;
            }
            else
            {
                _position = 0;
                _length = 0;
            }

            int read = _input.Read(_chunk.AsSpan(_length, (int)Math.Min(ChunkSize, _remaining)));
            _length += read;
            _remaining -= read;
            if (read == 0)
            {
                if (_input.Incremental && !_input.AtEndOfInput)
                {
                    return NotYet;
                }

                _remaining = 0;
                return -1;
            }
        }

        return _chunk[_position++];
    }
}
