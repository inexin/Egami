using System.Runtime.CompilerServices;

namespace Egami.Nal;

/// <summary>
/// The CABAC arithmetic encoding engine (H.264 9.3.4.2 to 9.3.4.6, which HEVC codes bin for bin), the inverse of
/// <see cref="CabacEngine"/>. Instead of putting out one bit at a time with outstanding bits, the low end of the interval is kept
/// in a register with the bits of the byte being formed above the standard's 10 bits: a whole byte leaves it at a time, and a
/// carry out of it goes into the last byte kept back and the run of 0xFF bytes after it, which are only written once no carry can
/// reach them. <see cref="Finish"/> after a terminating 1 bin flushes the interval and writes the stop bit and the zero bits to
/// the byte boundary (rbsp_slice_segment_trailing_bits, or byte_alignment() after end_of_subset_one_bit).
/// </summary>
internal sealed class CabacEncoder
{
    private byte[] _data = new byte[256];
    private int _length;
    private uint _low;
    private uint _range;
    private int _pending;
    private int _held = -1;
    private int _run;

    /// <summary>Gets the bytes written so far (without the bytes still held back for a carry).</summary>
    internal int Length => _length;

    /// <summary>
    /// Gets about how many bits the bins so far take: the bytes written and held back, and the bits in the register. Exact to a
    /// byte, for rate decisions between whole coding tree blocks.
    /// </summary>
    internal long BitCount => ((long)(_length + (_held >= 0 ? 1 : 0) + _run) * 8) + _pending + 1;

    /// <summary>Starts a new substream (H.265 9.3.2.5): the interval is the whole range and nothing is written.</summary>
    internal void Start()
    {
        _length = 0;
        _low = 0;
        _range = 510;
        _pending = 0;
        _held = -1;
        _run = 0;
    }

    /// <summary>Encodes a context-coded bin (H.264 9.3.4.2). A state is (pStateIdx &lt;&lt; 1) | valMps.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Encode(ref byte state, int bin)
    {
        int s = state;
        uint lps = CabacEngine.RangeLps[((s >> 1) << 2) + (int)((_range >> 6) & 3)];
        uint range = _range - lps;
        if (bin == (s & 1))
        {
            state = CabacCost.NextMps[s];
            if (range >= 256)
            {
                _range = range;
                return;
            }

            _low <<= 1;
            _range = range << 1;
            if (++_pending >= 8)
            {
                PutByte();
            }

            return;
        }

        state = CabacCost.NextLps[s];
        int shift = System.Numerics.BitOperations.LeadingZeroCount(lps) - 23;
        _low = (_low + range) << shift;
        _range = lps << shift;
        _pending += shift;
        if (_pending >= 8)
        {
            PutByte();
        }
    }

    /// <summary>Encodes a bypass bin (H.264 9.3.4.4).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Bypass(int bin)
    {
        _low <<= 1;
        if (bin != 0)
        {
            _low += _range;
        }

        if (++_pending >= 8)
        {
            PutByte();
        }
    }

    /// <summary>Encodes the low <paramref name="count"/> bits (up to 32) of <paramref name="bins"/> as bypass bins, most significant first.</summary>
    internal void Bypass(uint bins, int count)
    {
        while (count > 0)
        {
            // At most 8 bins at once keep the register within 32 bits: 9 + 7 bits of interval and pending byte, 8 more, a carry.
            int n = Math.Min(count, 8);
            count -= n;
            uint part = (bins >> count) & ((1u << n) - 1);
            _low = (_low << n) + (part * _range);
            _pending += n;
            while (_pending >= 8)
            {
                PutByte();
            }
        }
    }

    /// <summary>Encodes a terminating bin (H.264 9.3.4.5): 1 ends the slice segment, the substream or the bins before pcm_sample.</summary>
    internal void Terminate(int bin)
    {
        _range -= 2;
        if (bin != 0)
        {
            _low += _range;
            _low <<= 7;
            _range = 2 << 7;
            _pending += 7;
        }
        else if (_range < 256)
        {
            _low <<= 1;
            _range <<= 1;
            _pending++;
        }

        if (_pending >= 8)
        {
            PutByte();
        }
    }

    /// <summary>
    /// After a terminating 1 bin: writes the interval's last two bits, a 1 bit and zero bits to the byte boundary, and the bytes
    /// held back, and returns everything written since <see cref="Start"/>.
    /// </summary>
    internal ReadOnlySpan<byte> Finish()
    {
        // EncodeFlush (9.3.4.5): the pending bits (down to bit 9 of the standard's 10-bit register) and bit 8, then the stop bit.
        int bits = _pending + 1;
        ulong value = _low >> 8;
        value = (value << 1) | 1;
        bits++;
        int pad = (8 - (bits & 7)) & 7;
        value <<= pad;
        bits += pad;
        while (bits > 0)
        {
            bits -= 8;
            Put((uint)(value >> bits));
            value &= (1UL << bits) - 1;
        }

        Release(0);
        return _data.AsSpan(0, _length);
    }

    /// <summary>Takes the byte above the 10-bit register out of it, with its carry.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PutByte()
    {
        // The standard's first bit is never written (firstBitFlag): the first byte is bits 8 + _pending down to 1 + _pending of
        // the register, bit 9 + _pending its carry.
        int shift = _pending + 1;
        uint top = _low >> shift;
        _low &= (1u << shift) - 1;
        _pending -= 8;
        Put(top);
    }

    /// <summary>One byte with a possible carry in bit 8: 0xFF waits in the run, anything else releases what was held back.</summary>
    private void Put(uint top)
    {
        if (top == 0xFF)
        {
            _run++;
            return;
        }

        Release(top >> 8);
        _held = (int)(top & 0xFF);
    }

    /// <summary>Writes the held byte and the run of 0xFF bytes after it, with a carry into them.</summary>
    private void Release(uint carry)
    {
        if (_held >= 0)
        {
            Write((byte)(_held + carry));
        }

        byte filler = (byte)(0xFF + carry);
        for (; _run > 0; _run--)
        {
            Write(filler);
        }

        _held = -1;
    }

    private void Write(byte value)
    {
        if (_length == _data.Length)
        {
            Array.Resize(ref _data, _data.Length * 2);
        }

        _data[_length++] = value;
    }
}
