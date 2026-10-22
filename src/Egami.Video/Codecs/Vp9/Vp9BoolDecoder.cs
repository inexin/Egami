using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// VP9's boolean (arithmetic) decoder, as libvpx's vpx_reader runs it: a 64-bit window refilled eight bytes at a time, which
/// counts how far it read past the end of its data so a cut-off tile can be reported. The compressed header and each tile have
/// their own.
/// </summary>
internal struct Vp9BoolDecoder
{
    private const int ValueBits = 64;
    private const int LotsOfBits = 0x40000000;

    // Four fields, so the JIT can keep a copy of the decoder in registers (DecodeCoefs does); the input is in a class of its own.
    private ulong _value;
    private uint _range;
    private int _count;
    private Input _input;

    /// <summary>Starts reading <paramref name="length"/> bytes at <paramref name="start"/>.</summary>
    /// <returns>Whether the marker bit, which must be 0, was set: the data is broken.</returns>
    public bool Init(byte[] buffer, int start, int length)
    {
        _input = new Input { Buffer = buffer, Position = start, End = start + length };
        _value = 0;
        _count = -8;
        _range = 255;
        Fill();
        return ReadBit() != 0;
    }

    /// <summary>Gets whether bits were read past the end of the data.</summary>
    public readonly bool HasError => _count > ValueBits && _count < LotsOfBits;

    /// <summary>Gets the offset just past the last byte the decoder used (libvpx's vpx_reader_find_end).</summary>
    public int FindEnd()
    {
        int count = _count, position = _input.Position;
        while (count > 8 && count < ValueBits)
        {
            count -= 8;
            position--;
        }

        return position;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Read(int probability)
    {
        uint split = ((_range * (uint)probability) + (256 - (uint)probability)) >> 8;
        if (_count < 0)
        {
            Fill();
        }

        ulong value = _value;
        ulong bigSplit = (ulong)split << (ValueBits - 8);
        uint range = split;
        int bit = 0;
        if (value >= bigSplit)
        {
            range = _range - split;
            value -= bigSplit;
            bit = 1;
        }

        int shift = Norm(range);
        _range = range << shift;
        _value = value << shift;
        _count -= shift;
        return bit;
    }

    public int ReadBit() => Read(128);

    public int ReadLiteral(int bits)
    {
        int literal = 0;
        for (int bit = bits - 1; bit >= 0; bit--)
        {
            literal |= ReadBit() << bit;
        }

        return literal;
    }

    /// <summary>Reads a symbol coded with a VP9 tree (vpx_read_tree): pairs of entries, a value of 0 or below being a leaf.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadTree(ReadOnlySpan<sbyte> tree, ReadOnlySpan<byte> probabilities)
    {
        int i = 0;
        while ((i = tree[i + Read(probabilities[i >> 1])]) > 0)
        {
        }

        return -i;
    }

    /// <summary>The number of leading zero bits of an 8-bit range below 128 (vpx_norm), so the range is renormalized to 128..255.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Norm(uint range) => System.Numerics.BitOperations.LeadingZeroCount(range | 1) - 24;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Fill()
    {
        Input input = _input;
        int bytesLeft = input.End - input.Position;
        long bitsLeft = (long)bytesLeft * 8;
        int shift = ValueBits - 8 - (_count + 8);
        if (bitsLeft > ValueBits)
        {
            int bits = (shift & ~7) + 8;
            ulong bigEndian = BinaryPrimitives.ReadUInt64BigEndian(input.Buffer.AsSpan(input.Position, 8));
            ulong next = bigEndian >> (ValueBits - bits);
            _count += bits;
            input.Position += bits >> 3;
            _value |= next << (shift & 7);
            return;
        }

        int bitsOver = shift + 8 - (int)bitsLeft;
        int loopEnd = 0;
        if (bitsOver >= 0)
        {
            _count += LotsOfBits;
            loopEnd = bitsOver;
        }

        if (bitsOver < 0 || bitsLeft > 0)
        {
            while (shift >= loopEnd)
            {
                _count += 8;
                _value |= (ulong)input.Buffer[input.Position++] << shift;
                shift -= 8;
            }
        }
    }

    /// <summary>The data a decoder reads and how far it has read.</summary>
    private sealed class Input
    {
        public byte[] Buffer = [];
        public int Position;
        public int End;
    }
}
