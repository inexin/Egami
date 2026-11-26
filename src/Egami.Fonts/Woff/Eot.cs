using System.Buffers.Binary;
using System.Text;

using Egami.Fonts.Parsing;
using Egami.Fonts.Tables;

namespace Egami.Fonts.Woff;

/// <summary>
/// Reads and writes Embedded OpenType (W3C member submission, 2008): a little-endian EMBEDDEDFONT header (versions 0x00010000,
/// 0x00020001 and 0x00020002) with the font's names and OS/2 summary, then the font, optionally MicroType Express compressed
/// (<see cref="MtxDecoder"/>) and XOR-ed with 0x50. Egami never enforces the root string or the embedding bits, as it never
/// enforces fsType; a root string whose checksum does not match is reported.
/// </summary>
internal static class Eot
{
    internal const uint Version1 = 0x00010000;
    internal const uint Version21 = 0x00020001;
    internal const uint Version22 = 0x00020002;
    internal const ushort MagicNumber = 0x504C;
    internal const uint FlagSubset = 0x1;
    internal const uint FlagCompressed = 0x4;
    internal const uint FlagXor = 0x10000000;
    internal const byte XorKey = 0x50;
    internal const uint RootStringKey = 0x50475342;

    /// <summary>The fixed part of the header, up to and including Padding1.</summary>
    private const int FixedSize = 82;

