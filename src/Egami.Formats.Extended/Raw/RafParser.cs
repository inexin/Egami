using System.Buffers.Binary;

namespace Egami.Formats.Extended.Raw;

/// <summary>
/// Reads Fujifilm's RAF container (the Fujifilm compressed RAF clean-room specification, section 2): the big-endian header's
/// offsets to the embedded JPEG (whose Exif names the camera and holds the preview), the meta directory (sizes, crop and the
/// X-Trans pattern) and the CFA section, a TIFF whose IFD 0xF000 points at Fujifilm's raw IFD (size, depth, strip, black level
/// and white balance).
/// </summary>
internal static class RafParser
{
    /// <summary>Reads a RAF; returns its frame and the CFA section's TIFF, which the raw data's offsets count from.</summary>
    public static (RawFrame Frame, RawTiff Tiff) Parse(RawSource source)
    {
        if (!source.Ensure(108))
        {
            throw new InvalidDataException("The RAF header is cut short.");
        }

        ReadOnlySpan<byte> header = source.Data.AsSpan(0, 108);
        long jpegOffset = BinaryPrimitives.ReadUInt32BigEndian(header[84..]), jpegLength = BinaryPrimitives.ReadUInt32BigEndian(header[88..]);
        long metaOffset = BinaryPrimitives.ReadUInt32BigEndian(header[92..]), metaLength = BinaryPrimitives.ReadUInt32BigEndian(header[96..]);
        long cfaOffset = BinaryPrimitives.ReadUInt32BigEndian(header[100..]);
        Dictionary<int, byte[]> meta = Meta(source, metaOffset, metaLength);

        if (cfaOffset < 108 || cfaOffset > int.MaxValue - 16 || !source.Ensure(cfaOffset + 8)
            || !RawTiff.TryHeader(source.Data.AsSpan((int)cfaOffset, 8), out bool bigEndian, out ushort magic, out uint first) || magic != 42)
        {
            throw new NotSupportedException("Egami reads RAF files whose CFA section is a TIFF (bodies from 2012 on), not older ones yet.");
        }

        RawTiff tiff = new(source, bigEndian, (int)cfaOffset);
        RawIfd raw = tiff.ReadIfd(first)?.Sub(0xF000) ?? throw new InvalidDataException("The RAF's CFA section has no raw IFD.");
        int width = (int)raw.Int(0xF001), height = (int)raw.Int(0xF002), bits = (int)raw.Int(0xF003, 14);
        long offset = raw.Int(0xF007), count = raw.Int(0xF008);
        if (width <= 0 || height <= 0 || bits is < 8 or > 16 || offset <= 0)
        {
            throw new InvalidDataException("The RAF's raw IFD has no usable size or data.");
        }

        // Compressed when smaller than unpacked and the strip starts with the codec's header (spec, section 2); the X-H2S may
        // give a count past the end of the file, which the decoder clamps.
        long unpacked = (long)width * height * 2;
        RawCodec codec;
        object? data = null;
        source.Ensure(cfaOffset + offset + 16);
        ReadOnlySpan<byte> start = source.Data.AsSpan((int)Math.Min(cfaOffset + offset, source.Length), (int)Math.Clamp(source.Length - cfaOffset - offset, 0, 16));
        FujiDecoder.Header? compressed = count < unpacked ? FujiDecoder.ReadHeader(start) : null;
        if (compressed is not null)
        {
            codec = RawCodec.FujiCompressed;
            data = compressed;
            bits = compressed.Bits;
        }
        else if (count >= unpacked)
        {
            codec = RawCodec.Unpacked16;
        }
        else if (count >= (long)width * height * 3 / 2)
        {
            codec = RawCodec.Packed12Lsb;
        }
        else
        {
            throw new NotSupportedException("Egami does not read this RAF's raw packing yet.");
        }

        RawFrame frame = new() { Sensor = MakerParser.Sensor(raw, codec, width, height, bits, data, offset, count) };
        frame.White = [(1 << bits) - 1];

        // The pattern: the meta directory's X-Trans cells (stored last cell first) or, without them, RGGB. The compressed
        // codec places colours by its own reference pattern, so a file whose pattern differs is refused (spec, section 8.3).
        if (meta.TryGetValue(0x0131, out byte[]? cells) && cells.Length == 36 && cells.All(c => c <= 2))
        {
            byte[] pattern = [.. cells.Reverse()];
            if (compressed is { XTransType: true } && !pattern.AsSpan().SequenceEqual(FujiDecoder.XTrans))
            {
                throw new NotSupportedException("The RAF's X-Trans pattern is not the phase the compressed codec uses.");
            }

            frame.Cfa = new RawCfa(6, 6, pattern);
        }
        else
        {
            if (compressed is { XTransType: true })
            {
                throw new InvalidDataException("The RAF is X-Trans compressed but gives no X-Trans pattern.");
            }

            frame.Cfa = MakerParser.Bayer("RGGB");
        }

        // RawExposureBias (meta tag 0x9650, two signed 16-bit values, a ratio, as ExifTool's FujiFilm page names it).
        if (meta.TryGetValue(0x9650, out byte[]? biasBytes) && biasBytes.Length >= 4 && BinaryPrimitives.ReadInt16BigEndian(biasBytes.AsSpan(2)) is short den and not 0)
        {
            frame.ExposureBias = BinaryPrimitives.ReadInt16BigEndian(biasBytes) / (double)den;
        }

        if (raw.Doubles(0xF00A, 64) is { Length: > 0 } black)
        {
            frame.Black = [black.Average()];
            frame.HasOwnLevels = true;
        }

        if (raw.Doubles(0xF00E, 3) is { Length: 3 } grb && grb.All(v => v > 0))
        {
            frame.AsShotNeutral = [grb[0] / grb[1], 1, grb[0] / grb[2]];
        }
        else if (meta.TryGetValue(0x2FF0, out byte[]? wb) && wb.Length >= 8)
        {
            double g = BinaryPrimitives.ReadUInt16BigEndian(wb), r = BinaryPrimitives.ReadUInt16BigEndian(wb.AsSpan(2)), b = BinaryPrimitives.ReadUInt16BigEndian(wb.AsSpan(6));
            if (g > 0 && r > 0 && b > 0)
            {
                frame.AsShotNeutral = [g / r, 1, g / b];
            }
        }

        // The crop: its top-left (0x0110) and size (0x0111), each height first.
        if (meta.TryGetValue(0x0110, out byte[]? topLeft) && topLeft.Length >= 4 && meta.TryGetValue(0x0111, out byte[]? size) && size.Length >= 4)
        {
            int top = BinaryPrimitives.ReadUInt16BigEndian(topLeft), left = BinaryPrimitives.ReadUInt16BigEndian(topLeft.AsSpan(2));
            int h = BinaryPrimitives.ReadUInt16BigEndian(size), w = BinaryPrimitives.ReadUInt16BigEndian(size.AsSpan(2));
            if (w > 0 && h > 0 && left + w <= width && top + h <= height)
            {
                frame.Active = new Rect(left, top, w, h);
                frame.Crop = (0, 0, w, h);
                frame.HasOwnCrop = true;
            }
        }

        // The embedded JPEG: its Exif is the file's metadata, and it is the preview.
        RawIfd? ifd0 = null;
        List<RawIfd> all = [];
        if (jpegOffset > 0 && jpegLength > 0 && jpegOffset + jpegLength <= int.MaxValue && source.Ensure(jpegOffset + Math.Min(jpegLength, 1 << 20)))
        {
            (long exifAt, int pw, int ph) = Jpeg(source, (int)jpegOffset, (int)Math.Min(jpegLength, source.Length - jpegOffset));
            if (exifAt > 0 && RawTiff.TryHeader(source.Data.AsSpan((int)exifAt, Math.Min(16, source.Length - (int)exifAt)), out bool exifBe, out ushort exifMagic, out uint exifFirst) && exifMagic == 42)
            {
                RawTiff exifTiff = new(source, exifBe, (int)exifAt);
                foreach (RawIfd ifd in exifTiff.Chain(exifFirst, 4))
                {
                    MakerParser.Collect(ifd, all, 0);
                }

                ifd0 = all.FirstOrDefault();
            }

            if (pw > 0 && ph > 0)
            {
                frame.Previews.Add(new RawPreviewSource(pw, ph, true, jpegOffset, jpegLength, null));
            }
        }

        ifd0 ??= raw;
        MakerParser.Common(frame, ifd0, ifd0.Sub(RawTags.ExifIfd), all, null);
        if (frame.Model.Length == 0 && Ascii(header.Slice(28, 32)) is { Length: > 0 } model)
        {
            frame.Make = "FUJIFILM";
            frame.Model = model;
        }

        return (frame, tiff);
    }

