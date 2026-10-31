using Egami.Metadata;

namespace Egami.Formats.Extended.Raw;

/// <summary>Finds RAW files among TIFF-structured data: a TIFF header whose IFD0 has DNG's DNGVersion tag.</summary>
internal static class RawDetect
{
    /// <summary>Gets how many leading bytes detection looks at: enough for an IFD0 of 170 entries at offset 8.</summary>
    public const int SignatureLength = 2048;

    public static bool Matches(ReadOnlySpan<byte> header) => IsDng(header) || MakerParser.Detect(header) is not null;

    /// <summary>Gets whether a header is a DNG's: a TIFF header whose IFD0 has DNGVersion.</summary>
    public static bool IsDng(ReadOnlySpan<byte> header)
    {
        if (!RawTiff.TryHeader(header, out bool bigEndian, out ushort magic, out uint first) || magic != 42 || first < 8 || first + 2L > header.Length)
        {
            return false;
        }

        int count = U16(header, (int)first, bigEndian);
        for (int i = 0; i < count; i++)
        {
            int at = (int)first + 2 + (i * 12);
            if (at + 12 > header.Length)
            {
                break;
            }

            int tag = U16(header, at, bigEndian);
            if (tag == RawTags.DngVersion)
            {
                return true;
            }

            if (tag > RawTags.DngVersion)
            {
                break;
            }
        }

        return false;
    }

    private static int U16(ReadOnlySpan<byte> b, int at, bool bigEndian) => bigEndian ? (b[at] << 8) | b[at + 1] : b[at] | (b[at + 1] << 8);
}

/// <summary>An open RAW file: its bytes as far as read, its TIFF structure and the frame parsed from it.</summary>
internal sealed class RawFile : IDisposable
{
    private RawFile(RawSource source, RawTiff tiff, RawFrame frame)
    {
        Source = source;
        Tiff = tiff;
        Frame = frame;
    }

    public RawSource Source { get; }

    public RawTiff Tiff { get; }

    public RawFrame Frame { get; }

    /// <summary>Gets the previews, largest first.</summary>
    public List<RawPreviewSource> Previews => [.. Frame.Previews.OrderByDescending(p => (long)p.Width * p.Height)];

    public void Dispose() => Source.Dispose();

