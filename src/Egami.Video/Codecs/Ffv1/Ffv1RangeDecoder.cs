using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Video.Codecs.Ffv1;

/// <summary>
/// FFV1's binary range decoder (RFC 9043 section 3.8.1): a 16-bit range split by an 8-bit adaptive state, refilled one byte at a
/// time. Bytes past the end read as zero (Closed mode); <see cref="Position"/> tells where a Sentinel-mode stream ended.
/// </summary>
internal struct Ffv1RangeDecoder
{
    private readonly byte[] _data;
    private int _pos;
    private readonly int _end;
    private uint _low;
    private uint _range;
    private Ffv1StateTable _table;
    private byte[] _one;
    private byte[] _zero;

    /// <summary>Starts decoding <paramref name="data"/> from <paramref name="start"/> up to <paramref name="end"/>.</summary>
    public Ffv1RangeDecoder(byte[] data, int start, int end, Ffv1StateTable table)
    {
        _data = data;
        _pos = start;
        _end = end;
        _table = table;
        _one = table.One;
        _zero = table.Zero;
        _range = 0xFF00;
        _low = (uint)(Next() << 8);
        _low |= (uint)Next();
        if (_low >= _range)
        {
            _low = _range;
        }
    }

    /// <summary>Gets the index of the next byte the decoder would read.</summary>
    public readonly int Position => _pos;

    /// <summary>Gets or sets the state transition table the decoder adapts its states with.</summary>
    public Ffv1StateTable Table
    {
        readonly get => _table;
        set
        {
            _table = value;
            _one = value.One;
            _zero = value.Zero;
        }
    }

    /// <summary>Reads one bit with an adaptive state (get_rac).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBit(ref byte state)
    {
        uint split = (_range * state) >> 8;
        _range -= split;
        int bit;
        if (_low < _range)
        {
            state = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_zero), state);
            bit = 0;
        }
        else
        {
            _low -= _range;
            _range = split;
            state = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_one), state);
            bit = 1;
        }

        if (_range < 0x100)
        {
            _range <<= 8;
            _low <<= 8;
            if (_pos < _end)
            {
                _low += _data[_pos++];
            }
        }

        return bit;
    }

    /// <summary>Reads a scalar with the 32 adaptive states starting at <paramref name="states"/> (get_symbol).</summary>
    public int ReadSymbol(Span<byte> states, bool signed)
    {
        if (ReadBit(ref states[0]) != 0)
        {
            return 0;
        }

        int e = 0;
        while (ReadBit(ref states[1 + Math.Min(e, 9)]) != 0)
        {
            e++;
            if (e > 31)
            {
                throw new CorruptDataException("FFV1", -1, "An FFV1 range-coded number is too large.");
            }
        }

        int a = 1;
        for (int i = e - 1; i >= 0; i--)
        {
            a = (a << 1) | ReadBit(ref states[22 + Math.Min(i, 9)]);
        }

        return signed && ReadBit(ref states[11 + Math.Min(e, 10)]) != 0 ? -a : a;
    }

    /// <summary>
    /// Reads a signed scalar with the 32 adaptive states starting at <paramref name="states"/>, as <see cref="ReadSymbol"/> does,
    /// without bounds checks: the caller passes the first of 32 states it owns.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadSigned(ref byte states)
    {
        if (ReadBit(ref states) != 0)
        {
            return 0;
        }

        return ReadSignedTail(ref states);
    }

    private int ReadSignedTail(ref byte states)
    {
        int e = 0;
        while (ReadBit(ref Unsafe.Add(ref states, 1 + Math.Min(e, 9))) != 0)
        {
            if (++e > 31)
            {
                throw new CorruptDataException("FFV1", -1, "An FFV1 range-coded number is too large.");
            }
        }

        int a = 1;
        for (int i = e - 1; i >= 0; i--)
        {
            a = (a << 1) | ReadBit(ref Unsafe.Add(ref states, 22 + Math.Min(i, 9)));
        }

        return ReadBit(ref Unsafe.Add(ref states, 11 + Math.Min(e, 10))) != 0 ? -a : a;
    }

    /// <summary>Reads an unsigned header field and checks it is at most <paramref name="max"/>.</summary>
    public int ReadField(Span<byte> states, int max, string name)
    {
        int value = ReadSymbol(states, false);
        if ((uint)value > (uint)max)
        {
            throw new CorruptDataException("FFV1", -1, $"FFV1 {name} {value} is out of range (at most {max}).");
        }

        return value;
    }

    /// <summary>Reads a header flag (br) with the first of the header's states.</summary>
    public bool ReadFlag(Span<byte> states) => ReadBit(ref states[0]) != 0;

    private int Next() => _pos < _end ? _data[_pos++] : 0;
}

/// <summary>A range coder state transition table: where a state goes after a 1 and after a 0.</summary>
internal sealed class Ffv1StateTable
{
    /// <summary>The default table (RFC 9043 figure 24).</summary>
    public static readonly Ffv1StateTable Default = new(DefaultOne());

    public Ffv1StateTable(byte[] one)
    {
        One = one;
        Zero = new byte[256];
        for (int i = 1; i < 256; i++)
        {
            Zero[i] = (byte)(256 - one[256 - i]);
        }
    }

    /// <summary>Gets the next state after a 1.</summary>
    public byte[] One { get; }

    /// <summary>Gets the next state after a 0.</summary>
    public byte[] Zero { get; }

    /// <summary>The default table's next states after a 1, which custom tables are coded against.</summary>
    internal static byte[] DefaultOne() =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 20, 21, 22, 23, 24, 25, 26, 27,
        28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 37, 38, 39, 40, 41, 42,
        43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 56, 57,
        58, 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73,
        74, 75, 75, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88,
        89, 90, 91, 92, 93, 94, 94, 95, 96, 97, 98, 99, 100, 101, 102, 103,
        104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 114, 115, 116, 117, 118,
        119, 120, 121, 122, 123, 124, 125, 126, 127, 128, 129, 130, 131, 132, 133, 133,
        134, 135, 136, 137, 138, 139, 140, 141, 142, 143, 144, 145, 146, 147, 148, 149,
        150, 151, 152, 152, 153, 154, 155, 156, 157, 158, 159, 160, 161, 162, 163, 164,
        165, 166, 167, 168, 169, 170, 171, 171, 172, 173, 174, 175, 176, 177, 178, 179,
        180, 181, 182, 183, 184, 185, 186, 187, 188, 189, 190, 190, 191, 192, 194, 194,
        195, 196, 197, 198, 199, 200, 201, 202, 202, 204, 205, 206, 207, 208, 209, 209,
        210, 211, 212, 213, 215, 215, 216, 217, 218, 219, 220, 220, 222, 223, 224, 225,
        226, 227, 227, 229, 229, 230, 231, 232, 234, 234, 235, 236, 237, 238, 239, 240,
        241, 242, 243, 244, 245, 246, 247, 248, 248, 0, 0, 0, 0, 0, 0, 0,
    ];
}
