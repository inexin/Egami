namespace Egami.Video.Codecs.Av1;

/// <summary>
/// The OBUs of an AV1 temporal unit (AV1 Bitstream and Decoding Process specification, section 5): splitting low-overhead data
/// into OBUs, leb128 sizes, and writing OBUs back with or without their size field.
/// </summary>
internal static class Av1Obus
{
    public const string FormatName = "AV1";

    public const int SequenceHeader = 1;
    public const int TemporalDelimiter = 2;
    public const int FrameHeader = 3;
    public const int TileGroup = 4;
    public const int Metadata = 5;
    public const int Frame = 6;
    public const int RedundantFrameHeader = 7;
    public const int TileList = 8;
    public const int Padding = 15;

    /// <summary>A temporal delimiter OBU with its (zero) size field: what starts each temporal unit of a low-overhead stream.</summary>
    public static ReadOnlySpan<byte> TemporalDelimiterObu => [0x12, 0x00];

    /// <summary>
    /// Lists the OBUs of low-overhead data (section 5.2): each with obu_size, except that the last may run to the end, as
    /// containers allow. Throws <see cref="CorruptDataException"/> on a forbidden bit, a broken size or an OBU that runs past the data.
    /// </summary>
    public static List<Av1Obu> Split(ReadOnlySpan<byte> data)
    {
        List<Av1Obu> obus = [];
        int at = 0;
        while (at < data.Length)
        {
            obus.Add(Read(data, at));
            at = obus[^1].End;
        }

        return obus;
    }

    /// <summary>Reads the OBU starting at <paramref name="at"/>.</summary>
    public static Av1Obu Read(ReadOnlySpan<byte> data, int at)
    {
        Av1Obu obu = ReadHeader(data, at);
        if (obu.PayloadLength > data.Length - obu.PayloadStart)
        {
            throw new CorruptDataException(FormatName, at, $"An AV1 OBU of {obu.PayloadLength} bytes runs past the end of the data.");
        }

        return obu;
    }

    /// <summary>
    /// Reads the header of the OBU starting at <paramref name="at"/> without checking that its payload is all there, for data cut
    /// short on purpose; the OBU's payload may run past the data.
    /// </summary>
    public static Av1Obu ReadHeader(ReadOnlySpan<byte> data, int at)
    {
        int start = at;
        byte header = data[at++];
        if ((header & 0x80) != 0)
        {
            throw new CorruptDataException(FormatName, start, "An AV1 OBU header has its forbidden bit set.");
        }

        bool extension = (header & 4) != 0;
        int temporal = 0, spatial = 0;
        if (extension)
        {
            if (at >= data.Length)
            {
                throw new CorruptDataException(FormatName, start, "An AV1 OBU header is cut off.");
            }

            temporal = data[at] >> 5;
            spatial = (data[at] >> 3) & 3;
            at++;
        }

        long size = data.Length - at;
        if ((header & 2) != 0)
        {
            (ulong value, int length) = ReadLeb128(data[at..]);
            if (length < 0)
            {
                throw new CorruptDataException(FormatName, at, "An AV1 OBU's size is cut off or over 32 bits.");
            }

            at += length;
            size = (long)value;
        }

        return new Av1Obu((header >> 3) & 15, temporal, spatial, extension, (header & 2) != 0, start, at, (int)Math.Min(size, int.MaxValue - at));
    }

    /// <summary>Reads a leb128 value (section 4.10.5); the length is -1 when it is cut off or over 32 bits.</summary>
    public static (ulong Value, int Length) ReadLeb128(ReadOnlySpan<byte> data)
    {
        ulong value = 0;
        for (int i = 0; i < 8; i++)
        {
            if (i >= data.Length)
            {
                return (0, -1);
            }

            value |= (ulong)(data[i] & 0x7F) << (i * 7);
            if ((data[i] & 0x80) == 0)
            {
                return value > uint.MaxValue ? (0, -1) : (value, i + 1);
            }
        }

        return (0, -1);
    }

    /// <summary>Gets how many bytes the shortest leb128 coding of <paramref name="value"/> takes.</summary>
    public static int Leb128Length(ulong value)
    {
        int length = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            length++;
        }

