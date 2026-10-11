using System.Buffers.Binary;

namespace Egami.Formats.Jpeg2000;

/// <summary>A palette (pclr box, T.800 I.5.3.4): entries by column, each column with its own depth and sign.</summary>
internal sealed class Jp2Palette
{
    internal required int Entries { get; init; }

    internal required int[] Bits { get; init; }

    internal required bool[] Signed { get; init; }

    /// <summary>The values, column by column: column c's entry e is at c × Entries + e.</summary>
    internal required int[] Values { get; init; }
}

/// <summary>What a JP2 or JPX file's boxes say (T.800 Annex I, T.801 Annex M): the codestream and how to interpret it.</summary>
internal sealed class Jp2File
{
    internal Jpeg2000Container Container { get; set; } = Jpeg2000Container.Codestream;

    /// <summary>Gets or sets the first codestream (the jp2c box's contents, or the whole data for a raw codestream).</summary>
    internal ReadOnlyMemory<byte> Codestream { get; set; }

    internal int CodestreamCount { get; set; }

    /// <summary>Gets or sets the colr method used: 1 enumerated, 2 restricted ICC, 3 any ICC; 0 when none was usable.</summary>
    internal int ColorMethod { get; set; }

    internal int EnumeratedColorSpace { get; set; } = -1;

    /// <summary>Gets or sets the parameters of an enumerated CIELab space (EP), or empty.</summary>
    internal uint[] LabParameters { get; set; } = [];

    internal ReadOnlyMemory<byte> Icc { get; set; }

    internal Jp2Palette? Palette { get; set; }

    /// <summary>Gets or sets the component mapping (cmap): per channel, the component, the mapping type and the palette column.</summary>
    internal (int Component, int Type, int Column)[]? Mapping { get; set; }

    /// <summary>Gets or sets the channel definitions (cdef): per entry, the channel, its type and its association.</summary>
    internal (int Channel, int Type, int Association)[]? Channels { get; set; }

    /// <summary>Gets or sets the capture (resc) and display (resd) resolutions in grid points per metre, vertical then horizontal.</summary>
    internal (double Vertical, double Horizontal)? Capture { get; set; }

    internal (double Vertical, double Horizontal)? Display { get; set; }

    internal ReadOnlyMemory<byte> Xmp { get; set; }

    internal ReadOnlyMemory<byte> Exif { get; set; }

    internal ReadOnlyMemory<byte> Iptc { get; set; }

    internal int HeaderComponents { get; set; }

    internal List<(string Id, string Message)> Warnings { get; } = [];

    /// <summary>The JP2 signature box: length 12, type 'jP  ', contents 0D 0A 87 0A.</summary>
    internal static ReadOnlySpan<byte> Signature => [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A];

    private static readonly Guid XmpUuid = new("be7acfcb-97a9-42e8-9c71-999491e3afac");

    // The JPX UUIDs Adobe's and the JPEG committee's tools use for EXIF and IPTC.
    private static readonly byte[] ExifUuid = [.. "JpgTiffExif->JP2"u8];
    private static readonly byte[] IptcUuid = [0x33, 0xC7, 0xA4, 0xD2, 0xB8, 0x1D, 0x47, 0x23, 0xA0, 0xBA, 0xF1, 0xA3, 0xE0, 0x97, 0xAD, 0x38];

    /// <summary>Reads the boxes of a JP2 or JPX file, or takes data that starts SOC SIZ as a raw codestream.</summary>
    internal static Jp2File Parse(ReadOnlyMemory<byte> data)
    {
        Jp2File file = new();
        if (J2kCodestream.Matches(data.Span))
        {
            file.Codestream = data;
            file.CodestreamCount = 1;
            return file;
        }

        if (!data.Span.StartsWith(Signature))
        {
            throw new InvalidDataException("The data is neither a JP2 file nor a JPEG 2000 codestream.");
        }

        file.Container = Jpeg2000Container.Jp2;
        bool header = false;
        foreach ((uint type, ReadOnlyMemory<byte> body) in Boxes(data[12..]))
        {
            switch (type)
            {
                case 0x66747970: // ftyp
                    file.ReadFileType(body.Span);
                    break;
                case 0x6A703268: // jp2h
                    if (!header)
                    {
                        file.ReadHeader(body);
                        header = true;
                    }

                    break;
                case 0x6A703263: // jp2c
                    if (file.CodestreamCount++ == 0)
                    {
                        file.Codestream = body;
                    }

                    break;
                case 0x75756964: // uuid
                    file.ReadUuid(body);
                    break;
                case 0x72726571: // rreq
                    file.Container = Jpeg2000Container.Jpx;
                    break;
            }
        }

        if (file.CodestreamCount == 0)
        {
            throw new EndOfStreamException("The JP2 file has no codestream box.");
        }

        if (file.CodestreamCount > 1)
        {
            file.Warnings.Add(("Jpeg2000.ExtraCodestreams", $"The file holds {file.CodestreamCount} codestreams; only the first was read."));
        }

        return file;
    }

