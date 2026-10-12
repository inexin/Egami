using Egami.Fonts.Parsing;

namespace Egami.Fonts.Cff;

/// <summary>A CFF INDEX: a count, an offset array and the data the offsets point into. Items are sliced on demand.</summary>
internal readonly struct CffIndex
{
    private readonly ReadOnlyMemory<byte> _cff;
    private readonly int _offsetsStart;
    private readonly int _offSize;
    private readonly int _dataStart;

    private CffIndex(ReadOnlyMemory<byte> cff, int count, int offSize, int offsetsStart, int dataStart, int end)
    {
        _cff = cff;
        Count = count;
        _offSize = offSize;
        _offsetsStart = offsetsStart;
        _dataStart = dataStart;
        End = end;
    }

    public int Count { get; }

    /// <summary>Gets the offset just past the INDEX.</summary>
    public int End { get; }

    /// <summary>Gets the INDEX's whole bytes, header included.</summary>
    public ReadOnlyMemory<byte> Raw(int start) => _cff[start..End];

    /// <summary>
    /// Reads the INDEX at <paramref name="offset"/>; an empty INDEX is two zero bytes (four in CFF2, whose INDEX counts are 32-bit).
    /// </summary>
    public static CffIndex Read(ReadOnlyMemory<byte> cff, int offset, bool cff2 = false)
    {
        ReadOnlySpan<byte> data = cff.Span;
        int countSize = cff2 ? 4 : 2;
        uint rawCount = cff2 ? BigEndian.UInt32(data, offset) : BigEndian.UInt16(data, offset);
        if (rawCount == 0)
        {
            return new CffIndex(cff, 0, 1, offset + countSize, offset + countSize, offset + countSize);
        }

        if (offset + countSize + 1 > data.Length || rawCount > (uint)(data.Length / 2))
        {
            throw FontErrors.Truncated(offset);
        }

        int count = (int)rawCount;
        int offSize = data[offset + countSize];
        if (offSize is < 1 or > 4)
        {
            throw FontErrors.Corrupt(offset, $"A CFF INDEX has offset size {offSize}.");
        }

        int offsetsStart = offset + countSize + 1;
        long offsetsEnd = offsetsStart + ((long)(count + 1) * offSize);
        if (offsetsEnd > data.Length)
        {
            throw FontErrors.Truncated(offset);
        }

        int dataStart = (int)offsetsEnd - 1;
        uint last = ReadOffset(data, offsetsStart + (count * offSize), offSize);
        long end = dataStart + (long)last;
        if (last < 1 || end > data.Length)
        {
            throw FontErrors.Corrupt(offset, "A CFF INDEX runs past the end of the data.");
        }

        return new CffIndex(cff, count, offSize, offsetsStart, dataStart, (int)end);
    }

    public ReadOnlySpan<byte> this[int index] => GetMemory(index).Span;

    public ReadOnlyMemory<byte> GetMemory(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        ReadOnlySpan<byte> data = _cff.Span;
        uint start = ReadOffset(data, _offsetsStart + (index * _offSize), _offSize);
        uint end = ReadOffset(data, _offsetsStart + ((index + 1) * _offSize), _offSize);
        if (start < 1 || end < start || _dataStart + (long)end > End)
        {
            throw FontErrors.Corrupt(_offsetsStart, $"Item {index} of a CFF INDEX has bad offsets.");
        }

        return _cff.Slice(_dataStart + (int)start, (int)(end - start));
    }

    private static uint ReadOffset(ReadOnlySpan<byte> data, int at, int size)
    {
        uint value = 0;
        for (int i = 0; i < size; i++)
        {
            value = (value << 8) | data[at + i];
        }

        return value;
    }
}

/// <summary>A parsed CFF DICT: operator to operands. Operators with the 12 escape are stored as 1200 + the second byte.</summary>
internal sealed class CffDict
{
    private readonly Dictionary<int, double[]> _entries = [];

    public bool Contains(int op) => _entries.ContainsKey(op);

    public double[]? Get(int op) => _entries.TryGetValue(op, out double[]? v) ? v : null;

    public double GetNumber(int op, double fallback) => _entries.TryGetValue(op, out double[]? v) && v.Length > 0 ? v[0] : fallback;

    public int GetInt(int op, int fallback)
    {
        double value = GetNumber(op, fallback);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value : fallback;
    }

