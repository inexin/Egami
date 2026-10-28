using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Video.Codecs.Ffv1;

/// <summary>
/// The encoder for <see cref="Ffv1RangeDecoder"/>: the same 16-bit range split by the same adaptive states, its low end kept with
/// one cached byte and a count of pending 0xFF bytes so a carry can still reach bytes already decided.
/// </summary>
internal sealed class Ffv1RangeEncoder
{
    private byte[] _buffer = new byte[1 << 12];
    private int _length;
    private uint _low;
    private uint _range;
    private byte _cache;
    private bool _hasCache;
    private int _pending;
    private byte[] _one = Ffv1StateTable.Default.One;
    private byte[] _zero = Ffv1StateTable.Default.Zero;

    /// <summary>Gets the bytes written so far, after <see cref="Finish"/> or <see cref="FinishSentinel"/> all of them.</summary>
    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _length);

    /// <summary>Gets the number of bytes written so far.</summary>
    public int Length => _length;

    /// <summary>Gets the buffer the bytes are in.</summary>
    public byte[] Buffer => _buffer;

    /// <summary>Starts a new stream with the default state transition table.</summary>
    public void Reset()
    {
        _length = 0;
        _low = 0;
        _range = 0xFF00;
        _hasCache = false;
        _pending = 0;
        Table = Ffv1StateTable.Default;
    }

    /// <summary>Sets the state transition table the states adapt with.</summary>
    public Ffv1StateTable Table
    {
        set
        {
            _one = value.One;
            _zero = value.Zero;
        }
    }

    /// <summary>Writes one bit with an adaptive state (put_rac).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBit(ref byte state, int bit)
    {
        uint split = (_range * state) >> 8;
        if (bit == 0)
        {
            _range -= split;
            state = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_zero), state);
        }
        else
        {
            _low += _range - split;
            _range = split;
            state = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_one), state);
        }

        if (_range < 0x100)
        {
            _range <<= 8;
            ShiftLow();
        }
    }

    /// <summary>Writes a signed scalar with the 32 adaptive states starting at <paramref name="states"/> (put_symbol).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSigned(ref byte states, int value)
    {
        if (value == 0)
        {
            WriteBit(ref states, 1);
            return;
        }

        WriteTail(ref states, value, true);
    }

    /// <summary>Writes a scalar with 32 adaptive states, as <see cref="Ffv1RangeDecoder.ReadSymbol"/> reads it.</summary>
    public void WriteSymbol(Span<byte> states, int value, bool signed)
    {
        if (value == 0)
        {
            WriteBit(ref states[0], 1);
            return;
        }

        WriteTail(ref states[0], value, signed);
    }

    /// <summary>Writes a header flag (br) with the first of the header's states.</summary>
    public void WriteFlag(Span<byte> states, bool value) => WriteBit(ref states[0], value ? 1 : 0);

    /// <summary>
    /// Ends a stream read in Closed mode: the decoder is given its length and reads zeros past it. The range is at least 256, so
    /// it holds a multiple of 256; writing that one's high byte leaves the zero the decoder reads after it.
    /// </summary>
    public void Finish()
    {
        _low = (_low + 0xFF) & ~0xFFu;
        ShiftLow();
        ShiftLow();
    }

    /// <summary>
    /// Ends a version 3 range-coded slice header that Golomb-Rice coded data follows (Sentinel mode, RFC 9043 section
    /// 3.8.1.1.1). The decoder reads one symbol with state 129, whose value it drops, and takes the data to start one byte
    /// before where it then is. Under a range of 515, a 0 there leaves the range under 256, so the decoder reads one more byte;
    /// from 515 on neither value does. Either way the byte before the data is the high byte of the decoder's window, so the
    /// low end is rounded up to a multiple of 256 that leaves the low byte, the data's first, free.
    /// </summary>
    public void FinishSentinel()
    {
        if (_range < 515)
        {
            byte state = 129;
            WriteBit(ref state, 0);
        }

        Finish();
    }

    /// <summary>Appends eight bytes, most significant first, after a finished stream.</summary>
    public void AppendBigEndian(ulong value)
    {
        EnsureRoom(8);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(_buffer.AsSpan(_length), value);
        _length += 8;
    }

    /// <summary>Appends raw bytes after a finished stream.</summary>
    public void Append(ReadOnlySpan<byte> bytes)
    {
        EnsureRoom(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
    }

    private void WriteTail(ref byte states, int value, bool signed)
    {
        int a = Math.Abs(value);
        int e = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)a);
        WriteBit(ref states, 0);
        for (int i = 0; i < e; i++)
        {
            WriteBit(ref Unsafe.Add(ref states, 1 + Math.Min(i, 9)), 1);
        }

        WriteBit(ref Unsafe.Add(ref states, 1 + Math.Min(e, 9)), 0);
        for (int i = e - 1; i >= 0; i--)
        {
            WriteBit(ref Unsafe.Add(ref states, 22 + Math.Min(i, 9)), (a >> i) & 1);
        }

        if (signed)
        {
            WriteBit(ref Unsafe.Add(ref states, 11 + Math.Min(e, 10)), value < 0 ? 1 : 0);
        }
    }

    private void ShiftLow()
    {
        if (_low < 0xFF00 || _low >= 0x10000)
        {
            int carry = (int)(_low >> 16);
            EnsureRoom(_pending + 1);
            if (_hasCache)
            {
                _buffer[_length++] = (byte)(_cache + carry);
            }

            for (; _pending > 0; _pending--)
            {
                _buffer[_length++] = (byte)(0xFF + carry);
            }

            _cache = (byte)(_low >> 8);
            _hasCache = true;
        }
        else
        {
            _pending++;
        }

        _low = (_low & 0xFF) << 8;
    }

    private void EnsureRoom(int count)
    {
        if (_length + count > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + count));
        }
    }
}
