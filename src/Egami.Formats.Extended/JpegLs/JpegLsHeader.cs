using System.Buffers.Binary;

namespace Egami.Formats.Extended.JpegLs;

/// <summary>A component of the frame (T.87 C.2.2): its id, sampling factors, size and mapping table.</summary>
internal sealed class JpegLsComponent
{
    public int Id { get; init; }

    /// <summary>Gets the component's index in the frame.</summary>
    public int Index { get; init; }

    public int H { get; init; }

    public int V { get; init; }

    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>Gets or sets the mapping table the last scan of this component named, or 0 for none.</summary>
    public int Table { get; set; }
}

/// <summary>A mapping table (T.87 C.2.4.1.2 and C.2.4.1.3): entries of <see cref="EntryBytes"/> bytes each.</summary>
internal sealed class JpegLsMappingTable
{
    public int Id { get; init; }

    public int EntryBytes { get; init; }

    public byte[] Entries { get; set; } = [];

    public int Count => Entries.Length / EntryBytes;
}

/// <summary>
/// What the markers before a scan say: the frame (SOF55), the preset parameters (LSE), mapping tables, the restart interval
/// (DRI) and the colour transform of an APP8 "mrfx" segment. Segments between scans update it.
/// </summary>
internal sealed class JpegLsHeader
{
    public int Width { get; set; }

    public int Height { get; set; }

    public int Bits { get; set; }

    public JpegLsComponent[] Components { get; set; } = [];

    public int HMax { get; set; } = 1;

    public int VMax { get; set; } = 1;

    public int MaxVal { get; set; }

    public int T1 { get; set; }

    public int T2 { get; set; }

    public int T3 { get; set; }

    public int Reset { get; set; }

    public int RestartInterval { get; set; }

    public JpegLsColorTransform Transform { get; set; }

    public Dictionary<int, JpegLsMappingTable> Tables { get; } = [];

    /// <summary>Gets or sets the width and height of an LSE type 4 segment, which stand in for zeros in the frame header.</summary>
    public (long Width, long Height)? Oversize { get; set; }

    /// <summary>Gets or sets whether the frame header said 0 lines, so a DNL segment after the first scan gives the height.</summary>
    public bool HeightLater { get; set; }

    /// <summary>Gets whether any component is subsampled.</summary>
    public bool Subsampled => Components.Any(c => c.H != HMax || c.V != VMax);
}

