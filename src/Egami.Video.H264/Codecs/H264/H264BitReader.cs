using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Egami.Video.Codecs.H264;

/// <summary>
/// A fast reader of the CAVLC slice data: the RBSP sits in a buffer with zero padding, so peeking 32 bits never runs off the end;
/// reading past the data reads zeros, which <see cref="Overrun"/> reports.
/// </summary>
internal sealed class H264BitReader
{
    private byte[] _data = [];
    private int _end;
    private int _bit;

    /// <summary>Gets the bit position.</summary>
    internal int Position => _bit;

    /// <summary>Gets whether the reader has read past the data.</summary>
    internal bool Overrun => _bit > _end * 8;

    /// <summary>Gets the bits left before the end of the data.</summary>
    internal int BitsLeft => (_end * 8) - _bit;

    /// <summary>Starts reading a padded buffer whose data is <paramref name="end"/> bytes long, at a bit position.</summary>
    internal void Start(byte[] padded, int end, int bit)
    {
        _data = padded;
        _end = end;
        _bit = bit;
    }

    /// <summary>Returns the next 32 bits without reading them, most significant first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint Peek32()
    {
        int index = _bit >> 3;
        ulong word = BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan(index, 8));
        return (uint)(word >> (32 - (_bit & 7)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Skip(int n) => _bit = Math.Min(_bit + n, (_end * 8) + 64);

    /// <summary>Reads n bits (0 to 25).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Read(int n)
    {
        if (n == 0)
        {
            return 0;
        }

        int value = (int)(Peek32() >> (32 - n));
        Skip(n);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Flag() => Read(1) != 0;

    /// <summary>Reads ue(v) up to 2^16 - 2 in one peek, longer codes in two.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint Ue()
    {
        uint peek = Peek32();
        int zeros = BitOperations.LeadingZeroCount(peek);
        if (zeros < 16)
        {
            Skip((2 * zeros) + 1);
            return (peek >> (31 - (2 * zeros))) - 1;
        }

        return LongUe(zeros);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Se()
    {
        uint code = Ue();
        return (code & 1) != 0 ? (int)((code + 1) >> 1) : -(int)(code >> 1);
    }

    /// <summary>Reads ue(v) and checks it against a maximum.</summary>
    internal uint Ue(uint max, string name)
    {
        uint value = Ue();
        return value <= max ? value : throw H264Errors.Corrupt($"{name} is {value}, more than {max}.");
    }

    /// <summary>Gets whether more RBSP data comes before the trailing bits (7.2, more_rbsp_data).</summary>
    internal bool MoreRbspData()
    {
        int left = BitsLeft;
        if (left <= 0)
        {
            return false;
        }

        // The last 1 bit is rbsp_stop_one_bit: more data when a 1 bit comes before it.
        int last = _end - 1;
        while (last >= 0 && _data[last] == 0)
        {
            last--;
        }

        if (last < 0)
        {
            return false;
        }

        int stop = (last * 8) + 7 - BitOperations.TrailingZeroCount(_data[last]);
        return _bit < stop;
    }

    private uint LongUe(int zeros)
    {
        if (zeros > 31)
        {
            throw H264Errors.Corrupt("An Exp-Golomb code is longer than 32 bits.");
        }

        Skip(zeros + 1);
        uint high = (uint)Read(zeros - 16) << 16;
        return ((1u << zeros) - 1) + high + (uint)Read(16);
    }
}

/// <summary>
/// A variable length code table for prefix codes with up to 16 leading zeros and a few bits after the first 1 (the CAVLC tables
/// of Tables 9-5 to 9-10): looked up by the number of leading zeros and the bits that follow. A code of only zeros, which a
/// prefix-free table has at most one of, matches whenever at least that many zeros come.
/// </summary>
internal sealed class H264Vlc
{
    private readonly ushort[] _table;
    private readonly int _suffixBits;
    private readonly int _zeroCap = 16;

    /// <summary>Builds the table from (code, length, value) entries; length 0 entries are skipped.</summary>
    internal H264Vlc(IEnumerable<(int Code, int Length, int Value)> codes)
    {
        List<(int Code, int Length, int Value)> list = [.. codes.Where(c => c.Length > 0)];
        foreach (var (code, length, _) in list)
        {
            if (code == 0)
            {
                _zeroCap = length;
            }
            else
            {
                int zeros = length - (32 - BitOperations.LeadingZeroCount((uint)code));
                _suffixBits = Math.Max(_suffixBits, length - zeros - 1);
            }
        }

        _table = new ushort[17 << _suffixBits];
        foreach (var (code, length, value) in list)
        {
            if (code == 0)
            {
                _table[length << _suffixBits] = (ushort)((value << 5) | length);
                continue;
            }

            int zeros = length - (32 - BitOperations.LeadingZeroCount((uint)code));
            int suffixLength = length - zeros - 1;
            int suffix = code & ((1 << suffixLength) - 1);
            int free = _suffixBits - suffixLength;
            for (int fill = 0; fill < (1 << free); fill++)
            {
                _table[(zeros << _suffixBits) | (suffix << free) | fill] = (ushort)((value << 5) | length);
            }
        }
    }

    /// <summary>Reads one code and returns its value.</summary>
    /// <exception cref="CorruptDataException">The bits are no code of the table.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Read(H264BitReader r)
    {
        uint peek = r.Peek32();
        int zeros = Math.Min(BitOperations.LeadingZeroCount(peek), _zeroCap);
        int index = zeros << _suffixBits;
        if (zeros < _zeroCap && _suffixBits > 0)
        {
            index |= (int)((peek << zeros << 1) >> (32 - _suffixBits));
        }

        int entry = _table[index];
        if (entry == 0)
        {
            throw H264Errors.Corrupt("A CAVLC code is not in its table.");
        }

        r.Skip(entry & 31);
        return entry >> 5;
    }
}