    public static RawFile Open(EgamiInput input)
    {
        RawSource source = new(input);
        try
        {
            return Open(input, source);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private static RawFile Open(EgamiInput input, RawSource source)
    {
        source.Ensure(RawDetect.SignatureLength);
        RawKind? maker = MakerParser.Detect(source.Data.AsSpan(0, source.Length));
        if (maker == RawKind.Cr3)
        {
            try
            {
                (RawFrame cr3, RawTiff cmt1) = Cr3Parser.Parse(source);
                return new RawFile(source, cmt1, cr3);
            }
            catch (InvalidDataException e)
            {
                throw input.Corrupt(0, e.Message);
            }
            catch (Exception e) when (MakerDecoders.Malformed(e))
            {
                throw input.Corrupt(0, "The CR3's structure is malformed.");
            }
        }

        if (maker == RawKind.Raf)
        {
            try
            {
                (RawFrame raf, RawTiff cfa) = RafParser.Parse(source);
                return new RawFile(source, cfa, raf);
            }
            catch (InvalidDataException e)
            {
                throw input.Corrupt(0, e.Message);
            }
            catch (Exception e) when (MakerDecoders.Malformed(e))
            {
                throw input.Corrupt(0, "The RAF's structure is malformed.");
            }
        }

        if (!RawTiff.TryHeader(source.Data.AsSpan(0, source.Length), out bool bigEndian, out ushort magic, out uint first) || (magic != 42 && maker is null))
        {
            throw input.Corrupt(0, "The RAW file's TIFF header is wrong.");
        }

        RawTiff tiff = new(source, bigEndian);
        try
        {
            bool dng = RawDetect.IsDng(source.Data.AsSpan(0, source.Length));
            RawFrame frame = dng || maker is null ? DngParser.Parse(tiff, first) : MakerParser.Parse(tiff, first, maker.Value);
            if (frame.Sensor.Width <= 0 || frame.Sensor.Height <= 0 || frame.Sensor.UnitWidth <= 0 || frame.Sensor.UnitHeight <= 0)
            {
                throw input.Corrupt(0, "The raw image has no size.");
            }

            return new RawFile(source, tiff, frame);
        }
        catch (InvalidDataException e)
        {
            throw input.Corrupt(0, e.Message);
        }
        catch (Exception e) when (maker is not null && MakerDecoders.Malformed(e))
        {
            throw input.Corrupt(0, "The RAW file's structure is malformed.");
        }
    }

    /// <summary>Reads the file's EXIF, GPS and XMP into <paramref name="metadata"/>, within the load's metadata budget.</summary>
    public void ReadMetadata(RasterMetadata metadata, EgamiInput input)
    {
        if (Frame.MetadataIfd is not RawIfd ifd0)
        {
            return;
        }

        long kept = 0;
        ExifData exif = new();
        bool any = false;
        foreach (RawEntry entry in ifd0.Entries.Values.OrderBy(e => e.Tag))
        {
            if (entry.ValueAt >= 0 && entry.Type <= 12 && IsPrimary(entry.Tag))
            {
                kept += entry.ByteCount;
                input.CheckMetadataBytes(kept);
                exif.Set(new ExifKey((ushort)entry.Tag, ExifIfd.Primary), (ExifType)entry.Type, LittleEndian(ifd0, entry));
                any = true;
            }
        }

        any |= Sub(Frame.ExifIfd ?? ifd0.Sub(RawTags.ExifIfd), ExifIfd.Exif, exif, input, ref kept);
        any |= Sub(Frame.GpsIfd ?? ifd0.Sub(RawTags.GpsIfd), ExifIfd.Gps, exif, input, ref kept);
        if (any)
        {
            metadata.Exif = exif;
        }

        if (ifd0.TryGet(RawTags.Xmp, out RawEntry xmp))
        {
            kept += xmp.ByteCount;
            input.CheckMetadataBytes(kept);
            metadata.Xmp = ifd0.Bytes(xmp).ToArray();
        }
    }

    private static bool Sub(RawIfd? ifd, ExifIfd target, ExifData exif, EgamiInput input, ref long kept)
    {
        if (ifd is null)
        {
            return false;
        }

        bool any = false;
        foreach (RawEntry entry in ifd.Entries.Values.OrderBy(e => e.Tag))
        {
            if (entry.ValueAt < 0 || entry.Type > 12 || entry.Tag is 0xA005)
            {
                continue;
            }

            kept += entry.ByteCount;
            input.CheckMetadataBytes(kept);
            exif.Set(new ExifKey((ushort)entry.Tag, target), (ExifType)entry.Type, LittleEndian(ifd, entry));
            any = true;
        }

        return any;
    }

    private static byte[] LittleEndian(RawIfd ifd, RawEntry entry)
    {
        byte[] bytes = ifd.Bytes(entry).ToArray();
        if (ifd.File.BigEndian)
        {
            int size = entry.Type is 5 or 10 ? 4 : entry.ValueSize;
            for (int i = 0; size > 1 && i + size <= bytes.Length; i += size)
            {
                Array.Reverse(bytes, i, size);
            }
        }

        return bytes;
    }

    /// <summary>Gets whether an IFD0 tag is one EXIF defines for the primary image, besides the pixel layout.</summary>
    private static bool IsPrimary(int tag) => tag is 269 or 270 or 271 or 272 or 274 or 282 or 283 or 285 or 296 or 301 or 305 or 306
        or 315 or 316 or 318 or 319 or 33432 or 0x4746 or 0x4749 or (>= 0x9C9B and <= 0x9C9F);
}