/// <summary>Reads the marker segments of a JPEG-LS stream (T.87 annex C).</summary>
internal static class JpegLsSegments
{
    /// <summary>Returns whether the data starts with SOI and, after any fill bytes and segments, can be JPEG-LS.</summary>
    public static bool Matches(ReadOnlySpan<byte> data)
    {
        // SOI, then the first marker; JPEG-LS files usually start with SOF55, an APP8 (SPIFF or the colour transform) or LSE.
        if (data.Length < 4 || data[0] != 0xFF || data[1] != JpegLsMarker.Soi || data[2] != 0xFF)
        {
            return false;
        }

        int at = 2;
        while (at + 4 <= data.Length && data[at] == 0xFF)
        {
            byte marker = data[at + 1];
            if (marker == JpegLsMarker.Sof55)
            {
                return true;
            }

            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                return false;
            }

            if (marker == 0xFF)
            {
                at++;
                continue;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 2)..]);
            at += 2 + length;
        }

        return false;
    }

    /// <summary>
    /// Reads segments from <paramref name="at"/> until a scan starts. Returns the position of the SOS segment's length bytes,
    /// or −1 at EOI or the end of the data.
    /// </summary>
    public static int NextScan(byte[] data, int length, ref int at, JpegLsHeader header, bool frameSeen, Action<string, string> warn)
    {
        while (true)
        {
            // Skip anything that is not a marker, then fill bytes.
            while (at < length && data[at] != 0xFF)
            {
                at++;
            }

            while (at + 1 < length && data[at + 1] == 0xFF)
            {
                at++;
            }

            if (at + 1 >= length)
            {
                return -1;
            }

            byte marker = data[at + 1];
            at += 2;
            if (marker == JpegLsMarker.Eoi)
            {
                return -1;
            }

            if (marker is JpegLsMarker.Soi or (>= JpegLsMarker.Rst0 and <= JpegLsMarker.Rst0 + 7))
            {
                continue;
            }

            if (at + 2 > length)
            {
                return -1;
            }

            int size = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
            if (size < 2 || at + size > length)
            {
                throw new JpegLsDataException($"The segment of marker 0x{marker:X2} at {at - 2} runs past the end of the data.");
            }

            ReadOnlySpan<byte> body = data.AsSpan(at + 2, size - 2);
            switch (marker)
            {
                case JpegLsMarker.Sof55:
                    if (frameSeen)
                    {
                        throw new JpegLsDataException("The stream has a second frame header.");
                    }

                    ReadFrame(body, header);
                    frameSeen = true;
                    break;
                case JpegLsMarker.Sos:
                    if (!frameSeen)
                    {
                        throw new JpegLsDataException("A scan starts before the frame header.");
                    }

                    return at;
                case JpegLsMarker.Lse:
                    ReadPreset(body, header, warn);
                    break;
                case JpegLsMarker.Dri:
                    header.RestartInterval = body.Length switch
                    {
                        2 => BinaryPrimitives.ReadUInt16BigEndian(body),
                        3 => (body[0] << 16) | (body[1] << 8) | body[2],
                        4 => (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body), int.MaxValue),
                        _ => throw new JpegLsDataException($"The DRI segment has {body.Length} bytes."),
                    };
                    break;
                case JpegLsMarker.Dnl:
                    if (body.Length >= 2 && header.HeightLater)
                    {
                        header.Height = BinaryPrimitives.ReadUInt16BigEndian(body);
                    }

                    break;
                case JpegLsMarker.App8:
                    if (body.Length == 5 && body[..4].SequenceEqual("mrfx"u8))
                    {
                        header.Transform = body[4] switch
                        {
                            0 => JpegLsColorTransform.None,
                            1 => JpegLsColorTransform.Hp1,
                            2 => JpegLsColorTransform.Hp2,
                            3 => JpegLsColorTransform.Hp3,
                            _ => Unknown(body[4]),
                        };
                    }

                    break;
                case >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC:
                    throw new JpegLsDataException($"Marker 0x{marker:X2} starts a JPEG frame, not a JPEG-LS one.");
            }

            at += size;
        }

        JpegLsColorTransform Unknown(byte id)
        {
            warn("TransformUnknown", $"The colour transform {id} of the APP8 segment is not known; the samples are shown as stored.");
            return JpegLsColorTransform.None;
        }
    }

    /// <summary>Gives the frame header its final size: an LSE type 4 segment's, and each component's subsampled size.</summary>
    public static void Finish(JpegLsHeader header)
    {
        if (header.Oversize is { } size)
        {
            if (header.Width == 0)
            {
                header.Width = (int)Math.Min(size.Width, int.MaxValue);
            }

            if (header.Height == 0)
            {
                header.Height = (int)Math.Min(size.Height, int.MaxValue);
            }
        }

        foreach (JpegLsComponent c in header.Components)
        {
            c.Width = (int)((((long)header.Width * c.H) + header.HMax - 1) / header.HMax);
            c.Height = (int)((((long)header.Height * c.V) + header.VMax - 1) / header.VMax);
        }
    }

    private static void ReadFrame(ReadOnlySpan<byte> body, JpegLsHeader header)
    {
        if (body.Length < 6)
        {
            throw new JpegLsDataException("The frame header is too short.");
        }

        header.Bits = body[0];
        header.Height = BinaryPrimitives.ReadUInt16BigEndian(body[1..]);
        header.Width = BinaryPrimitives.ReadUInt16BigEndian(body[3..]);
        int count = body[5];
        if (header.Bits is < 2 or > 16)
        {
            throw new JpegLsDataException($"A sample has {header.Bits} bits; JPEG-LS allows 2 to 16.");
        }

        if (count == 0 || body.Length < 6 + (3 * count))
        {
            throw new JpegLsDataException($"The frame header names {count} components in {body.Length} bytes.");
        }

        JpegLsComponent[] components = new JpegLsComponent[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> c = body.Slice(6 + (3 * i), 3);
            int h = c[1] >> 4, v = c[1] & 15;
            if (h is < 1 or > 4 || v is < 1 or > 4)
            {
                throw new JpegLsDataException($"Component {c[0]} has sampling factors {h}×{v}; they must be 1 to 4.");
            }

            for (int j = 0; j < i; j++)
            {
                if (components[j].Id == c[0])
                {
                    throw new JpegLsDataException($"Two components have the id {c[0]}.");
                }
            }

            components[i] = new JpegLsComponent { Id = c[0], Index = i, H = h, V = v };
        }

        header.Components = components;
        header.HMax = components.Max(c => c.H);
        header.VMax = components.Max(c => c.V);
        header.HeightLater = header.Height == 0;
    }

    private static void ReadPreset(ReadOnlySpan<byte> body, JpegLsHeader header, Action<string, string> warn)
    {
        if (body.Length < 1)
        {
            throw new JpegLsDataException("An LSE segment is empty.");
        }

        switch (body[0])
        {
            case 1:
                if (body.Length < 11)
                {
                    throw new JpegLsDataException("The preset parameters segment is too short.");
                }

                header.MaxVal = BinaryPrimitives.ReadUInt16BigEndian(body[1..]);
                header.T1 = BinaryPrimitives.ReadUInt16BigEndian(body[3..]);
                header.T2 = BinaryPrimitives.ReadUInt16BigEndian(body[5..]);
                header.T3 = BinaryPrimitives.ReadUInt16BigEndian(body[7..]);
                header.Reset = BinaryPrimitives.ReadUInt16BigEndian(body[9..]);
                break;
            case 2:
            case 3:
                {
                    if (body.Length < 3 || body[2] == 0)
                    {
                        throw new JpegLsDataException("A mapping table segment is too short.");
                    }

                    int id = body[1];
                    int entryBytes = body[2];
                    ReadOnlySpan<byte> entries = body[3..];
                    if (body[0] == 2 || !header.Tables.TryGetValue(id, out JpegLsMappingTable? table))
                    {
                        if (body[0] == 3)
                        {
                            warn("TableContinued", $"Mapping table {id} is continued before it starts.");
                        }

                        header.Tables[id] = new JpegLsMappingTable { Id = id, EntryBytes = entryBytes, Entries = entries.ToArray() };
                    }
                    else if (table.EntryBytes == entryBytes)
                    {
                        table.Entries = [.. table.Entries, .. entries];
                    }
                    else
                    {
                        warn("TableContinued", $"Mapping table {id} is continued with {entryBytes}-byte entries instead of {table.EntryBytes}-byte ones; the rest was left out.");
                    }

                    break;
                }

            case 4:
                {
                    int bytes = body.Length > 1 ? body[1] : 0;
                    if (bytes is < 2 or > 4 || body.Length < 2 + (2 * bytes))
                    {
                        throw new JpegLsDataException("The oversize dimensions segment is malformed.");
                    }

                    long height = 0, width = 0;
                    for (int i = 0; i < bytes; i++)
                    {
                        height = (height << 8) | body[2 + i];
                        width = (width << 8) | body[2 + bytes + i];
                    }

                    header.Oversize = (width, height);
                    break;
                }

            default:
                warn("PresetUnknown", $"The LSE segment of type {body[0]} is not known; it was skipped.");
                break;
        }
    }
}