    public IEnumerable<KeyValuePair<int, double[]>> Entries => _entries;

    /// <summary>
    /// Reads a DICT. In CFF2 (<paramref name="regionCount"/> given) the operators vsindex (22), blend (23) and vstore (24) exist; a
    /// blend keeps its default values, and <paramref name="regionCount"/> says how many deltas each value has for a vsindex.
    /// </summary>
    public static CffDict Read(ReadOnlySpan<byte> data, long offset, Func<int, int>? regionCount = null)
    {
        var dict = new CffDict();
        var operands = new List<double>();
        int vsindex = 0;
        int i = 0;
        while (i < data.Length)
        {
            int b0 = data[i];
            if (b0 <= 21 || (regionCount is not null && b0 is 22 or 23 or 24))
            {
                int op = b0;
                i++;
                if (b0 == 12)
                {
                    if (i >= data.Length)
                    {
                        throw FontErrors.Truncated(offset + i);
                    }

                    op = 1200 + data[i++];
                }

                if (op == 23)
                {
                    // blend: n defaults, n * k deltas, n; the defaults stay as operands.
                    int n = operands.Count > 0 ? (int)operands[^1] : -1;
                    int k = regionCount!(vsindex);
                    long used = 1 + ((long)n * (k + 1));
                    if (n < 0 || used > operands.Count)
                    {
                        throw FontErrors.Corrupt(offset + i, "A CFF2 DICT blend has too few operands.");
                    }

                    int start = operands.Count - (int)used;
                    operands.RemoveRange(start + n, (int)used - n);
                    continue;
                }

                if (op == 22 && operands.Count > 0)
                {
                    vsindex = (int)operands[^1];
                }

                dict._entries[op] = [.. operands];
                operands.Clear();
                if (operands.Count > 48)
                {
                    throw FontErrors.Corrupt(offset + i, "A CFF DICT has too many operands.");
                }
            }
            else if (b0 == 30)
            {
                operands.Add(ReadReal(data, ref i, offset));
            }
            else
            {
                operands.Add(ReadInteger(data, ref i, offset));
            }

            if (operands.Count > 513)
            {
                throw FontErrors.Corrupt(offset + i, "A CFF DICT has too many operands.");
            }
        }

        return dict;
    }

    private static int ReadInteger(ReadOnlySpan<byte> data, ref int i, long offset)
    {
        int b0 = data[i];
        int need = b0 switch
        {
            28 => 3,
            29 => 5,
            >= 32 and <= 246 => 1,
            >= 247 and <= 254 => 2,
            _ => throw FontErrors.Corrupt(offset + i, $"A CFF DICT has the reserved byte {b0}."),
        };
        if (i + need > data.Length)
        {
            throw FontErrors.Truncated(offset + i);
        }

        int value = b0 switch
        {
            28 => (short)((data[i + 1] << 8) | data[i + 2]),
            29 => (data[i + 1] << 24) | (data[i + 2] << 16) | (data[i + 3] << 8) | data[i + 4],
            <= 246 => b0 - 139,
            <= 250 => ((b0 - 247) * 256) + data[i + 1] + 108,
            _ => (-(b0 - 251) * 256) - data[i + 1] - 108,
        };
        i += need;
        return value;
    }

    private static double ReadReal(ReadOnlySpan<byte> data, ref int i, long offset)
    {
        i++;
        Span<char> text = stackalloc char[64];
        int length = 0;
        while (true)
        {
            if (i >= data.Length)
            {
                throw FontErrors.Truncated(offset + i);
            }

            byte b = data[i++];
            for (int shift = 4; shift >= 0; shift -= 4)
            {
                int nibble = (b >> shift) & 0xF;
                if (nibble == 0xF)
                {
                    return double.TryParse(text[..length], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0;
                }

                ReadOnlySpan<char> part = nibble switch
                {
                    <= 9 => [(char)('0' + nibble)],
                    0xA => ".",
                    0xB => "E",
                    0xC => "E-",
                    0xE => "-",
                    _ => "",
                };
                if (length + part.Length > text.Length)
                {
                    throw FontErrors.Corrupt(offset + i, "A CFF real number is too long.");
                }

                part.CopyTo(text[length..]);
                length += part.Length;
            }
        }
    }
}
