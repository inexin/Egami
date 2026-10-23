using System.Runtime.CompilerServices;

namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// VP9's boolean (arithmetic) encoder, as libvpx 1.14.1's vpx_writer runs it (vpx_dsp/bitwriter.h, BSD-3; see
/// THIRD-PARTY-NOTICES), into a buffer that grows as needed. <see cref="Stop"/> pads with 32 zero bits as vpx_stop_encode does,
/// and adds a zero byte when the last one would look like a superframe index marker.
/// </summary>
internal struct Vp9BoolEncoder
{
    private byte[] _buffer;
    private int _start;
    private int _pos;
    private uint _low;
    private uint _range;
    private int _count;

    /// <summary>Starts writing at <paramref name="start"/> in <paramref name="buffer"/>, which grows when it runs out.</summary>
    public void Start(byte[] buffer, int start)
    {
        _buffer = buffer;
        _start = start;
        _pos = start;
        _low = 0;
        _range = 255;
        _count = -24;
        WriteBit(0);
    }

    /// <summary>Gets the buffer, which may have been replaced by a larger one.</summary>
    public readonly byte[] Buffer => _buffer;

    /// <summary>Gets the bytes written so far.</summary>
    public readonly int Length => _pos - _start;

    /// <summary>Flushes the coder; returns the bytes written since <see cref="Start"/>.</summary>
    public int Stop()
    {
        for (int i = 0; i < 32; i++)
        {
            WriteBit(0);
        }

        if ((_buffer[_pos - 1] & 0xe0) == 0xc0)
        {
            Ensure();
            _buffer[_pos++] = 0;
        }

        return _pos - _start;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(int bit, int probability)
    {
        uint split = 1 + (((_range - 1) * (uint)probability) >> 8);
        uint range = split;
        uint low = _low;
        if (bit != 0)
        {
            low += split;
            range = _range - split;
        }

        int shift = System.Numerics.BitOperations.LeadingZeroCount(range) - 24;
        range <<= shift;
        int count = _count + shift;
        if (count >= 0)
        {
            int offset = shift - count;
            Emit(low, offset);
            low <<= offset;
            shift = count;
            low &= 0xffffff;
            count -= 8;
        }

        _low = low << shift;
        _count = count;
        _range = range;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Emit(uint low, int offset)
    {
        if (((low << (offset - 1)) & 0x80000000) != 0)
        {
            int x = _pos - 1;
            while (x >= _start && _buffer[x] == 0xff)
            {
                _buffer[x] = 0;
                x--;
            }

            _buffer[x]++;
        }

        Ensure();
        _buffer[_pos++] = (byte)((low >> (24 - offset)) & 0xff);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Ensure()
    {
        if (_pos >= _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, 4096));
        }
    }

    public void WriteBit(int bit) => Write(bit, 128);

    public void WriteLiteral(int value, int bits)
    {
        for (int bit = bits - 1; bit >= 0; bit--)
        {
            WriteBit((value >> bit) & 1);
        }
    }

    /// <summary>Writes a symbol with a VP9 tree (vpx_write_tree): <paramref name="bits"/> holds the path's <paramref name="length"/> branches, first branch highest.</summary>
    public void WriteTree(ReadOnlySpan<sbyte> tree, ReadOnlySpan<byte> probabilities, int bits, int length)
    {
        int i = 0;
        do
        {
            int bit = (bits >> --length) & 1;
            Write(bit, probabilities[i >> 1]);
            i = tree[i + bit];
        }
        while (length != 0);
    }
}

/// <summary>Writes the uncompressed header's bits, most significant first (vpx_wb_write_bit).</summary>
internal ref struct Vp9BitWriter
{
    private readonly Span<byte> _data;
    private int _bit;

    public Vp9BitWriter(Span<byte> data)
    {
        _data = data;
        _data.Clear();
    }

    /// <summary>Gets the bytes used so far, the last one counted when partly written.</summary>
    public readonly int BytesWritten => (_bit + 7) >> 3;

    public readonly int BitPosition => _bit;

    public void Bit(int bit)
    {
        if (bit != 0)
        {
            _data[_bit >> 3] |= (byte)(0x80 >> (_bit & 7));
        }

        _bit++;
    }

    public void Literal(int value, int bits)
    {
        for (int i = bits - 1; i >= 0; i--)
        {
            Bit((value >> i) & 1);
        }
    }

    /// <summary>A magnitude then its sign bit (vpx_wb_write_signed_literal's layout in VP9 headers).</summary>
    public void SignedLiteral(int value, int bits)
    {
        Literal(Math.Abs(value), bits);
        Bit(value < 0 ? 1 : 0);
    }

    /// <summary>Overwrites 16 bits at a bit position written earlier (the compressed header's size).</summary>
    public readonly void Patch16(int bitPosition, int value)
    {
        for (int i = 0; i < 16; i++)
        {
            int at = bitPosition + i;
            byte mask = (byte)(0x80 >> (at & 7));
            if (((value >> (15 - i)) & 1) != 0)
            {
                _data[at >> 3] |= mask;
            }
            else
            {
                _data[at >> 3] &= (byte)~mask;
            }
        }
    }
}
