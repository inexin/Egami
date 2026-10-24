using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Nal;

/// <summary>
/// The CABAC arithmetic decoding engine, which H.264 (9.3.3.2) and HEVC (H.265 9.3.4.3) share bin for bin, reading whole bytes
/// ahead as HM does: the offset holds 16 bits and more bits come in when <c>_bitsNeeded</c> reaches zero. After a terminating bin
/// equal to 1 (end of slice or slice segment, end of a tile or wavefront row, pcm_flag), the byte position is the next
/// byte-aligned byte of the bitstream. The context tables and their initialisation belong to each codec.
/// </summary>
internal sealed class CabacEngine
{
    /// <summary>rangeTabLps (H.265 Table 9-52, H.264 Table 9-44), by pStateIdx and qRangeIdx.</summary>
    private static ReadOnlySpan<byte> RangeLps =>
    [
        128, 176, 208, 240, 128, 167, 197, 227, 128, 158, 187, 216, 123, 150, 178, 205, 116, 142, 169, 195, 111, 135, 160, 185,
        105, 128, 152, 175, 100, 122, 144, 166, 95, 116, 137, 158, 90, 110, 130, 150, 85, 104, 123, 142, 81, 99, 117, 135,
        77, 94, 111, 128, 73, 89, 105, 122, 69, 85, 100, 116, 66, 80, 95, 110, 62, 76, 90, 104, 59, 72, 86, 99,
        56, 69, 81, 94, 53, 65, 77, 89, 51, 62, 73, 85, 48, 59, 69, 80, 46, 56, 66, 76, 43, 53, 63, 72,
        41, 50, 59, 69, 39, 48, 56, 65, 37, 45, 54, 62, 35, 43, 51, 59, 33, 41, 48, 56, 32, 39, 46, 53,
        30, 37, 43, 50, 29, 35, 41, 48, 27, 33, 39, 45, 26, 31, 37, 43, 24, 30, 35, 41, 23, 28, 33, 39,
        22, 27, 32, 37, 21, 26, 30, 35, 20, 24, 29, 33, 19, 23, 27, 31, 18, 22, 26, 30, 17, 21, 25, 28,
        16, 20, 23, 27, 15, 19, 22, 25, 14, 18, 21, 24, 14, 17, 20, 23, 13, 16, 19, 22, 12, 15, 18, 21,
        12, 14, 17, 20, 11, 14, 16, 19, 11, 13, 15, 18, 10, 12, 15, 17, 10, 12, 14, 16, 9, 11, 13, 15,
        9, 11, 12, 14, 8, 10, 12, 14, 8, 9, 11, 13, 7, 9, 11, 12, 7, 9, 10, 12, 7, 8, 10, 11,
        6, 8, 9, 11, 6, 7, 9, 10, 6, 7, 8, 9, 2, 2, 2, 2,
    ];

    /// <summary>transIdxLps (H.265 Table 9-53, H.264 Table 9-45).</summary>
    private static ReadOnlySpan<byte> TransitionLps =>
    [
        0, 0, 1, 2, 2, 4, 4, 5, 6, 7, 8, 9, 9, 11, 11, 12, 13, 13, 15, 15, 16, 16, 18, 18, 19, 19, 21, 21, 22, 22, 23, 24,
        24, 25, 26, 26, 27, 27, 28, 29, 29, 30, 30, 30, 31, 32, 32, 33, 33, 33, 34, 34, 35, 35, 35, 36, 36, 36, 37, 37, 37, 38, 38, 63,
    ];

    /// <summary>
    /// rangeTabLps and both transitions in one entry per state and qRangeIdx, indexed by (state &lt;&lt; 2) | qRangeIdx: the LPS range
    /// in bits 0 to 7, the state after the more probable symbol in bits 8 to 15 and after the less probable one in bits 16 to 23,
    /// so a bin needs one table load (the layout libavc uses).
    /// </summary>
    private static readonly uint[] Packed = MakePacked();

    private byte[] _data = [];
    private int _position;
    private int _end;
    private uint _range;
    private uint _value;
    private int _bitsNeeded;