    /// <summary>Lists the boxes in <paramref name="data"/> (I.4): length, type and contents; a box running past the end is cut short.</summary>
    internal static List<(uint Type, ReadOnlyMemory<byte> Body)> Boxes(ReadOnlyMemory<byte> data)
    {
        List<(uint, ReadOnlyMemory<byte>)> boxes = [];
        ReadOnlySpan<byte> span = data.Span;
        int at = 0;
        while (at + 8 <= span.Length && boxes.Count < 100_000)
        {
            ulong length = BinaryPrimitives.ReadUInt32BigEndian(span[at..]);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(span[(at + 4)..]);
            int head = 8;
            if (length == 1)
            {
                if (at + 16 > span.Length)
                {
                    break;
                }

                length = BinaryPrimitives.ReadUInt64BigEndian(span[(at + 8)..]);
                head = 16;
            }
            else if (length == 0)
            {
                length = (ulong)(span.Length - at);
            }

            if (length < (ulong)head)
            {
                throw new InvalidDataException($"A box ('{TypeName(type)}') is shorter than its header.");
            }

            ulong end = Math.Min((ulong)at + length, (ulong)span.Length);
            boxes.Add((type, data[(at + head)..(int)end]));
            at = (int)end;
        }

        return boxes;
    }

    private static string TypeName(uint type) => string.Create(4, type, (chars, t) =>
    {
        for (int i = 0; i < 4; i++)
        {
            byte b = (byte)(t >> (24 - (8 * i)));
            chars[i] = b is >= 32 and < 127 ? (char)b : '?';
        }
    });

    private void ReadFileType(ReadOnlySpan<byte> body)
    {
        for (int at = 0; at + 4 <= body.Length; at += 4)
        {
            if (at == 4)
            {
                continue;
            }

            if (BinaryPrimitives.ReadUInt32BigEndian(body[at..]) == 0x6A707820)
            {
                // 'jpx ' as the brand or among the compatible ones.
                Container = Jpeg2000Container.Jpx;
            }
        }
    }