    /// <summary>Returns whether the data starts with an EOT header: a known version at 8 and the magic number at 34.</summary>
    public static bool IsEot(ReadOnlySpan<byte> data) =>
        data.Length >= 36
        && BinaryPrimitives.ReadUInt16LittleEndian(data[34..]) == MagicNumber
        && BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) is Version1 or Version21 or Version22;

    /// <summary>Probes an EOT header: one face, whose outlines show when the font data is stored uncompressed.</summary>
    public static FontProbe Probe(ReadOnlySpan<byte> data)
    {
        FontOutlines? outlines = null;
        try
        {
            Header header = ReadHeader(data);
            if ((header.Flags & FlagCompressed) == 0 && header.FontDataSize >= 4)
            {
                Span<byte> start = stackalloc byte[4];
                data.Slice(header.FontDataOffset, 4).CopyTo(start);
                if ((header.Flags & FlagXor) != 0)
                {
                    Xor(start);
                }

                outlines = BigEndian.UInt32(start, 0) == 0x4F54544F ? FontOutlines.Cff : FontOutlines.TrueType;
            }
            else if ((header.Flags & FlagCompressed) != 0)
            {
                // MicroType Express compacts TrueType outlines only.
                outlines = FontOutlines.TrueType;
            }
        }
        catch (CorruptDataException)
        {
        }

        return new FontProbe(FontFileKind.Eot, 1, outlines);
    }

    public static DecodedFont Decode(ReadOnlyMemory<byte> memory, long limit, FontLoadOptions options)
    {
        ReadOnlySpan<byte> data = memory.Span;
        Header header = ReadHeader(data);
        if (header.EotSize != data.Length)
        {
            Typeface.Report(options, "Font.BadContainer", $"The EOT header gives a size of {header.EotSize} bytes; the data has {data.Length}.", 0);
        }

        if (header.RootStringChecksum is uint stored && stored != RootStringChecksum(data.Slice(header.RootStringOffset, header.RootStringSize)))
        {
            Typeface.Report(options, "Font.BadContainer", "The EOT root string checksum does not match its root string.", header.RootStringOffset);
        }

        WebFontDecoder.CheckLimit(header.FontDataSize, limit);
        byte[] font = data.Slice(header.FontDataOffset, header.FontDataSize).ToArray();
        if ((header.Flags & FlagXor) != 0)
        {
            Xor(font);
        }

        if ((header.Flags & FlagCompressed) != 0)
        {
            font = MtxDecoder.Decode(font, limit, header.FontDataOffset);
        }

        uint flavor = font.Length >= 4 ? BigEndian.UInt32(font, 0) : 0;
        if (flavor is not (SfntDirectory.TrueTypeVersion or SfntDirectory.OpenTypeVersion or SfntDirectory.AppleTrueVersion))
        {
            throw FontErrors.Corrupt(header.FontDataOffset, "The EOT font data is not a TrueType or OpenType font.");
        }

        return new DecodedFont(font, FontFileKind.Eot, Metadata: null, PrivateData: null, FaceCount: 1);
    }

    /// <summary>Writes an EOT 0x00020001 file holding the sfnt uncompressed, XOR-ed when asked.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> sfnt, string? rootString, bool xor, long limit)
    {
        int faceOffset = SfntDirectory.FaceOffset(sfnt, 0, out _);
        TableRecord[] tables = SfntDirectory.Read(sfnt, faceOffset, out _, _ => { });
        ReadOnlySpan<byte> os2Data = Table(sfnt, tables, "OS/2");
        Os2Table os2 = os2Data.IsEmpty ? new Os2Table() : Os2Table.Read(os2Data);
        ReadOnlySpan<byte> nameData = Table(sfnt, tables, "name");
        NameTable names = nameData.IsEmpty ? NameTable.Empty : NameTable.Read(nameData, 0);
        ReadOnlySpan<byte> head = Table(sfnt, tables, "head");

        byte[] family = Utf16(names.GetEnglish(1)), style = Utf16(names.GetEnglish(2));
        byte[] version = Utf16(names.GetEnglish(5)), full = Utf16(names.GetEnglish(4));
        byte[] root = Utf16(rootString);
        long size = FixedSize + 2 + family.Length + 4 + style.Length + 4 + version.Length + 4 + full.Length + 4 + root.Length + (long)sfnt.Length;
        WebFontDecoder.CheckLimit(size, limit);

        byte[] eot = new byte[size];
        Span<byte> d = eot;
        BinaryPrimitives.WriteUInt32LittleEndian(d, (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(d[4..], (uint)sfnt.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(d[8..], Version21);
        BinaryPrimitives.WriteUInt32LittleEndian(d[12..], xor ? FlagXor : 0);
        os2.Panose.AsSpan(0, Math.Min(10, os2.Panose.Length)).CopyTo(d[16..]);
        d[26] = 1; // DEFAULT_CHARSET
        d[27] = (byte)(os2.FsSelection & 1);
        BinaryPrimitives.WriteUInt32LittleEndian(d[28..], os2.WeightClass);
        BinaryPrimitives.WriteUInt16LittleEndian(d[32..], os2.FsType);
        BinaryPrimitives.WriteUInt16LittleEndian(d[34..], MagicNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(d[36..], os2.UnicodeRange1);
        BinaryPrimitives.WriteUInt32LittleEndian(d[40..], os2.UnicodeRange2);
        BinaryPrimitives.WriteUInt32LittleEndian(d[44..], os2.UnicodeRange3);
        BinaryPrimitives.WriteUInt32LittleEndian(d[48..], os2.UnicodeRange4);
        BinaryPrimitives.WriteUInt32LittleEndian(d[52..], os2.CodePageRange1);
        BinaryPrimitives.WriteUInt32LittleEndian(d[56..], os2.CodePageRange2);
        BinaryPrimitives.WriteUInt32LittleEndian(d[60..], head.Length >= 12 ? BigEndian.UInt32(head, 8) : 0);
        int at = FixedSize;
        foreach (byte[] s in new[] { family, style, version, full })
        {
            BinaryPrimitives.WriteUInt16LittleEndian(d[at..], (ushort)s.Length);
            s.CopyTo(d[(at + 2)..]);
            at += 2 + s.Length + 2; // the next string's padding stays zero
        }

        BinaryPrimitives.WriteUInt16LittleEndian(d[at..], (ushort)root.Length);
        root.CopyTo(d[(at + 2)..]);
        at += 2 + root.Length;
        sfnt.CopyTo(d[at..]);
        if (xor)
        {
            Xor(d[at..]);
        }

        return eot;
    }

    private static ReadOnlySpan<byte> Table(ReadOnlySpan<byte> sfnt, TableRecord[] tables, string tag)
    {
        var wanted = new FontTag(tag);
        foreach (TableRecord t in tables)
        {
            if (t.Tag == wanted)
            {
                return BigEndian.Slice(sfnt, t.Offset, t.Length);
            }
        }

        return [];
    }

    internal static uint RootStringChecksum(ReadOnlySpan<byte> root)
    {
        uint sum = 0;
        foreach (byte b in root)
        {
            sum += b;
        }

        return sum ^ RootStringKey;
    }

    internal static void Xor(Span<byte> data)
    {
        foreach (ref byte b in data)
        {
            b ^= XorKey;
        }
    }

    private static byte[] Utf16(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }

        byte[] bytes = Encoding.Unicode.GetBytes(value);
        if (bytes.Length > 0xFFFF)
        {
            throw new ArgumentException("An EOT name or root string takes at most 65535 bytes.", nameof(value));
        }

        return bytes;
    }

    /// <summary>Reads the header, checking every size against the data.</summary>
    internal static Header ReadHeader(ReadOnlySpan<byte> data)
    {
        if (!IsEot(data))
        {
            throw FontErrors.Corrupt(0, "The data is not an EOT file.");
        }

        uint eotSize = BinaryPrimitives.ReadUInt32LittleEndian(data);
        uint fontDataSize = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        int at = FixedSize;
        for (int i = 0; i < 4; i++)
        {
            // FamilyName, StyleName, VersionName, FullName, each after a padding word but the first.
            at = SkipString(data, at + (i == 0 ? 0 : 2));
        }

        int rootOffset = 0, rootSize = 0;
        uint? rootChecksum = null;
        if (version >= Version21)
        {
            rootOffset = Need(data, at + 2, 2) + 2;
            rootSize = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 2)..]);
            at = SkipString(data, at + 2);
            if (version >= Version22)
            {
                // RootStringCheckSum, EUDCCodePage, Padding6, SignatureSize and Signature, EUDCFlags, EUDCFontSize and its data.
                rootChecksum = BinaryPrimitives.ReadUInt32LittleEndian(data[Need(data, at, 10)..]);
                at = SkipString(data, at + 10);
                Need(data, at, 8);
                uint eudcSize = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]);
                at += 8;
                if (eudcSize > data.Length - at)
                {
                    throw FontErrors.Corrupt(at, "The EOT EUDC font runs past the end of the file.");
                }

                at += (int)eudcSize;
            }
        }

        if (fontDataSize > data.Length - at)
        {
            throw FontErrors.Corrupt(4, $"The EOT font data ({fontDataSize} bytes at {at}) runs past the end of the file.");
        }

        return new Header(eotSize, (int)fontDataSize, version, flags, rootOffset, rootSize, rootChecksum, at);
    }

    private static int SkipString(ReadOnlySpan<byte> data, int at)
    {
        int size = BinaryPrimitives.ReadUInt16LittleEndian(data[Need(data, at, 2)..]);
        Need(data, at + 2, size);
        return at + 2 + size;
    }

    private static int Need(ReadOnlySpan<byte> data, int at, int count)
    {
        if (at < 0 || count > data.Length - at)
        {
            throw FontErrors.Truncated(at);
        }

        return at;
    }

    internal readonly record struct Header(uint EotSize, int FontDataSize, uint Version, uint Flags, int RootStringOffset, int RootStringSize, uint? RootStringChecksum, int FontDataOffset);
}