    /// <summary>Gets the data the engine reads, for raw samples (pcm_sample) that follow a terminating 1 bin.</summary>
    internal byte[] Data => _data;

    /// <summary>Gets the end of the data, as <see cref="Start"/> was given it.</summary>
    internal int End => _end;

    /// <summary>Gets the byte position: after a terminating 1 bin, where the next byte-aligned data starts.</summary>
    internal int Position => _position;

    /// <summary>Gets whether the engine has read past the end of its data (the stream is damaged).</summary>
    internal bool Overrun => _position > _end + 2;

    /// <summary>Starts decoding at a byte position (H.265 9.3.2.5, H.264 9.3.1.2): reads 9 bits into the offset.</summary>
    internal void Start(byte[] data, int position, int end)
    {
        _data = data;
        _position = position;
        _end = end;
        _range = 510;
        _bitsNeeded = -8;
        _value = (uint)((ReadByte() << 8) | ReadByte());
    }

    /// <summary>Decodes a context-coded bin (H.265 9.3.4.3.2, H.264 9.3.3.2.1). A state is (pStateIdx &lt;&lt; 1) | valMps.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Decode(ref byte state)
    {
        int s = state;
        uint lps = Unsafe.Add(ref MemoryMarshal.GetReference(RangeLps), ((s >> 1) << 2) + (int)((_range >> 6) & 3));
        _range -= lps;
        uint scaledRange = _range << 7;
        int bin;
        if (_value < scaledRange)
        {
            bin = s & 1;
            state = (byte)(s < 124 ? s + 2 : s);
            if (scaledRange < (256 << 7))
            {
                _range = scaledRange >> 6;
                _value <<= 1;
                if (++_bitsNeeded == 0)
                {
                    _bitsNeeded = -8;
                    _value |= (uint)ReadByte();
                }
            }
        }
        else
        {
            bin = (s & 1) ^ 1;
            int p = s >> 1;
            state = (byte)((Unsafe.Add(ref MemoryMarshal.GetReference(TransitionLps), p) << 1) | (p == 0 ? bin : s & 1));
            int shift = System.Numerics.BitOperations.LeadingZeroCount(lps) - 23;
            _value = (_value - scaledRange) << shift;
            _range = lps << shift;
            _bitsNeeded += shift;
            if (_bitsNeeded >= 0)
            {
                _value |= (uint)ReadByte() << _bitsNeeded;
                _bitsNeeded -= 8;
            }
        }

        return bin;
    }

    /// <summary>
    /// Gets the engine's state, for a hot loop that decodes bins with <see cref="Decode(ref State, ref byte)"/> and
    /// <see cref="Bypass(ref State)"/>, whose state the JIT keeps in registers; <see cref="Restore"/> gives it back.
    /// </summary>
    internal State Save() => new() { Range = _range, Value = _value, BitsNeeded = _bitsNeeded, Position = _position };

    /// <summary>Takes back the state <see cref="Save"/> gave.</summary>
    internal void Restore(in State state)
    {
        _range = state.Range;
        _value = state.Value;
        _bitsNeeded = state.BitsNeeded;
        _position = state.Position;
    }