    /// <summary>Reads the meta directory: a u32 count, then per record a u16 tag, a u16 length and the bytes.</summary>
    private static Dictionary<int, byte[]> Meta(RawSource source, long offset, long length)
    {
        Dictionary<int, byte[]> meta = [];
        if (offset < 108 || length < 4 || length > 1 << 20 || !source.Ensure(offset + length))
        {
            return meta;
        }

        ReadOnlySpan<byte> d = source.Data.AsSpan((int)offset, (int)length);
        uint records = BinaryPrimitives.ReadUInt32BigEndian(d);
        int at = 4;
        for (uint i = 0; i < records && at + 4 <= d.Length; i++)
        {
            int tag = BinaryPrimitives.ReadUInt16BigEndian(d[at..]), n = BinaryPrimitives.ReadUInt16BigEndian(d[(at + 2)..]);
            if (at + 4 + n > d.Length)
            {
                break;
            }

            meta[tag] = d.Slice(at + 4, n).ToArray();
            at += 4 + n;
        }

        return meta;
    }

    /// <summary>Walks a JPEG's markers to its Exif TIFF header's position and its frame size.</summary>
    internal static (long ExifAt, int Width, int Height) Jpeg(RawSource source, int offset, int length)
    {
        ReadOnlySpan<byte> j = source.Data.AsSpan(offset, Math.Min(length, source.Length - offset));
        long exif = 0;
        if (j.Length < 4 || j[0] != 0xFF || j[1] != 0xD8)
        {
            return (0, 0, 0);
        }

        int at = 2;
        while (at + 4 <= j.Length)
        {
            if (j[at] != 0xFF)
            {
                break;
            }

            int marker = j[at + 1];
            int size = BinaryPrimitives.ReadUInt16BigEndian(j[(at + 2)..]);
            if (marker == 0xE1 && exif == 0 && at + 10 <= j.Length && j.Slice(at + 4, 6).SequenceEqual("Exif\0\0"u8))
            {
                exif = offset + at + 10;
            }
            else if (marker is 0xC0 or 0xC1 or 0xC2 && at + 9 <= j.Length)
            {
                return (exif, BinaryPrimitives.ReadUInt16BigEndian(j[(at + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(j[(at + 5)..]));
            }
            else if (marker == 0xDA)
            {
                break;
            }

            at += 2 + size;
        }

        return (exif, 0, 0);
    }

    private static string Ascii(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        return System.Text.Encoding.Latin1.GetString(end < 0 ? bytes : bytes[..end]).Trim();
    }
}
