namespace Egami.Heic.Hevc;

/// <summary>
/// Reads the fixed- and variable-length codes of an HEVC RBSP (H.265 7.2, 9.2): u(n), ue(v), se(v), most significant bit first.
/// The bytes are an RBSP, emulation prevention already removed (<see cref="HevcNal.Unescape"/>). Reading past the end throws
/// <see cref="CorruptDataException"/>. When <see cref="Trace"/> is set, every element read is added to it by name, the names H.265
/// and HM's syntax trace use, so headers can be compared with HM's.
/// </summary>
internal sealed class HevcBitReader
{
    private readonly byte[] _data;
    private readonly int _end;
    private long _bit;

    /// <summary>Initializes a reader over an RBSP, from <paramref name="start"/> (in bytes) to the end of the array.</summary>
    internal HevcBitReader(byte[] rbsp, int start = 0)
    {
        _data = rbsp;
        _end = rbsp.Length;
        _bit = (long)start * 8;
    }

    /// <summary>Gets or sets the list each element read is added to, or <see langword="null"/>.</summary>
    internal List<(string Name, long Value)>? Trace { get; set; }

    /// <summary>Gets the position, in bits from the start of the RBSP.</summary>
    internal long BitPosition => _bit;

    /// <summary>Gets the bits left before the end of the RBSP.</summary>
    internal long BitsLeft => ((long)_end * 8) - _bit;

    /// <summary>Gets whether the position is at a byte boundary.</summary>
    internal bool ByteAligned => (_bit & 7) == 0;

    /// <summary>Gets the byte the position is in (or starts, when aligned).</summary>
    internal int BytePosition => (int)(_bit >> 3);

    /// <summary>Reads n bits (0 to 32) as an unsigned number: u(n).</summary>
    internal uint U(int n, string name)
    {
        uint value = Bits(n);
        Trace?.Add((name, value));
        return value;
    }

    /// <summary>Reads one bit as a flag: u(1).</summary>
    internal bool Flag(string name) => U(1, name) != 0;

    /// <summary>Reads an unsigned Exp-Golomb code: ue(v).</summary>
    internal uint Ue(string name)
    {
        uint value = ExpGolomb();
        Trace?.Add((name, value));
        return value;
    }

    /// <summary>Reads a signed Exp-Golomb code: se(v).</summary>
    internal int Se(string name)
    {
        uint code = ExpGolomb();
        int value = (code & 1) != 0 ? (int)((code + 1) >> 1) : -(int)(code >> 1);
        Trace?.Add((name, value));
        return value;
    }

    /// <summary>Reads an unsigned Exp-Golomb code checked against a maximum, as the syntax limits it.</summary>
    internal uint Ue(string name, uint max)
    {
        uint value = Ue(name);
        return value <= max ? value : throw HevcErrors.Corrupt($"{name} is {value}, more than {max}.");
    }

    /// <summary>Reads a signed Exp-Golomb code checked against a range.</summary>
    internal int Se(string name, int min, int max)
    {
        int value = Se(name);
        return value >= min && value <= max ? value : throw HevcErrors.Corrupt($"{name} is {value}, outside {min} to {max}.");
    }

    /// <summary>Skips n bits.</summary>
    internal void Skip(long n)
    {
        if (n > BitsLeft)
        {
            throw HevcErrors.Corrupt("A NAL unit is cut off.");
        }

        _bit += n;
    }

    /// <summary>Gets whether more RBSP data comes before the trailing bits (H.265 7.2, more_rbsp_data).</summary>
    internal bool MoreRbspData()
    {
        long left = BitsLeft;
        if (left <= 0)
        {
            return false;
        }

        // The last 1 bit of the RBSP is rbsp_stop_one_bit; there is more data when a 1 bit comes before it.
        int last = _end - 1;
        while (last >= 0 && _data[last] == 0)
        {
            last--;
        }

        if (last < 0)
        {
            return false;
        }

        long stop = ((long)last * 8) + 7 - System.Numerics.BitOperations.TrailingZeroCount(_data[last]);
        return _bit < stop;
    }

    /// <summary>Reads rbsp_trailing_bits: the stop bit and the zero bits to the byte boundary.</summary>
    internal void TrailingBits()
    {
        if (U(1, "rbsp_stop_one_bit") != 1)
        {
            throw HevcErrors.Corrupt("rbsp_stop_one_bit is 0.");
        }

        while (!ByteAligned)
        {
            U(1, "rbsp_alignment_zero_bit");
        }
    }

    /// <summary>Reads byte_alignment(): a 1 bit, then zero bits to the byte boundary (not traced, as HM does not trace it).</summary>
    internal void ByteAlignment()
    {
        if (Bits(1) != 1)
        {
            throw HevcErrors.Corrupt("alignment_bit_equal_to_one is 0.");
        }

        while (!ByteAligned)
        {
            Bits(1);
        }
    }

    private uint Bits(int n)
    {
        if (n == 0)
        {
            return 0;
        }

        if (n > BitsLeft)
        {
            throw HevcErrors.Corrupt("A NAL unit is cut off.");
        }

        ulong value = 0;
        int got = 0;
        while (got < n)
        {
            int index = (int)(_bit >> 3), offset = (int)(_bit & 7);
            int take = Math.Min(8 - offset, n - got);
            int bits = (_data[index] >> (8 - offset - take)) & ((1 << take) - 1);
            value = (value << take) | (uint)bits;
            got += take;
            _bit += take;
        }

        return (uint)value;
    }

    private uint ExpGolomb()
    {
        int zeros = 0;
        while (Bits(1) == 0)
        {
            if (++zeros > 31)
            {
                throw HevcErrors.Corrupt("An Exp-Golomb code is longer than 32 bits.");
            }
        }

        return (uint)(((1UL << zeros) - 1) + Bits(zeros));
    }
}

/// <summary>Builds the exceptions the HEVC decoder throws.</summary>
internal static class HevcErrors
{
    /// <summary>The format name errors carry.</summary>
    internal const string FormatName = "HEIC";

    internal static CorruptDataException Corrupt(string problem) => new(FormatName, -1, "HEVC: " + problem);

    internal static UnsupportedFormatException Unsupported(string what) => new($"HEVC: {what} Egami.Heic does not decode it (2.9.4 decodes intra pictures of the Main, Main 10, Main Still Picture and format range extensions profiles up to 12 bits).");
}