    /// <summary><see cref="Decode(ref byte)"/> on a saved state.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Decode(ref State e, ref byte state)
    {
        // One load gives the LPS range and the state after either symbol.
        int s = state;
        uint entry = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Packed), (s << 2) + (int)((e.Range >> 6) & 3));
        uint lps = entry & 0xFF;
        e.Range -= lps;
        uint scaledRange = e.Range << 7;
        int bin;
        if (e.Value < scaledRange)
        {
            bin = s & 1;
            state = (byte)(entry >> 8);
            if (scaledRange < (256 << 7))
            {
                e.Range = scaledRange >> 6;
                e.Value <<= 1;
                if (++e.BitsNeeded == 0)
                {
                    e.BitsNeeded = -8;
                    e.Value |= (uint)ReadByte(ref e.Position);
                }
            }
        }
        else
        {
            bin = (s & 1) ^ 1;
            state = (byte)(entry >> 16);
            int shift = System.Numerics.BitOperations.LeadingZeroCount(lps) - 23;
            e.Value = (e.Value - scaledRange) << shift;
            e.Range = lps << shift;
            e.BitsNeeded += shift;
            if (e.BitsNeeded >= 0)
            {
                e.Value |= (uint)ReadByte(ref e.Position) << e.BitsNeeded;
                e.BitsNeeded -= 8;
            }
        }

        return bin;
    }

    /// <summary><see cref="Bypass()"/> on a saved state.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Bypass(ref State e)
    {
        e.Value <<= 1;
        if (++e.BitsNeeded >= 0)
        {
            e.BitsNeeded = -8;
            e.Value |= (uint)ReadByte(ref e.Position);
        }

        uint scaledRange = e.Range << 7;
        if (e.Value >= scaledRange)
        {
            e.Value -= scaledRange;
            return 1;
        }

        return 0;
    }

    /// <summary>Decodes a bypass bin (H.265 9.3.4.3.4, H.264 9.3.3.2.3).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Bypass()
    {
        _value <<= 1;
        if (++_bitsNeeded >= 0)
        {
            _bitsNeeded = -8;
            _value |= (uint)ReadByte();
        }

        uint scaledRange = _range << 7;
        if (_value >= scaledRange)
        {
            _value -= scaledRange;
            return 1;
        }

        return 0;
    }

    /// <summary>Decodes <paramref name="count"/> bypass bins (up to 32), most significant first.</summary>
    internal uint Bypass(int count)
    {
        uint bins = 0;
        for (int i = 0; i < count; i++)
        {
            bins = (bins << 1) | (uint)Bypass();
        }

        return bins;
    }

    /// <summary>Decodes a terminating bin (H.265 9.3.4.3.5, H.264 9.3.3.2.2).</summary>
    internal int Terminate()
    {
        _range -= 2;
        uint scaledRange = _range << 7;
        if (_value >= scaledRange)
        {
            return 1;
        }

        if (scaledRange < (256 << 7))
        {
            _range = scaledRange >> 6;
            _value <<= 1;
            if (++_bitsNeeded == 0)
            {
                _bitsNeeded = -8;
                _value |= (uint)ReadByte();
            }
        }

        return 0;
    }

    private static uint[] MakePacked()
    {
        uint[] packed = new uint[512];
        for (int s = 0; s < 128; s++)
        {
            int p = s >> 1, mps = s & 1;
            uint nextMps = (uint)(s < 124 ? s + 2 : s);
            uint nextLps = (uint)((TransitionLps[p] << 1) | (p == 0 ? mps ^ 1 : mps));
            for (int q = 0; q < 4; q++)
            {
                packed[(s << 2) + q] = RangeLps[(p << 2) + q] | (nextMps << 8) | (nextLps << 16);
            }
        }

        return packed;
    }

    /// <summary>Reads raw bits from the byte position (pcm_sample after a terminating pcm_flag), most significant first.</summary>
    internal sealed class RawReader(byte[] data, int position)
    {
        private long _bit = (long)position * 8;

        internal int Read(int n)
        {
            int value = 0;
            for (int i = 0; i < n; i++)
            {
                int index = (int)(_bit >> 3);
                int b = index < data.Length ? (data[index] >> (7 - (int)(_bit & 7))) & 1 : 0;
                value = (value << 1) | b;
                _bit++;
            }

            return value;
        }

        /// <summary>Gets the byte after the bits read, rounded up.</summary>
        internal int BytePosition => (int)((_bit + 7) >> 3);
    }

    /// <summary>The engine's state as <see cref="Save"/> gives it.</summary>
    internal struct State
    {
        internal uint Range;
        internal uint Value;
        internal int BitsNeeded;
        internal int Position;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadByte(ref int position)
    {
        if (position < _end)
        {
            return _data[position++];
        }

        position++;
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadByte()
    {
        if (_position < _end)
        {
            return _data[_position++];
        }

        // Past the end the stream is damaged; zeros keep the engine going until the caller checks Overrun.
        _position++;
        return 0;
    }
}
