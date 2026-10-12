namespace Egami.Fonts.Parsing;

/// <summary>One entry of an sfnt table directory, with the offset and length clamped to the file.</summary>
internal readonly record struct TableRecord(FontTag Tag, uint Checksum, int Offset, int Length);

/// <summary>The sfnt and collection headers: where each face's tables are.</summary>
internal static class SfntDirectory
{
    public const uint TrueTypeVersion = 0x00010000;
    public const uint OpenTypeVersion = 0x4F54544F; // 'OTTO'
    public const uint AppleTrueVersion = 0x74727565; // 'true'
    public const uint Type1Version = 0x74797031; // 'typ1'
    public const uint CollectionTag = 0x74746366; // 'ttcf'
    public const uint WoffTag = 0x774F4646; // 'wOFF'
    public const uint Woff2Tag = 0x774F4632; // 'wOF2'

    public static FontProbe Probe(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return new FontProbe(FontFileKind.Unknown, 0, null);
        }

        uint magic = BigEndian.UInt32(data, 0);
        switch (magic)
        {
            case TrueTypeVersion:
            case AppleTrueVersion:
                return new FontProbe(FontFileKind.Sfnt, 1, OutlinesOf(data, 0));
            case OpenTypeVersion:
                return new FontProbe(FontFileKind.Sfnt, 1, FontOutlines.Cff);
            case CollectionTag:
                {
                    int count = data.Length >= 12 ? (int)Math.Min(BigEndian.UInt32(data, 8), (uint)((data.Length - 12) / 4)) : 0;
                    FontOutlines? outlines = count > 0 ? OutlinesOf(data, (int)Math.Min(BigEndian.UInt32(data, 12), int.MaxValue)) : null;
                    return new FontProbe(FontFileKind.Collection, count, outlines);
                }

            case WoffTag:
                {
                    uint flavor = data.Length >= 8 ? BigEndian.UInt32(data, 4) : 0;
                    FontOutlines? outlines = flavor == OpenTypeVersion ? FontOutlines.Cff : flavor is TrueTypeVersion or AppleTrueVersion ? FontOutlines.TrueType : null;
                    return new FontProbe(FontFileKind.Woff, 1, outlines);
                }

            case Woff2Tag:
                return Woff.Woff2Decoder.Probe(data);
        }

        // Bare CFF: major 1, a header size of at least 4 and an offset size of 1 to 4.
        if (data[0] == 1 && data[2] >= 4 && data[3] is >= 1 and <= 4)
        {
            return new FontProbe(FontFileKind.Cff, 1, FontOutlines.Cff);
        }

        return new FontProbe(FontFileKind.Unknown, 0, null);
    }

    /// <summary>Returns the offset of face <paramref name="index"/>'s table directory, following a collection header.</summary>
    public static int FaceOffset(ReadOnlySpan<byte> data, int index, out int faceCount)
    {
        uint magic = data.Length >= 4 ? BigEndian.UInt32(data, 0) : 0;
        if (magic == CollectionTag)
        {
            faceCount = (int)Math.Min(BigEndian.UInt32(data, 8), (uint)((data.Length - 12) / 4));
            if ((uint)index >= (uint)faceCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index), $"The collection has {faceCount} faces.");
            }

            uint offset = BigEndian.UInt32(data, 12 + (index * 4));
            if (offset > data.Length - 12)
            {
                throw FontErrors.Corrupt(12 + (index * 4), $"Face {index} of the collection starts past the end.");
            }

            return (int)offset;
        }

        faceCount = 1;
        if (index != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "The file holds one face.");
        }

        return 0;
    }

    /// <summary>Reads a table directory; records that point outside the file are dropped and reported through <paramref name="damaged"/>.</summary>
    public static TableRecord[] Read(ReadOnlySpan<byte> data, int offset, out uint version, Action<string>? damaged)
    {
        var r = new FontReader(data[offset..], offset);
        version = r.ReadUInt32();
        if (version is not (TrueTypeVersion or OpenTypeVersion or AppleTrueVersion))
        {
            throw version == Type1Version
                ? new UnsupportedFormatException("sfnt-wrapped Type 1 fonts are not supported.")
                : new UnsupportedFormatException("The data is not a TrueType or OpenType font.");
        }

        int count = r.ReadUInt16();
        r.Skip(6);
        var records = new List<TableRecord>(count);
        for (int i = 0; i < count; i++)
        {
            if (r.Remaining < 16)
            {
                damaged?.Invoke($"The table directory lists {count} tables but holds {i}.");
                break;
            }

            var tag = r.ReadTag();
            uint checksum = r.ReadUInt32();
            uint tableOffset = r.ReadUInt32();
            uint length = r.ReadUInt32();
            if (tableOffset > data.Length)
            {
                damaged?.Invoke($"The {tag} table starts past the end of the file.");
                continue;
            }

            if (length > data.Length - tableOffset)
            {
                damaged?.Invoke($"The {tag} table runs past the end of the file and is cut short.");
                length = (uint)data.Length - tableOffset;
            }

            records.Add(new TableRecord(tag, checksum, (int)tableOffset, (int)length));
        }

        return [.. records];
    }

    private static FontOutlines? OutlinesOf(ReadOnlySpan<byte> data, int offset)
    {
        if (offset + 12 > data.Length)
        {
            return null;
        }

        int count = BigEndian.UInt16(data, offset + 4);
        for (int i = 0; i < count && offset + 12 + ((i + 1) * 16) <= data.Length; i++)
        {
            var tag = new FontTag(BigEndian.UInt32(data, offset + 12 + (i * 16)));
            if (tag == Tags.Glyf)
            {
                return FontOutlines.TrueType;
            }

            if (tag == Tags.Cff)
            {
                return FontOutlines.Cff;
            }
        }

        return null;
    }
}
