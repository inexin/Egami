using System.Buffers.Binary;
using System.Globalization;

using Egami.Colors;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Jpeg;

/// <summary>
/// Writes a gain map JPEG the way Skia and libultrahdr do (HDR Design questions 10 and 11): the base image carries an XMP packet
/// with hdrgm:Version and a GContainer directory, an ISO 21496-1 APP2 segment with only the version, and an MPF index; the gain
/// map image after it carries hdrgm XMP with the values and the full ISO 21496-1 metadata, so readers of either format, and of
/// MPF, find it.
/// </summary>
internal static class JpegGainMapWriter
{
    private const string HdrgmNamespace = "http://ns.adobe.com/hdr-gain-map/1.0/";
    private const string ContainerNamespace = "http://ns.google.com/photos/1.0/container/";
    private const string ItemNamespace = "http://ns.google.com/photos/1.0/container/item/";

    /// <summary>The bytes of the base image's MPF APP2 segment, marker included.</summary>
    internal const int MpfSegmentLength = 4 + 4 + 82;

    private static ReadOnlySpan<byte> XmpId => "http://ns.adobe.com/xap/1.0/\0"u8;

    private static ReadOnlySpan<byte> MpfId => "MPF\0"u8;

    /// <summary>Returns whether the map still fits the picture: the same shape, give or take a map pixel.</summary>
    internal static bool Fits(GainMap map, int width, int height) =>
        Math.Abs((map.Width * (double)height / width) - map.Height) <= 1 && Math.Abs((map.Height * (double)width / height) - map.Width) <= 1;

    /// <summary>
    /// The gain map image to append: the stored JPEG with its old gain map segments replaced, or the pixels encoded at
    /// the map's quality, with hdrgm XMP and ISO 21496-1 metadata after SOI.
    /// </summary>
    internal static byte[] MapImage(GainMap map, EgamiContext context)
    {
        byte[] plain;
        if (!map.Encoded.IsEmpty)
        {
            plain = map.Encoded.ToArray();
        }
        else
        {
            using Raster image = map.ToRaster(context);
            using MemoryStream stream = new();
            image.Save(stream, JpegFormat.Info, new JpegWriterOptions { Quality = map.Quality, StripMetadata = true, Subsampling = ChromaSubsampling.Subsample444 });
            plain = stream.ToArray();
        }

        List<byte[]> segments = [Segment(0xE1, [.. XmpId, .. MapXmp(map.Info).ToBytes().Span]), Segment(0xE2, [.. JpegGainMap.IsoId, .. Iso(map.Info)])];
        if (map.Encoded.IsEmpty && !map.Info.UseBaseColorSpace && map.Info.AlternateProfile is ColorProfile alternate && alternate.ToIcc() is { Length: <= ushort.MaxValue - 16 } icc)
        {
            segments.Add(Segment(0xE2, [.. "ICC_PROFILE\0"u8, 1, 1, .. icc]));
        }

        return Rewrap(plain, segments);
    }

    /// <summary>
    /// The base image's XMP: the raster's packet (or a new one) with any old hdrgm and container entries replaced by
    /// hdrgm:Version and a GContainer directory naming the gain map image's length.
    /// </summary>
    internal static ReadOnlyMemory<byte> BaseXmp(XmpData? xmp, int mapLength)
    {
        XmpData packet = xmp?.Copy() ?? new XmpData();
        foreach (XmpName name in packet.Names.Where(n => n.Namespace is HdrgmNamespace or ContainerNamespace).ToList())
        {
            packet.Remove(name);
        }

        packet.Set(new XmpName(HdrgmNamespace, "Version"), "1.0");
        XmpArray directory = new(XmpArrayKind.Sequence);
        directory.Add(Item("Primary", null));
        directory.Add(Item("GainMap", mapLength));
        packet.Set(new XmpName(ContainerNamespace, "Directory"), directory);
        return packet.ToBytes();

        static XmpStruct Item(string semantic, int? length)
        {
            XmpStruct item = new();
            item.Set(new XmpName(ItemNamespace, "Semantic"), semantic);
            item.Set(new XmpName(ItemNamespace, "Mime"), "image/jpeg");
            if (length is int n)
            {
                item.Set(new XmpName(ItemNamespace, "Length"), n.ToString(CultureInfo.InvariantCulture));
            }

            XmpStruct entry = new();
            entry.Set(new XmpName(ContainerNamespace, "Item"), item);
            return entry;
        }
    }

