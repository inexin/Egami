using System.Buffers.Binary;

namespace Egami.Formats.Jbig2;

/// <summary>One segment's header (T.88 7.2) and where its data lies.</summary>
internal sealed class Jbig2Segment
{
    internal const int SymbolDictionary = 0;
    internal const int PatternDictionary = 16;
    internal const int PageInformation = 48;
    internal const int EndOfPage = 49;
    internal const int EndOfStripe = 50;
    internal const int EndOfFile = 51;
    internal const int Profiles = 52;
    internal const int Tables = 53;
    internal const int ColourPalette = 54;
    internal const int Extension = 62;

    internal uint Number { get; init; }

    internal int Type { get; init; }

    internal uint[] References { get; init; } = [];

    internal uint Page { get; init; }

    /// <summary>Gets the offset of the header in the data, for error messages.</summary>
    internal int HeaderOffset { get; init; }

    internal int DataStart { get; set; }

    internal int DataLength { get; set; }

    internal int DataEnd => DataStart + DataLength;

    /// <summary>Gets whether this is a region segment (types 4 to 43).</summary>
    internal bool IsRegion => Type is 4 or 6 or 7 or 20 or 22 or 23 or 36 or 38 or 39 or 40 or 42 or 43;

    /// <summary>Gets whether this is an intermediate region segment, drawn into an auxiliary buffer.</summary>
    internal bool IsIntermediate => Type is 4 or 20 or 36 or 40;

    internal bool IsRefinement => Type is 40 or 42 or 43;

    internal bool IsDictionary => Type is SymbolDictionary or PatternDictionary or Tables;
}

/// <summary>The region segment information field (T.88 7.4.1).</summary>
internal readonly record struct Jbig2RegionInfo(int Width, int Height, int X, int Y, Jbig2Combine Combine, bool ColourExtension)
{
    internal const int Length = 17;

    internal static Jbig2RegionInfo Read(byte[] data, Jbig2Segment segment)
    {
        if (segment.DataLength < Length)
        {
            throw new InvalidDataException("A region segment is shorter than its region information.");
        }

        int at = segment.DataStart;
        uint width = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at + 4));
        int x = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at + 8));
        int y = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at + 12));
        byte flags = data[at + 16];
        if (width > int.MaxValue || height > int.MaxValue)
        {
            throw new InvalidDataException("A region is too large.");
        }

        int op = flags & 7;
        return new Jbig2RegionInfo((int)width, (int)height, x, y, op <= 4 ? (Jbig2Combine)op : Jbig2Combine.Or, (flags & 8) != 0);
    }
}

/// <summary>The page information segment (T.88 7.4.8).</summary>
internal sealed record Jbig2PageInfo(uint Width, uint Height, uint XResolution, uint YResolution, byte Flags, ushort Striping)
{
    internal const uint UnknownHeight = 0xFFFFFFFF;

    internal bool HeightUnknown => Height == UnknownHeight;

    internal int DefaultPixel => (Flags >> 2) & 1;

    internal Jbig2Combine DefaultCombine => (Jbig2Combine)((Flags >> 3) & 3);

    internal bool EventuallyLossless => (Flags & 1) != 0;

    internal bool Striped => (Striping & 0x8000) != 0;

    internal int MaxStripeSize => Striping & 0x7FFF;

