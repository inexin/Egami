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
    /// so a bin needs one table load (the layout libavc uses). For state s = (p &lt;&lt; 1) | valMps the entry is RangeLps[(p &lt;&lt; 2) | q],
    /// then s + 2 (s from 124 on), then (TransitionLps[p] &lt;&lt; 1) | valMps with valMps flipped at p = 0. Constant data, so its address
    /// is a constant in the JIT's code.
    /// </summary>
    private static ReadOnlySpan<uint> Packed =>
    [
        0x010280, 0x0102B0, 0x0102D0, 0x0102F0, 0x000380, 0x0003B0, 0x0003D0, 0x0003F0,
        0x000480, 0x0004A7, 0x0004C5, 0x0004E3, 0x010580, 0x0105A7, 0x0105C5, 0x0105E3,
        0x020680, 0x02069E, 0x0206BB, 0x0206D8, 0x030780, 0x03079E, 0x0307BB, 0x0307D8,
        0x04087B, 0x040896, 0x0408B2, 0x0408CD, 0x05097B, 0x050996, 0x0509B2, 0x0509CD,
        0x040A74, 0x040A8E, 0x040AA9, 0x040AC3, 0x050B74, 0x050B8E, 0x050BA9, 0x050BC3,
        0x080C6F, 0x080C87, 0x080CA0, 0x080CB9, 0x090D6F, 0x090D87, 0x090DA0, 0x090DB9,
        0x080E69, 0x080E80, 0x080E98, 0x080EAF, 0x090F69, 0x090F80, 0x090F98, 0x090FAF,
        0x0A1064, 0x0A107A, 0x0A1090, 0x0A10A6, 0x0B1164, 0x0B117A, 0x0B1190, 0x0B11A6,
        0x0C125F, 0x0C1274, 0x0C1289, 0x0C129E, 0x0D135F, 0x0D1374, 0x0D1389, 0x0D139E,
        0x0E145A, 0x0E146E, 0x0E1482, 0x0E1496, 0x0F155A, 0x0F156E, 0x0F1582, 0x0F1596,
        0x101655, 0x101668, 0x10167B, 0x10168E, 0x111755, 0x111768, 0x11177B, 0x11178E,
        0x121851, 0x121863, 0x121875, 0x121887, 0x131951, 0x131963, 0x131975, 0x131987,
        0x121A4D, 0x121A5E, 0x121A6F, 0x121A80, 0x131B4D, 0x131B5E, 0x131B6F, 0x131B80,
        0x161C49, 0x161C59, 0x161C69, 0x161C7A, 0x171D49, 0x171D59, 0x171D69, 0x171D7A,
        0x161E45, 0x161E55, 0x161E64, 0x161E74, 0x171F45, 0x171F55, 0x171F64, 0x171F74,
        0x182042, 0x182050, 0x18205F, 0x18206E, 0x192142, 0x192150, 0x19215F, 0x19216E,
        0x1A223E, 0x1A224C, 0x1A225A, 0x1A2268, 0x1B233E, 0x1B234C, 0x1B235A, 0x1B2368,
        0x1A243B, 0x1A2448, 0x1A2456, 0x1A2463, 0x1B253B, 0x1B2548, 0x1B2556, 0x1B2563,
        0x1E2638, 0x1E2645, 0x1E2651, 0x1E265E, 0x1F2738, 0x1F2745, 0x1F2751, 0x1F275E,
        0x1E2835, 0x1E2841, 0x1E284D, 0x1E2859, 0x1F2935, 0x1F2941, 0x1F294D, 0x1F2959,
        0x202A33, 0x202A3E, 0x202A49, 0x202A55, 0x212B33, 0x212B3E, 0x212B49, 0x212B55,
        0x202C30, 0x202C3B, 0x202C45, 0x202C50, 0x212D30, 0x212D3B, 0x212D45, 0x212D50,
        0x242E2E, 0x242E38, 0x242E42, 0x242E4C, 0x252F2E, 0x252F38, 0x252F42, 0x252F4C,
        0x24302B, 0x243035, 0x24303F, 0x243048, 0x25312B, 0x253135, 0x25313F, 0x253148,
        0x263229, 0x263232, 0x26323B, 0x263245, 0x273329, 0x273332, 0x27333B, 0x273345,
        0x263427, 0x263430, 0x263438, 0x263441, 0x273527, 0x273530, 0x273538, 0x273541,
        0x2A3625, 0x2A362D, 0x2A3636, 0x2A363E, 0x2B3725, 0x2B372D, 0x2B3736, 0x2B373E,
        0x2A3823, 0x2A382B, 0x2A3833, 0x2A383B, 0x2B3923, 0x2B392B, 0x2B3933, 0x2B393B,
        0x2C3A21, 0x2C3A29, 0x2C3A30, 0x2C3A38, 0x2D3B21, 0x2D3B29, 0x2D3B30, 0x2D3B38,
        0x2C3C20, 0x2C3C27, 0x2C3C2E, 0x2C3C35, 0x2D3D20, 0x2D3D27, 0x2D3D2E, 0x2D3D35,
        0x2E3E1E, 0x2E3E25, 0x2E3E2B, 0x2E3E32, 0x2F3F1E, 0x2F3F25, 0x2F3F2B, 0x2F3F32,
        0x30401D, 0x304023, 0x304029, 0x304030, 0x31411D, 0x314123, 0x314129, 0x314130,
        0x30421B, 0x304221, 0x304227, 0x30422D, 0x31431B, 0x314321, 0x314327, 0x31432D,
        0x32441A, 0x32441F, 0x324425, 0x32442B, 0x33451A, 0x33451F, 0x334525, 0x33452B,
        0x344618, 0x34461E, 0x344623, 0x344629, 0x354718, 0x35471E, 0x354723, 0x354729,
        0x344817, 0x34481C, 0x344821, 0x344827, 0x354917, 0x35491C, 0x354921, 0x354927,
        0x364A16, 0x364A1B, 0x364A20, 0x364A25, 0x374B16, 0x374B1B, 0x374B20, 0x374B25,
        0x364C15, 0x364C1A, 0x364C1E, 0x364C23, 0x374D15, 0x374D1A, 0x374D1E, 0x374D23,
        0x384E14, 0x384E18, 0x384E1D, 0x384E21, 0x394F14, 0x394F18, 0x394F1D, 0x394F21,
        0x3A5013, 0x3A5017, 0x3A501B, 0x3A501F, 0x3B5113, 0x3B5117, 0x3B511B, 0x3B511F,
        0x3A5212, 0x3A5216, 0x3A521A, 0x3A521E, 0x3B5312, 0x3B5316, 0x3B531A, 0x3B531E,
        0x3C5411, 0x3C5415, 0x3C5419, 0x3C541C, 0x3D5511, 0x3D5515, 0x3D5519, 0x3D551C,
        0x3C5610, 0x3C5614, 0x3C5617, 0x3C561B, 0x3D5710, 0x3D5714, 0x3D5717, 0x3D571B,
        0x3C580F, 0x3C5813, 0x3C5816, 0x3C5819, 0x3D590F, 0x3D5913, 0x3D5916, 0x3D5919,
        0x3E5A0E, 0x3E5A12, 0x3E5A15, 0x3E5A18, 0x3F5B0E, 0x3F5B12, 0x3F5B15, 0x3F5B18,
        0x405C0E, 0x405C11, 0x405C14, 0x405C17, 0x415D0E, 0x415D11, 0x415D14, 0x415D17,
        0x405E0D, 0x405E10, 0x405E13, 0x405E16, 0x415F0D, 0x415F10, 0x415F13, 0x415F16,
        0x42600C, 0x42600F, 0x426012, 0x426015, 0x43610C, 0x43610F, 0x436112, 0x436115,
        0x42620C, 0x42620E, 0x426211, 0x426214, 0x43630C, 0x43630E, 0x436311, 0x436314,
        0x42640B, 0x42640E, 0x426410, 0x426413, 0x43650B, 0x43650E, 0x436510, 0x436513,
        0x44660B, 0x44660D, 0x44660F, 0x446612, 0x45670B, 0x45670D, 0x45670F, 0x456712,
        0x44680A, 0x44680C, 0x44680F, 0x446811, 0x45690A, 0x45690C, 0x45690F, 0x456911,
        0x466A0A, 0x466A0C, 0x466A0E, 0x466A10, 0x476B0A, 0x476B0C, 0x476B0E, 0x476B10,
        0x466C09, 0x466C0B, 0x466C0D, 0x466C0F, 0x476D09, 0x476D0B, 0x476D0D, 0x476D0F,
        0x466E09, 0x466E0B, 0x466E0C, 0x466E0E, 0x476F09, 0x476F0B, 0x476F0C, 0x476F0E,
        0x487008, 0x48700A, 0x48700C, 0x48700E, 0x497108, 0x49710A, 0x49710C, 0x49710E,
        0x487208, 0x487209, 0x48720B, 0x48720D, 0x497308, 0x497309, 0x49730B, 0x49730D,
        0x487407, 0x487409, 0x48740B, 0x48740C, 0x497507, 0x497509, 0x49750B, 0x49750C,
        0x4A7607, 0x4A7609, 0x4A760A, 0x4A760C, 0x4B7707, 0x4B7709, 0x4B770A, 0x4B770C,
        0x4A7807, 0x4A7808, 0x4A780A, 0x4A780B, 0x4B7907, 0x4B7908, 0x4B790A, 0x4B790B,
        0x4A7A06, 0x4A7A08, 0x4A7A09, 0x4A7A0B, 0x4B7B06, 0x4B7B08, 0x4B7B09, 0x4B7B0B,
        0x4C7C06, 0x4C7C07, 0x4C7C09, 0x4C7C0A, 0x4D7D06, 0x4D7D07, 0x4D7D09, 0x4D7D0A,
        0x4C7C06, 0x4C7C07, 0x4C7C08, 0x4C7C09, 0x4D7D06, 0x4D7D07, 0x4D7D08, 0x4D7D09,
        0x7E7E02, 0x7E7E02, 0x7E7E02, 0x7E7E02, 0x7F7F02, 0x7F7F02, 0x7F7F02, 0x7F7F02,
    ];

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
        uint entry = Unsafe.Add(ref MemoryMarshal.GetReference(Packed), (s << 2) + (int)((_range >> 6) & 3));
        uint lps = entry & 0xFF;
        _range -= lps;
        uint scaledRange = _range << 7;
        int bin;
        if (_value < scaledRange)
        {
            bin = s & 1;
            state = (byte)(entry >> 8);
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
            state = (byte)(entry >> 16);
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
        uint entry = Unsafe.Add(ref MemoryMarshal.GetReference(Packed), (s << 2) + (int)((e.Range >> 6) & 3));
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