        return length;
    }

    /// <summary>Writes the shortest leb128 coding of <paramref name="value"/> and returns its length.</summary>
    public static int WriteLeb128(Span<byte> destination, ulong value)
    {
        int length = 0;
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            destination[length++] = value != 0 ? (byte)(b | 0x80) : b;
        }
        while (value != 0);
        return length;
    }

    /// <summary>Writes the shortest leb128 coding of <paramref name="value"/> to a stream.</summary>
    public static void WriteLeb128(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[10];
        stream.Write(bytes[..WriteLeb128(bytes, value)]);
    }

    /// <summary>Writes an OBU with its size field (low-overhead format), whether or not it had one.</summary>
    public static void WriteWithSize(Stream stream, ReadOnlySpan<byte> data, Av1Obu obu)
    {
        stream.WriteByte((byte)(data[obu.Start] | 2));
        if (obu.HasExtension)
        {
            stream.WriteByte(data[obu.Start + 1]);
        }

        WriteLeb128(stream, (ulong)obu.PayloadLength);
        stream.Write(data.Slice(obu.PayloadStart, obu.PayloadLength));
    }

    /// <summary>Writes an OBU without its size field (as Annex B stores it); returns the bytes written.</summary>
    public static int WriteWithoutSize(Stream stream, ReadOnlySpan<byte> data, Av1Obu obu)
    {
        stream.WriteByte((byte)(data[obu.Start] & ~2));
        if (obu.HasExtension)
        {
            stream.WriteByte(data[obu.Start + 1]);
        }

        stream.Write(data.Slice(obu.PayloadStart, obu.PayloadLength));
        return SizeWithoutSizeField(obu);
    }

    /// <summary>Gets an OBU's length without its size field.</summary>
    public static int SizeWithoutSizeField(Av1Obu obu) => 1 + (obu.HasExtension ? 1 : 0) + obu.PayloadLength;
}

/// <summary>One OBU of low-overhead data: its header fields and where it and its payload lie.</summary>
/// <param name="Type">obu_type.</param>
/// <param name="TemporalId">temporal_id, 0 without an extension.</param>
/// <param name="SpatialId">spatial_id, 0 without an extension.</param>
/// <param name="HasExtension">obu_extension_flag.</param>
/// <param name="HasSize">obu_has_size_field.</param>
/// <param name="Start">The offset of the OBU header.</param>
/// <param name="PayloadStart">The offset of the payload.</param>
/// <param name="PayloadLength">The payload's length.</param>
internal readonly record struct Av1Obu(int Type, int TemporalId, int SpatialId, bool HasExtension, bool HasSize, int Start, int PayloadStart, int PayloadLength)
{
    /// <summary>Gets the offset just past the OBU.</summary>
    public int End => PayloadStart + PayloadLength;

    /// <summary>Gets whether the OBU starts a frame: a frame header or a frame OBU.</summary>
    public bool StartsFrame => Type is Av1Obus.FrameHeader or Av1Obus.Frame;
}

/// <summary>Reads bits most significant first (section 4.10.2), listing each named field when asked.</summary>
internal ref struct Av1BitReader(ReadOnlySpan<byte> data, List<CodecHeaderField>? fields = null)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _bit;

    public readonly int BitPosition => _bit;

    /// <summary>Reads f(n), noting it under <paramref name="name"/> when that is given.</summary>
    public long Bits(int count, string? name = null)
    {
        long value = 0;
        for (int i = 0; i < count; i++)
        {
            int at = _bit >> 3;
            if (at >= _data.Length)
            {
                throw new CorruptDataException(Av1Obus.FormatName, at, "An AV1 header is cut off.");
            }

            value = (value << 1) | (uint)((_data[at] >> (7 - (_bit & 7))) & 1);
            _bit++;
        }

        Note(name, value);
        return value;
    }

    public int Int(int count, string? name = null) => (int)Bits(count, name);

    public bool Flag(string? name = null) => Bits(1, name) != 0;

    /// <summary>Reads uvlc() (section 4.10.3).</summary>
    public long Uvlc(string? name = null)
    {
        int zeros = 0;
        while (Bits(1) == 0)
        {
            zeros++;
        }

        if (zeros >= 32)
        {
            Note(name, uint.MaxValue);
            return uint.MaxValue;
        }

        long value = Bits(zeros) + (1L << zeros) - 1;
        Note(name, value);
        return value;
    }

    public readonly void Note(string? name, long value)
    {
        if (name is not null)
        {
            fields?.Add(new CodecHeaderField(name, value));
        }
    }
}