    internal static Jbig2PageInfo Read(byte[] data, Jbig2Segment segment)
    {
        if (segment.DataLength < 19)
        {
            throw new InvalidDataException("A page information segment is shorter than 19 bytes.");
        }

        ReadOnlySpan<byte> d = data.AsSpan(segment.DataStart, 19);
        return new Jbig2PageInfo(
            BinaryPrimitives.ReadUInt32BigEndian(d),
            BinaryPrimitives.ReadUInt32BigEndian(d[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(d[8..]),
            BinaryPrimitives.ReadUInt32BigEndian(d[12..]),
            d[16],
            BinaryPrimitives.ReadUInt16BigEndian(d[17..]));
    }
}

/// <summary>
/// A JBIG2 data stream split into segments: a standalone file (T.88 Annex D, sequential or random-access) or the embedded form
/// a PDF holds, which has no file header.
/// </summary>
internal sealed class Jbig2Stream
{
    /// <summary>The file header's ID string (D.4.1).</summary>
    internal static ReadOnlySpan<byte> Id => [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A];

    private Jbig2Stream(byte[] data, List<Jbig2Segment> segments, Jbig2Organisation organisation, uint? declaredPages, bool truncated)
    {
        Truncated = truncated;
        Data = data;
        Segments = segments;
        Organisation = organisation;
        DeclaredPages = declaredPages;
    }

    internal byte[] Data { get; }

    internal List<Jbig2Segment> Segments { get; }

    internal Jbig2Organisation Organisation { get; }

    internal uint? DeclaredPages { get; }

    /// <summary>Gets whether the data ended inside a segment, which was cut short.</summary>
    internal bool Truncated { get; }

    /// <summary>
    /// Splits <paramref name="data"/> into segments. Data with the file header is a standalone file; data without it is read as
    /// embedded only when <paramref name="embedded"/> allows. A stream that ends inside a segment keeps the segments before it.
    /// </summary>
    internal static Jbig2Stream Parse(byte[] data, int length, bool embedded, int maxSegments)
    {
        List<Jbig2Segment> segments = [];
        if (length >= 9 && data.AsSpan(0, 8).SequenceEqual(Id))
        {
            byte flags = data[8];
            int at = 9;
            uint? pages = null;
            if ((flags & 2) == 0)
            {
                if (length < 13)
                {
                    throw new EndOfStreamException("The JBIG2 file header ends early.");
                }

                pages = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(9));
                at = 13;
            }

            bool sequential = (flags & 1) != 0;
            if (sequential)
            {
                bool cut = ReadSequential(data, length, at, segments, maxSegments);
                return new Jbig2Stream(data, segments, Jbig2Organisation.Sequential, pages, cut);
            }

            bool truncated = false;

            // Random access: every header first, up to the end of file segment, then the data parts in the same order.
            while (at < length)
            {
                Jbig2Segment segment = ReadHeader(data, length, ref at);
                segments.Add(segment);
                Limit(segments, maxSegments);
                if (segment.Type == Jbig2Segment.EndOfFile)
                {
                    break;
                }
            }

            foreach (Jbig2Segment segment in segments)
            {
                if (segment.DataLength < 0)
                {
                    throw new InvalidDataException("A random-access JBIG2 file has a segment of unknown length.");
                }

                segment.DataStart = at;
                if ((long)at + segment.DataLength > length)
                {
                    segment.DataLength = Math.Max(0, length - at);
                    truncated = true;
                }

                at += segment.DataLength;
            }

            return new Jbig2Stream(data, segments, Jbig2Organisation.RandomAccess, pages, truncated);
        }

        if (!embedded)
        {
            throw new InvalidDataException("The data does not start with the JBIG2 file header.");
        }

        bool embeddedCut = ReadSequential(data, length, 0, segments, maxSegments);
        return new Jbig2Stream(data, segments, Jbig2Organisation.Embedded, null, embeddedCut);
    }

    private static void Limit(List<Jbig2Segment> segments, int maxSegments)
    {
        if (segments.Count > maxSegments)
        {
            throw new InvalidDataException($"The stream holds more than {maxSegments} segments.");
        }
    }

    /// <summary>Reads interleaved headers and data; returns whether the data ended inside a segment.</summary>
    private static bool ReadSequential(byte[] data, int length, int at, List<Jbig2Segment> segments, int maxSegments)
    {
        bool truncated = false;
        while (at < length)
        {
            int start = at;
            Jbig2Segment segment;
            try
            {
                segment = ReadHeader(data, length, ref at);
            }
            catch (EndOfStreamException) when (segments.Count > 0)
            {
                return true;
            }

            segment.DataStart = at;
            if (segment.DataLength < 0)
            {
                segment.DataLength = UnknownLength(data, length, segment);
            }

            if ((long)at + segment.DataLength > length)
            {
                segment.DataLength = length - at;
                truncated = true;
            }

            at += segment.DataLength;
            segments.Add(segment);
            Limit(segments, maxSegments);
            if (segment.Type == Jbig2Segment.EndOfFile || at <= start)
            {
                return truncated;
            }
        }

        return truncated;
    }

    /// <summary>
    /// Finds the end of an immediate generic region of unknown length (7.2.7): 0xFF 0xAC (arithmetic) or 0x00 0x00 (MMR) after
    /// the eighteenth data byte, then a four-byte row count.
    /// </summary>
    private static int UnknownLength(byte[] data, int length, Jbig2Segment segment)
    {
        int start = segment.DataStart;
        if (segment.Type is not (38 or 39) || length - start < 18)
        {
            throw new InvalidDataException("A segment other than an immediate generic region has an unknown length.");
        }

        bool mmr = (data[start + 17] & 1) != 0;
        byte first = mmr ? (byte)0x00 : (byte)0xFF;
        byte second = mmr ? (byte)0x00 : (byte)0xAC;
        for (int i = start + 18; i + 5 < length; i++)
        {
            if (data[i] == first && data[i + 1] == second)
            {
                return i + 6 - start;
            }
        }

        return length - start;
    }

    /// <summary>Reads a segment header (7.2); throws <see cref="EndOfStreamException"/> when the data ends inside it.</summary>
    private static Jbig2Segment ReadHeader(byte[] data, int length, ref int at)
    {
        int start = at;
        uint number = U32(data, length, ref at);
        byte flags = U8(data, length, ref at);
        int type = flags & 0x3F;
        byte countByte = U8(data, length, ref at);
        long count = countByte >> 5;
        if (count == 7)
        {
            at--;
            count = U32(data, length, ref at) & 0x1FFFFFFF;
            long retainBytes = (count + 8) / 8;
            if (at + retainBytes > length)
            {
                throw new EndOfStreamException("A segment header ends early.");
            }

            at += (int)retainBytes;
        }
        else if (count > 4)
        {
            throw new InvalidDataException("A segment header's referred-to segment count is 5 or 6, which T.88 reserves.");
        }

        int size = number <= 256 ? 1 : number <= 65536 ? 2 : 4;
        if (count * size > length - at)
        {
            throw new EndOfStreamException("A segment header ends early.");
        }

        uint[] references = new uint[count];
        for (int i = 0; i < count; i++)
        {
            references[i] = size == 1 ? U8(data, length, ref at) : size == 2 ? U16(data, length, ref at) : U32(data, length, ref at);
        }

        uint page = (flags & 0x40) != 0 ? U32(data, length, ref at) : U8(data, length, ref at);
        uint dataLength = U32(data, length, ref at);
        return new Jbig2Segment
        {
            Number = number,
            Type = type,
            References = references,
            Page = page,
            HeaderOffset = start,
            DataLength = dataLength == 0xFFFFFFFF ? -1 : dataLength > int.MaxValue ? int.MaxValue : (int)dataLength,
        };
    }

    private static byte U8(byte[] data, int length, ref int at)
    {
        if (at >= length)
        {
            throw new EndOfStreamException("A segment header ends early.");
        }

        return data[at++];
    }

    private static ushort U16(byte[] data, int length, ref int at)
    {
        if (at + 2 > length)
        {
            throw new EndOfStreamException("A segment header ends early.");
        }

        ushort value = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
        at += 2;
        return value;
    }

    private static uint U32(byte[] data, int length, ref int at)
    {
        if (at + 4 > length)
        {
            throw new EndOfStreamException("A segment header ends early.");
        }

        uint value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
        at += 4;
        return value;
    }
}