    /// <summary>Reads the JP2 header box: ihdr, bpcc, colr, pclr, cmap, cdef and res (I.5.3).</summary>
    private void ReadHeader(ReadOnlyMemory<byte> body)
    {
        foreach ((uint type, ReadOnlyMemory<byte> box) in Boxes(body))
        {
            ReadOnlySpan<byte> b = box.Span;
            switch (type)
            {
                case 0x69686472: // ihdr
                    if (b.Length >= 14)
                    {
                        HeaderComponents = BinaryPrimitives.ReadUInt16BigEndian(b[8..]);
                    }

                    break;
                case 0x636F6C72: // colr
                    ReadColor(box);
                    break;
                case 0x70636C72: // pclr
                    Palette = ReadPalette(b);
                    break;
                case 0x636D6170: // cmap
                    {
                        var mapping = new (int, int, int)[b.Length / 4];
                        for (int i = 0; i < mapping.Length; i++)
                        {
                            mapping[i] = (BinaryPrimitives.ReadUInt16BigEndian(b[(4 * i)..]), b[(4 * i) + 2], b[(4 * i) + 3]);
                        }

                        Mapping = mapping;
                    }

                    break;
                case 0x63646566: // cdef
                    if (b.Length >= 2)
                    {
                        int n = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(b), (b.Length - 2) / 6);
                        var channels = new (int, int, int)[n];
                        for (int i = 0; i < n; i++)
                        {
                            ReadOnlySpan<byte> e = b[(2 + (6 * i))..];
                            channels[i] = (BinaryPrimitives.ReadUInt16BigEndian(e), BinaryPrimitives.ReadUInt16BigEndian(e[2..]), BinaryPrimitives.ReadUInt16BigEndian(e[4..]));
                        }

                        Channels = channels;
                    }

                    break;
                case 0x72657320: // res
                    foreach ((uint resType, ReadOnlyMemory<byte> resBox) in Boxes(box))
                    {
                        if (Resolution(resBox.Span) is { } r)
                        {
                            if (resType == 0x72657363)
                            {
                                Capture = r;
                            }
                            else if (resType == 0x72657364)
                            {
                                Display = r;
                            }
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Reads a colour specification box (I.5.3.3, M.11.7.2); the first one Egami supports wins: enumerated, restricted ICC or,
    /// in JPX, any ICC.
    /// </summary>
    private void ReadColor(ReadOnlyMemory<byte> box)
    {
        ReadOnlySpan<byte> b = box.Span;
        if (ColorMethod != 0 || b.Length < 3)
        {
            return;
        }

        int method = b[0];
        if (method == 1 && b.Length >= 7)
        {
            ColorMethod = 1;
            EnumeratedColorSpace = (int)BinaryPrimitives.ReadUInt32BigEndian(b[3..]);
            if (EnumeratedColorSpace == 14 && b.Length >= 7 + 28)
            {
                uint[] ep = new uint[7];
                for (int i = 0; i < 7; i++)
                {
                    ep[i] = BinaryPrimitives.ReadUInt32BigEndian(b[(7 + (4 * i))..]);
                }

                LabParameters = ep;
            }
        }
        else if (method is 2 or 3 && b.Length > 3)
        {
            ColorMethod = method;
            Icc = box[3..];
        }
    }

    private static Jp2Palette? ReadPalette(ReadOnlySpan<byte> b)
    {
        if (b.Length < 3)
        {
            return null;
        }

        int entries = BinaryPrimitives.ReadUInt16BigEndian(b);
        int columns = b[2];
        if (entries is 0 or > 1024 || columns == 0 || b.Length < 3 + columns)
        {
            throw new InvalidDataException("The palette box gives no entries, more than 1,024 or no columns.");
        }

        int[] bits = new int[columns];
        bool[] signed = new bool[columns];
        int rowBytes = 0;
        for (int c = 0; c < columns; c++)
        {
            bits[c] = (b[3 + c] & 0x7F) + 1;
            signed[c] = (b[3 + c] & 0x80) != 0;
            if (bits[c] > 32)
            {
                throw new InvalidDataException("A palette column is deeper than 32 bits.");
            }

            rowBytes += (bits[c] + 7) / 8;
        }

        int at = 3 + columns;
        if (b.Length < at + ((long)rowBytes * entries))
        {
            throw new InvalidDataException("The palette box is shorter than its entries.");
        }

        int[] values = new int[columns * entries];
        for (int e = 0; e < entries; e++)
        {
            for (int c = 0; c < columns; c++)
            {
                int size = (bits[c] + 7) / 8;
                long v = 0;
                for (int i = 0; i < size; i++)
                {
                    v = (v << 8) | b[at++];
                }

                v &= (1L << bits[c]) - 1;
                if (signed[c] && (v & (1L << (bits[c] - 1))) != 0)
                {
                    v -= 1L << bits[c];
                }

                values[(c * entries) + e] = (int)v;
            }
        }

        return new Jp2Palette { Entries = entries, Bits = bits, Signed = signed, Values = values };
    }

    /// <summary>Reads a resc or resd box (I.5.3.7): VR = VR_N / VR_D × 10^VR_E, likewise HR, in grid points per metre.</summary>
    private static (double, double)? Resolution(ReadOnlySpan<byte> b)
    {
        if (b.Length < 10)
        {
            return null;
        }

        int vn = BinaryPrimitives.ReadUInt16BigEndian(b);
        int vd = BinaryPrimitives.ReadUInt16BigEndian(b[2..]);
        int hn = BinaryPrimitives.ReadUInt16BigEndian(b[4..]);
        int hd = BinaryPrimitives.ReadUInt16BigEndian(b[6..]);
        int ve = (sbyte)b[8];
        int he = (sbyte)b[9];
        if (vd == 0 || hd == 0 || vn == 0 || hn == 0)
        {
            return null;
        }

        return ((double)vn / vd * Math.Pow(10, ve), (double)hn / hd * Math.Pow(10, he));
    }

    private void ReadUuid(ReadOnlyMemory<byte> body)
    {
        ReadOnlySpan<byte> b = body.Span;
        if (b.Length < 16)
        {
            return;
        }

        if (new Guid(b[..16], bigEndian: true) == XmpUuid)
        {
            Xmp = body[16..];
        }
        else if (b[..16].SequenceEqual(ExifUuid))
        {
            Exif = body[16..];
        }
        else if (b[..16].SequenceEqual(IptcUuid))
        {
            Iptc = body[16..];
        }
    }
}