    /// <summary>The base image's ISO 21496-1 APP2 payload after the identifier: the minimum and writer versions, both 0.</summary>
    internal static ReadOnlySpan<byte> IsoVersion => [0, 0, 0, 0];

    /// <summary>
    /// The base image's MPF APP2 segment, marker included, for a base image of <paramref name="baseLength"/> bytes whose MP
    /// header (the byte after "MPF\0") is at <paramref name="headerAt"/>, followed by a gain map image of <paramref name="mapLength"/>
    /// bytes. Big-endian, the primary typed as a baseline MP primary image and the gain map untyped, as libultrahdr writes.
    /// </summary>
    internal static byte[] Mpf(int baseLength, int headerAt, int mapLength)
    {
        byte[] segment = new byte[MpfSegmentLength];
        Span<byte> s = segment;
        s[0] = 0xFF;
        s[1] = 0xE2;
        BinaryPrimitives.WriteUInt16BigEndian(s[2..], MpfSegmentLength - 2);
        MpfId.CopyTo(s[4..]);
        Span<byte> tiff = s[8..];
        "MM\0*"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt32BigEndian(tiff[4..], 8);
        BinaryPrimitives.WriteUInt16BigEndian(tiff[8..], 3);
        Entry(tiff[10..], 0xB000, 7, 4, BinaryPrimitives.ReadUInt32BigEndian("0100"u8));
        Entry(tiff[22..], 0xB001, 4, 1, 2);
        Entry(tiff[34..], 0xB002, 7, 32, 50);
        BinaryPrimitives.WriteUInt32BigEndian(tiff[46..], 0);
        Span<byte> entries = tiff[50..];
        BinaryPrimitives.WriteUInt32BigEndian(entries, 0x030000);
        BinaryPrimitives.WriteUInt32BigEndian(entries[4..], (uint)baseLength);
        BinaryPrimitives.WriteUInt32BigEndian(entries[8..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(entries[16..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(entries[20..], (uint)mapLength);
        BinaryPrimitives.WriteUInt32BigEndian(entries[24..], (uint)(baseLength - headerAt));
        return segment;

        static void Entry(Span<byte> at, ushort tag, ushort type, uint count, uint value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(at, tag);
            BinaryPrimitives.WriteUInt16BigEndian(at[2..], type);
            BinaryPrimitives.WriteUInt32BigEndian(at[4..], count);
            BinaryPrimitives.WriteUInt32BigEndian(at[8..], value);
        }
    }

    /// <summary>
    /// ISO 21496-1 metadata after the identifier: versions 0, then each value as its own fraction (one channel when the three
    /// agree, else three), as libultrahdr writes it.
    /// </summary>
    internal static byte[] Iso(GainMapInfo info)
    {
        bool multichannel = !(info.GainMapMin.IsUniform && info.GainMapMax.IsUniform && info.Gamma.IsUniform && info.OffsetSdr.IsUniform && info.OffsetHdr.IsUniform);
        int count = multichannel ? 3 : 1;
        byte[] data = new byte[4 + 1 + 16 + (count * 40)];
        Span<byte> s = data;
        s[4] = (byte)((multichannel ? 0x80 : 0) | (info.UseBaseColorSpace ? 0x40 : 0) | (info.BaseRenditionIsHdr ? 0x04 : 0));
        int pos = 5;
        Unsigned(s, ref pos, info.HdrCapacityMin);
        Unsigned(s, ref pos, info.HdrCapacityMax);
        for (int c = 0; c < count; c++)
        {
            Signed(s, ref pos, info.GainMapMin[c]);
            Signed(s, ref pos, info.GainMapMax[c]);
            Unsigned(s, ref pos, info.Gamma[c]);
            Signed(s, ref pos, info.OffsetSdr[c]);
            Signed(s, ref pos, info.OffsetHdr[c]);
        }

        return data;

        static void Signed(Span<byte> s, ref int pos, double value)
        {
            uint denominator = Denominator(value, int.MaxValue);
            BinaryPrimitives.WriteInt32BigEndian(s[pos..], (int)Math.Round(value * denominator));
            BinaryPrimitives.WriteUInt32BigEndian(s[(pos + 4)..], denominator);
            pos += 8;
        }

        static void Unsigned(Span<byte> s, ref int pos, double value)
        {
            value = Math.Max(value, 0);
            uint denominator = Denominator(value, uint.MaxValue);
            BinaryPrimitives.WriteUInt32BigEndian(s[pos..], (uint)Math.Round(value * denominator));
            BinaryPrimitives.WriteUInt32BigEndian(s[(pos + 4)..], denominator);
            pos += 8;
        }

        // The largest denominator, up to 2^30, that keeps the numerator in range.
        static uint Denominator(double value, double limit) => (uint)Math.Clamp(Math.Floor(limit / Math.Max(Math.Ceiling(Math.Abs(value)), 1)), 1, 1 << 30);
    }

    /// <summary>The gain map image's hdrgm XMP: every value, as one number or a sequence of three.</summary>
    internal static XmpData MapXmp(GainMapInfo info)
    {
        XmpData xmp = new();
        xmp.Set(new XmpName(HdrgmNamespace, "Version"), "1.0");
        Values("GainMapMin", info.GainMapMin);
        Values("GainMapMax", info.GainMapMax);
        Values("Gamma", info.Gamma);
        Values("OffsetSDR", info.OffsetSdr);
        Values("OffsetHDR", info.OffsetHdr);
        xmp.Set(new XmpName(HdrgmNamespace, "HDRCapacityMin"), Text(info.HdrCapacityMin));
        xmp.Set(new XmpName(HdrgmNamespace, "HDRCapacityMax"), Text(info.HdrCapacityMax));
        xmp.Set(new XmpName(HdrgmNamespace, "BaseRenditionIsHDR"), info.BaseRenditionIsHdr ? "True" : "False");
        return xmp;

        void Values(string name, GainMapValues values)
        {
            XmpName key = new(HdrgmNamespace, name);
            if (values.IsUniform)
            {
                xmp.Set(key, Text(values.Red));
            }
            else
            {
                xmp.Set(key, XmpArray.Of(XmpArrayKind.Sequence, Text(values.Red), Text(values.Green), Text(values.Blue)));
            }
        }

        static string Text(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>A segment: marker, length, payload.</summary>
    private static byte[] Segment(byte marker, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ushort.MaxValue - 2)
        {
            throw new InvalidOperationException("A gain map metadata segment is over the 64 KB a JPEG segment holds.");
        }

        byte[] segment = new byte[payload.Length + 4];
        segment[0] = 0xFF;
        segment[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(segment.AsSpan(4));
        return segment;
    }

    /// <summary>
    /// Copies <paramref name="jpeg"/> with its XMP, ISO 21496-1 and MPF segments left out and <paramref name="segments"/> put after
    /// SOI and any APP0; everything from the first segment that is not an APPn or COM on is copied as it is.
    /// </summary>
    internal static byte[] Rewrap(byte[] jpeg, List<byte[]> segments)
    {
        using MemoryStream output = new(jpeg.Length + segments.Sum(s => s.Length));
        output.Write(jpeg, 0, 2);
        int pos = 2;
        bool inserted = false;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF)
        {
            byte marker = jpeg[pos + 1];
            if (!(marker is >= 0xE0 and <= 0xEF || marker == 0xFE))
            {
                break;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 2)) + 2;
            if (pos + length > jpeg.Length)
            {
                break;
            }

            if (marker != 0xE0 && !inserted)
            {
                segments.ForEach(s => output.Write(s));
                inserted = true;
            }

            ReadOnlySpan<byte> payload = jpeg.AsSpan(pos + 4, length - 4);
            bool old = (marker == 0xE1 && payload.StartsWith(XmpId)) || (marker == 0xE2 && (payload.StartsWith(JpegGainMap.IsoId) || payload.StartsWith(MpfId)));
            if (!old)
            {
                output.Write(jpeg, pos, length);
            }

            pos += length;
        }

        if (!inserted)
        {
            segments.ForEach(s => output.Write(s));
        }

        output.Write(jpeg, pos, jpeg.Length - pos);
        return output.ToArray();
    }
}
