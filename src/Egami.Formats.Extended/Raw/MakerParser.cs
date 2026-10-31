using System.Globalization;
using System.Text.RegularExpressions;
using System.Buffers.Binary;
using System.Text;

namespace Egami.Formats.Extended.Raw;

/// <summary>The RAW formats Egami reads, told apart by their headers.</summary>
internal enum RawKind
{
    Dng,
    Cr2,
    Nef,
    Pef,
    Arw,
    Orf,
    Rw2,
    Raf,
}

/// <summary>The codecs of maker sensor data, decoded by <see cref="MakerDecoders"/>; <see cref="Tiff"/> is TIFF's own compressions.</summary>
internal enum RawCodec
{
    Tiff,
    PentaxHuffman,
    Nikon,
    SonyArw2,
    SonyArw1,
    SonyLossless,
    Olympus,
    OlympusPacked,
    PanasonicV4,
    PanasonicV5,
    PanasonicV6,
    PanasonicV7,
    PanasonicV8,
    Canon,
    Packed12Lsb,
    Unpacked16,
    FujiCompressed,
    CanonSraw,
}

/// <summary>
/// Reads the maker RAW formats that are TIFF underneath (CR2, NEF, NRW, PEF, ARW, SR2, ORF, RW2) and RAF's container into a
/// <see cref="RawFrame"/>: the raw IFD and its codec, from the clean-room format specifications Egami's maker decoders are
/// written from; the maker notes' tags (as ExifTool's public tag-name pages name them, read as facts) for white balance, black
/// levels and crops; and, for what a file does not say, the camera table Adobe DNG Converter's output was measured into.
/// </summary>
internal static class MakerParser
{
    /// <summary>Returns the kind of RAW file a header holds, or <see langword="null"/>: by magic number, then by IFD0's Make.</summary>
    public static RawKind? Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 16 && header.StartsWith("FUJIFILMCCD-RAW"u8))
        {
            return RawKind.Raf;
        }

        if (!RawTiff.TryHeader(header, out bool bigEndian, out ushort magic, out uint first))
        {
            return null;
        }

        if (magic == 0x4F52 || magic == 0x5352)
        {
            return RawKind.Orf;
        }

        if (magic == 0x55)
        {
            return RawKind.Rw2;
        }

        if (magic != 42)
        {
            return null;
        }

        if (header.Length >= 10 && header[8] == 'C' && header[9] == 'R')
        {
            return RawKind.Cr2;
        }

        string? make = Ascii(header, bigEndian, first, RawTags.Make);
        if (make is null)
        {
            return null;
        }

        string upper = make.ToUpperInvariant();
        return upper.StartsWith("NIKON", StringComparison.Ordinal) ? RawKind.Nef
            : upper.StartsWith("PENTAX", StringComparison.Ordinal) || upper.StartsWith("RICOH IMAGING", StringComparison.Ordinal) || upper.StartsWith("SAMSUNG TECHWIN", StringComparison.Ordinal) ? RawKind.Pef
            : upper.StartsWith("SONY", StringComparison.Ordinal) ? RawKind.Arw
            : null;
    }

    /// <summary>Reads an ASCII tag of the IFD at <paramref name="first"/> when it lies within <paramref name="header"/>.</summary>
    private static string? Ascii(ReadOnlySpan<byte> header, bool bigEndian, uint first, int tag)
    {
        if (first + 2 > header.Length)
        {
            return null;
        }

        int count = U16(header, (int)first, bigEndian);
        for (int i = 0; i < count; i++)
        {
            int at = (int)first + 2 + (i * 12);
            if (at + 12 > header.Length)
            {
                return null;
            }

            if (U16(header, at, bigEndian) != tag)
            {
                continue;
            }

            int n = (int)U32(header, at + 4, bigEndian);
            int offset = n <= 4 ? at + 8 : (int)U32(header, at + 8, bigEndian);
            if (n <= 0 || offset < 0 || offset + n > header.Length)
            {
                return null;
            }

            ReadOnlySpan<byte> bytes = header.Slice(offset, n);
            int end = bytes.IndexOf((byte)0);
            return Encoding.Latin1.GetString(end < 0 ? bytes : bytes[..end]).Trim();
        }

        return null;
    }

    private static int U16(ReadOnlySpan<byte> b, int at, bool bigEndian) => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(b[at..]) : BinaryPrimitives.ReadUInt16LittleEndian(b[at..]);

    private static uint U32(ReadOnlySpan<byte> b, int at, bool bigEndian) => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(b[at..]) : BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);

    /// <summary>Reads a TIFF-structured maker file. Throws <see cref="InvalidDataException"/> when it has no raw image Egami reads.</summary>
    public static RawFrame Parse(RawTiff tiff, uint firstIfd, RawKind kind)
    {
        List<RawIfd> chain = tiff.Chain(firstIfd, 16);
        if (chain.Count == 0)
        {
            throw new InvalidDataException("The RAW file has no IFD.");
        }

        RawIfd ifd0 = chain[0];
        List<RawIfd> all = [];
        foreach (RawIfd ifd in chain)
        {
            Collect(ifd, all, 0);
        }

        RawIfd? exif = ifd0.Sub(RawTags.ExifIfd);
        MakerNote? note = MakerNote.Read(tiff, exif, kind);
        RawFrame frame = kind switch
        {
            RawKind.Cr2 => Cr2(tiff, chain, all, note),
            RawKind.Nef => Nef(tiff, all, note),
            RawKind.Pef => Pef(all, note),
            RawKind.Arw => Arw(tiff, ifd0, all),
            RawKind.Orf => Orf(tiff, ifd0, note),
            RawKind.Rw2 => Rw2(tiff, ifd0),
            _ => throw new InvalidDataException("The RAW file is not a format Egami reads."),
        };
        Common(frame, ifd0, exif, all, note);
        return frame;
    }

    internal static void Collect(RawIfd ifd, List<RawIfd> all, int depth)
    {
        if (all.Count >= 64 || all.Any(i => i.Offset == ifd.Offset))
        {
            return;
        }

        all.Add(ifd);
        if (depth < 3 && ifd.Ints(RawTags.SubIfds, 64) is long[] subs)
        {
            foreach (long at in subs)
            {
                if (ifd.File.ReadIfd(at) is RawIfd sub)
                {
                    Collect(sub, all, depth + 1);
                }
            }
        }
    }

    /// <summary>Fills in what every maker format shares: camera, exposure, previews, and the camera table's colour and levels.</summary>
    internal static void Common(RawFrame frame, RawIfd ifd0, RawIfd? exif, List<RawIfd> all, MakerNote? note)
    {
        frame.MetadataIfd = ifd0;
        frame.Make = ifd0.Ascii(RawTags.Make) ?? "";
        frame.Model = ifd0.Ascii(RawTags.Model) ?? "";
        frame.Orientation = (int)ifd0.Int(RawTags.Orientation, 1);
        double? Exif(int tag) => exif?.Has(tag) == true ? exif.Double(tag) : ifd0.Has(tag) ? ifd0.Double(tag) : null;
        frame.Iso = Exif(RawTags.IsoSpeed);
        frame.ExposureTime = Exif(RawTags.ExposureTime);
        frame.FNumber = Exif(RawTags.FNumber);
        frame.FocalLength = Exif(RawTags.FocalLength);
        frame.FocalLength35 = Exif(0xA405) is double f35 and > 0 ? f35 : null;
        frame.FocusDistance = Exif(0x9206) is double d and > 0 ? d : null;
        frame.LensModel = exif?.Ascii(RawTags.LensModel);
        frame.MakerNote = note?.Bytes;
        foreach (RawIfd ifd in all)
        {
            if (ifd != frame.Sensor.Ifd && DngParserPreview(ifd) is RawPreviewSource preview)
            {
                frame.Previews.Add(preview);
            }
        }

        RawCameraEntry? camera = RawCameraTable.Find(frame.Make, frame.Model);
        if (camera is not null)
        {
            frame.Profile = camera.Profile;
            frame.BaselineExposure = camera.Profile.BaselineExposure;
            if (camera.ExposureBias is double measured && frame.ExposureBias is double bias)
            {
                // Adobe's baseline exposure follows the bias one for one: move it by this file's difference from the sample's.
                frame.BaselineExposure += measured - bias;
            }
            bool sameSize = camera.Width == frame.Sensor.Width && camera.Height == frame.Sensor.Height;
            if (sameSize && camera.ActiveArea is Rect active && !frame.HasOwnCrop)
            {
                frame.Active = active;
                frame.Crop = camera.DefaultCrop is Rect c ? (c.X, c.Y, c.Width, c.Height) : (0, 0, active.Width, active.Height);
            }

            if (!frame.HasOwnLevels && camera.White > 0)
            {
                frame.BlackRows = camera.BlackRepeat.Rows;
                frame.BlackColumns = camera.BlackRepeat.Columns;
                frame.Black = camera.Black.Length == camera.BlackRepeat.Rows * camera.BlackRepeat.Columns ? camera.Black : [camera.Black.Average()];
                if (frame.Black.Length != frame.BlackRows * frame.BlackColumns)
                {
                    frame.BlackRows = frame.BlackColumns = 1;
                }

                frame.White = [camera.White];
            }
        }
        else
        {
            frame.Repairs.Add(("CameraUnknown", $"The camera {frame.Make} {frame.Model} is not in Egami's camera table; colours use a generic matrix."));
        }

        if (frame.Active.Width == 0)
        {
            frame.Active = new Rect(0, 0, frame.Sensor.Width, frame.Sensor.Height);
            frame.Crop = (0, 0, frame.Sensor.Width, frame.Sensor.Height);
        }
    }

    private static RawPreviewSource? DngParserPreview(RawIfd ifd)
    {
        long at = ifd.Int(RawTags.JpegInterchangeFormat);
        long length = ifd.Int(RawTags.JpegInterchangeFormatLength);
        int width = (int)ifd.Int(RawTags.ImageWidth), height = (int)ifd.Int(RawTags.ImageLength);
        if (at > 0 && length > 0)
        {
            return new RawPreviewSource(width, height, true, ifd.File.Origin + at, length, ifd);
        }

        if (ifd.Int(RawTags.Compression, 1) == 6 && ifd.Ints(RawTags.StripOffsets, 1) is { Length: 1 } o && ifd.Ints(RawTags.StripByteCounts, 1) is { Length: 1 } n
            && ifd.Int(RawTags.Photometric, 6) is 2 or 6 && ifd.Int(RawTags.BitsPerSample, 8) == 8)
        {
            return new RawPreviewSource(width, height, true, ifd.File.Origin + o[0], n[0], ifd);
        }

        return null;
    }

    /// <summary>A sensor description for a strip-stored maker raw IFD, with the codec that decodes it.</summary>
    internal static RawSensor Sensor(RawIfd ifd, RawCodec codec, int width, int height, int bits, object? data = null, long offset = -1, long count = -1)
    {
        long[] offsets = offset >= 0 ? [offset] : ifd.Ints(RawTags.StripOffsets) ?? ifd.Ints(RawTags.TileOffsets) ?? [];
        long[] counts = count >= 0 ? [count] : ifd.Ints(RawTags.StripByteCounts) ?? ifd.Ints(RawTags.TileByteCounts) ?? [];
        bool tiled = offset < 0 && ifd.Has(RawTags.TileOffsets);
        return new RawSensor
        {
            Ifd = ifd,
            Width = width,
            Height = height,
            Bits = bits,
            Compression = (int)ifd.Int(RawTags.Compression, 1),
            Photometric = DngParser.PhotometricCfa,
            Tiled = tiled,
            UnitWidth = tiled ? (int)ifd.Int(RawTags.TileWidth) : width,
            UnitHeight = tiled ? (int)ifd.Int(RawTags.TileLength) : (int)Math.Clamp(ifd.Int(RawTags.RowsPerStrip, height), 1, Math.Max(height, 1)),
            Offsets = offsets,
            Counts = counts,
            BigEndian = ifd.File.BigEndian,
            Codec = codec,
            CodecData = data,
        };
    }

    internal static RawCfa Bayer(string pattern)
    {
        byte[] colors = [.. pattern.Select(c => (byte)(c == 'R' ? 0 : c == 'G' ? 1 : 2))];
        return new RawCfa(2, 2, colors);
    }

    /// <summary>Reads a TIFF/EP CFA pattern (0x828D repeat size, 0x828E colours) from an IFD, or <see langword="null"/>.</summary>
    private static RawCfa? TiffEpCfa(RawIfd ifd)
    {
        if (ifd.Ints(RawTags.CfaRepeatPatternDim, 2) is { Length: 2 } dims && dims[0] is > 0 and <= 8 && dims[1] is > 0 and <= 8)
        {
            ReadOnlySpan<byte> pattern = ifd.Bytes(RawTags.CfaPattern);
            if (pattern.Length >= dims[0] * dims[1] && pattern[..(int)(dims[0] * dims[1])].IndexOfAnyExceptInRange((byte)0, (byte)2) < 0)
            {
                return new RawCfa((int)dims[0], (int)dims[1], pattern[..(int)(dims[0] * dims[1])].ToArray());
            }
        }

        return null;
    }

    /// <summary>Returns the largest strip-stored image among IFDs that pass <paramref name="accept"/>.</summary>
    private static RawIfd? Largest(List<RawIfd> all, Func<RawIfd, bool> accept)
    {
        return all.Where(i => i.Has(RawTags.StripOffsets) || i.Has(RawTags.TileOffsets)).Where(accept)
            .OrderByDescending(i => i.Int(RawTags.ImageWidth) * i.Int(RawTags.ImageLength)).FirstOrDefault();
    }

    private static RawFrame Nef(RawTiff tiff, List<RawIfd> all, MakerNote? note)
    {
        RawIfd raw = Largest(all, i => i.Has(RawTags.CfaPattern) || i.Int(RawTags.Compression) == 34713)
            ?? throw new InvalidDataException("The NEF has no raw IFD.");
        int width = (int)raw.Int(RawTags.ImageWidth), height = (int)raw.Int(RawTags.ImageLength), bits = (int)raw.Int(RawTags.BitsPerSample, 12);
        long count = raw.Ints(RawTags.StripByteCounts)?.Sum() ?? 0;
        int compression = (int)raw.Int(RawTags.Compression, 1);
        RawCodec codec = RawCodec.Tiff;
        object? data = null;
        if (compression == 34713)
        {
            bool uncompressedSize = count == (long)width * height * bits / 8 || (count > (long)width * height * bits / 8 && (count - ((long)width * height * bits / 8)) < 16L * height);
            byte[] meta = note is null ? [] : note.Ifd.Bytes(0x0096).ToArray() is { Length: > 0 } m96 ? m96 : note.Ifd.Bytes(0x008C).ToArray();
            if (!uncompressedSize && meta.Length > 0)
            {
                codec = RawCodec.Nikon;
                data = new NikonMeta(meta, note!.Ifd.File.BigEndian);
            }
            else
            {
                codec = count >= (long)width * height * 2 ? RawCodec.Unpacked16 : RawCodec.Tiff;
            }
        }
        else if (compression == 1 && count >= (long)width * height * 2)
        {
            codec = RawCodec.Unpacked16;
        }

        RawFrame frame = new() { Sensor = Sensor(raw, codec, width, height, bits, data) };
        frame.Cfa = TiffEpCfa(raw) ?? Bayer("RGGB");
        if (note is not null && note.Ifd.Doubles(0x000C, 4) is { Length: >= 2 } wb && wb[0] > 0 && wb[1] > 0)
        {
            frame.AsShotNeutral = [1 / wb[0], 1, 1 / wb[1]];
        }

        frame.White = [Math.Pow(2, bits) - 1];
        _ = tiff;
        return frame;
    }

    private static RawFrame Pef(List<RawIfd> all, MakerNote? note)
    {
        RawIfd raw = Largest(all, i => i.Int(RawTags.BitsPerSample, 8) >= 12) ?? throw new InvalidDataException("The PEF has no raw IFD.");
        int width = (int)raw.Int(RawTags.ImageWidth), height = (int)raw.Int(RawTags.ImageLength), bits = (int)raw.Int(RawTags.BitsPerSample, 12);
        int compression = (int)raw.Int(RawTags.Compression, 1);
        object? table = compression == 65535 ? PentaxTable(note) : null;
        RawFrame frame = new() { Sensor = Sensor(raw, compression == 65535 ? RawCodec.PentaxHuffman : RawCodec.Tiff, width, height, bits, table) };
        frame.Cfa = TiffEpCfa(raw) ?? Bayer("RGGB");
        if (note is not null)
        {
            // Tag 0x0200 does not hold the sensor's black on every body (a K-5 says 65 for about 512): black comes from the camera table.
            if (note.Ifd.Doubles(0x0201, 4) is { Length: 4 } wb && wb.All(v => v > 0))
            {
                frame.AsShotNeutral = [wb[1] / wb[0], 1, wb[1] / wb[3]];
            }
        }

        return frame;
    }

    /// <summary>Returns the Pentax Huffman table bytes of maker-note tag 0x0220, or <see langword="null"/> for the legacy fixed table.</summary>
    private static PentaxTable? PentaxTable(MakerNote? note)
    {
        if (note is null)
        {
            return null;
        }

        byte[] bytes = note.Ifd.Bytes(0x0220).ToArray();
        return bytes.Length >= 16 ? new PentaxTable(bytes, note.Ifd.File.BigEndian) : null;
    }

    private static RawFrame Arw(RawTiff tiff, RawIfd ifd0, List<RawIfd> all)
    {
        RawIfd raw = Largest(all, i => i.Int(RawTags.Compression) is 32767 or 7 or 1 && i.Int(RawTags.BitsPerSample, 8) >= 8 && i.Int(RawTags.Photometric, DngParser.PhotometricCfa) == DngParser.PhotometricCfa && i.Int(RawTags.ImageWidth) > 640)
            ?? throw new InvalidDataException("The ARW has no raw IFD.");
        int width = (int)raw.Int(RawTags.ImageWidth), height = (int)raw.Int(RawTags.ImageLength), bits = (int)raw.Int(RawTags.BitsPerSample, 12);
        int compression = (int)raw.Int(RawTags.Compression, 1);
        long count = raw.Ints(RawTags.StripByteCounts)?.Sum() ?? 0;
        RawCodec codec;
        object? data = null;
        int white = 16383;
        if (compression == 7)
        {
            codec = RawCodec.SonyLossless;
        }
        else if (compression == 32767 && count == (long)width * height)
        {
            codec = RawCodec.SonyArw2;
            data = SonyCurve(raw);
            bits = 14;
        }
        else if (compression == 32767 && count == (long)width * height * 2)
        {
            codec = RawCodec.Unpacked16;
            bits = 14;
        }
        else if (compression == 32767 && bits == 12 && count == (long)width * height * 3 / 2)
        {
            codec = RawCodec.Packed12Lsb;
            white = 4095;
        }
        else if (compression == 32767)
        {
            codec = RawCodec.SonyArw1;
            white = 4095;
        }
        else
        {
            codec = RawCodec.Unpacked16;
            bits = 14;
        }

        RawFrame frame = new() { Sensor = Sensor(raw, codec, width, height, bits, data) };
        frame.Cfa = TiffEpCfa(raw) ?? Bayer("RGGB");
        frame.White = [white];
        if (raw.Ints(0x7038, 2) is { Length: 2 } size && size[0] > 0 && size[1] > 0 && size[0] <= width && size[1] <= height)
        {
            frame.Active = new Rect(0, 0, (int)size[0], (int)size[1]);
            frame.Crop = (0, 0, size[0], size[1]);
            frame.HasOwnCrop = true;
        }

        SonyPrivate.Read(tiff, ifd0, frame, codec == RawCodec.Packed12Lsb);
        return frame;
    }

    /// <summary>Builds the ARW2 tone curve from tag 0x7010: slopes 1, 2, 4, 8 and 16 between its points (Sony spec, section 3).</summary>
    private static ushort[] SonyCurve(RawIfd raw)
    {
        int[] points = [0, 0, 0, 0, 0, 4095];
        if (raw.Ints(0x7010, 4) is { Length: 4 } tag)
        {
            for (int i = 0; i < 4; i++)
            {
                points[i + 1] = (int)((tag[i] >> 2) & 0xFFF);
            }
        }

        ushort[] curve = new ushort[0x4001];
        for (int i = 0; i < curve.Length; i++)
        {
            curve[i] = (ushort)i;
        }

        for (int k = 0; k < 5; k++)
        {
            for (int j = points[k] + 1; j <= points[k + 1] && j < curve.Length; j++)
            {
                curve[j] = (ushort)Math.Min(65535, curve[j - 1] + (1 << k));
            }
        }

        return curve;
    }

    private static RawFrame Orf(RawTiff tiff, RawIfd ifd0, MakerNote? note)
    {
        int width = (int)ifd0.Int(RawTags.ImageWidth), height = (int)ifd0.Int(RawTags.ImageLength);
        long[] offsets = ifd0.Ints(RawTags.StripOffsets) ?? throw new InvalidDataException("The ORF has no raw data.");
        long[] counts = ifd0.Ints(RawTags.StripByteCounts) ?? [];
        long size = counts.Length == offsets.Length && offsets.Length > 0 ? offsets[^1] + counts[^1] - offsets[0] : 0;
        RawIfd? processing = note?.Sub(0x2040);
        int bits = (int)(processing?.Int(0x0611, 12) ?? 12);
        RawCodec codec;
        object? data = null;
        long w = width, h = height;
        if (size == h * ((w * 3 / 2) + ((w + 2) / 10)))
        {
            codec = RawCodec.OlympusPacked;
            data = OlympusPacking.WithControl;
        }
        else if (size == w * h * 3 / 2)
        {
            codec = RawCodec.OlympusPacked;
            data = OlympusPacking.Msb32;
        }
        else if (size == w * h * 2)
        {
            codec = RawCodec.OlympusPacked;
            data = OlympusPacking.LeftAligned;
        }
        else if (size > w * h * 3 / 2)
        {
            codec = RawCodec.OlympusPacked;
            data = OlympusPacking.Interlaced;
        }
        else
        {
            codec = RawCodec.Olympus;
            ushort[] t = [0, 5, 0, 2, 5, 4, 3, 2, 3, 4, 7, 15, 12, (ushort)(bits > 12 ? 6 : 5)];
            for (int i = 0; i < 14; i++)
            {
                if (processing?.TryGet(0x0640 + i, out RawEntry e) == true)
                {
                    t[i] = (ushort)processing.Int(0x0640 + i);
                }
            }

            data = new OlympusParams(t, bits);
        }

        RawFrame frame = new() { Sensor = Sensor(ifd0, codec, width, height, bits, data, offsets[0], size) };
        frame.Cfa = Bayer("RGGB");
        frame.White = [Math.Pow(2, bits) - 1];
        if (processing is not null)
        {
            if (processing.Doubles(0x0100, 4) is { Length: >= 2 } wb && wb[0] > 0 && wb[1] > 0)
            {
                frame.AsShotNeutral = [256 / wb[0], 1, 256 / wb[1]];
            }

            if (processing.Doubles(0x0600, 4) is { Length: 4 } black)
            {
                frame.BlackRows = frame.BlackColumns = 2;
                frame.Black = black;
                frame.HasOwnLevels = true;
            }

            if (processing.Ints(0x0612, 1) is { Length: 1 } left && processing.Ints(0x0613, 1) is { Length: 1 } top
                && processing.Ints(0x0614, 1) is { Length: 1 } cw && processing.Ints(0x0615, 1) is { Length: 1 } ch
                && cw[0] > 0 && ch[0] > 0 && left[0] + cw[0] <= width && top[0] + ch[0] <= height)
            {
                frame.Active = new Rect((int)left[0], (int)top[0], (int)cw[0], (int)ch[0]);
                frame.Crop = (0, 0, cw[0], ch[0]);
                frame.HasOwnCrop = true;
            }
        }

        _ = tiff;
        return frame;
    }

    private static RawFrame Rw2(RawTiff tiff, RawIfd ifd0)
    {
        int width = (int)ifd0.Int(0x0002), height = (int)ifd0.Int(0x0003), bits = (int)ifd0.Int(0x000A, 12);
        RawCodec codec;
        long offset;
        long count;
        if (ifd0.Has(0x0118))
        {
            offset = ifd0.Int(0x0118);
            count = 0;
            codec = ifd0.Int(0x002D, 4) switch
            {
                5 => RawCodec.PanasonicV5,
                6 => RawCodec.PanasonicV6,
                7 => RawCodec.PanasonicV7,
                8 => RawCodec.PanasonicV8,
                _ => RawCodec.PanasonicV4,
            };
        }
        else
        {
            width = (int)ifd0.Int(RawTags.ImageWidth, width);
            height = (int)ifd0.Int(RawTags.ImageLength, height);
            offset = ifd0.Ints(RawTags.StripOffsets, 1)?[0] ?? throw new InvalidDataException("The RW2 has no raw data.");
            count = 0;
            codec = RawCodec.PanasonicV4;
        }

        RawFrame frame = new() { Sensor = Sensor(ifd0, codec, width, height, bits, new PanasonicTags(ifd0, ifd0.Has(0x0118)), offset, count) };
        frame.Cfa = Bayer(ifd0.Int(0x0009, 1) switch { 2 => "GRBG", 3 => "GBRG", 4 => "BGGR", _ => "RGGB" });
        frame.White = [Math.Pow(2, bits) - 1];
        if (ifd0.Has(0x0024) && ifd0.Has(0x0025) && ifd0.Has(0x0026))
        {
            double r = ifd0.Double(0x0024), g = ifd0.Double(0x0025), b = ifd0.Double(0x0026);
            if (r > 0 && g > 0 && b > 0)
            {
                frame.AsShotNeutral = [g / r, 1, g / b];
            }
        }

        if (ifd0.Ints(0x0004, 1) is { Length: 1 } top && ifd0.Ints(0x0005, 1) is { Length: 1 } left
            && ifd0.Ints(0x0006, 1) is { Length: 1 } bottom && ifd0.Ints(0x0007, 1) is { Length: 1 } right
            && bottom[0] > top[0] && right[0] > left[0] && bottom[0] <= height && right[0] <= width)
        {
            frame.Active = new Rect((int)left[0], (int)top[0], (int)(right[0] - left[0]), (int)(bottom[0] - top[0]));
            frame.Crop = (0, 0, frame.Active.Width, frame.Active.Height);
            frame.HasOwnCrop = true;
        }

        return frame;
    }

    /// <summary>
    /// Canon's as-shot white balance as a neutral: ColorData's WB_RGGBLevelsAsShot, at the index its version (told by its
    /// length) puts it, as ExifTool's Canon tag page lists them; or <see langword="null"/>.
    /// </summary>
    private static double[]? CanonAsShot(MakerNote? note)
    {
        if (note?.Ifd.Ints(0x4001, 8192) is not long[] color)
        {
            return null;
        }

        int index = color.Length switch
        {
            582 => 25,
            653 => 34,
            796 or 674 or 692 or 702 or 1227 or 1250 or 1251 or 1337 or 1338 or 1346 or 1273 or 1275 or 1312 or 1313 or 1316 or 1506
                or 1560 or 1592 or 1353 or 1602 => 63,
            5120 or 1816 or 1820 or 1824 => 71,
            2024 or 3656 => 85,
            3973 or 3778 or 4528 => 105,
            _ => -1,
        };
        return index >= 0 && index + 4 <= color.Length && color[index] > 0 && color[index + 1] > 0 && color[index + 3] > 0
            ? [color[index + 1] / (double)color[index], 1, color[index + 1] / (double)color[index + 3]]
            : null;
    }

    /// <summary>
    /// A Canon sRAW or mRAW CR2 (CR2 spec, sections 5 and 6), decoded to LinearRaw RGB. The decode stops before the spec's
    /// sRAW multipliers, which leaves the values Adobe DNG Converter stores for these files (measured bit for bit); the
    /// multipliers' levels (ColorData at the offset its length gives) instead divide the as-shot neutral, and the white level
    /// is the one Adobe gives the camera's reduced-resolution data, from the camera table.
    /// </summary>
    private static RawFrame CanonSraw(RawIfd ifd0, RawIfd raw, LosslessJpeg header, long[] slices, MakerNote? note)
    {
        int h = header.H0, v = header.V0;
        if (header.Components != 3 || h != 2 || v is not (1 or 2))
        {
            throw new NotSupportedException("The CR2's sRAW sampling is not one Egami reads.");
        }

        long groups = (long)header.McusPerLine * header.McuLines;
        // Slice widths count decoded values of one row of groups (some frames' headers are transposed, so the frame's own
        // width is not used when slices are given).
        int perGroup = (h * v) + 2;
        int width = slices is { Length: 3 } && slices[1] > 0 ? (int)(((slices[0] * slices[1]) + slices[2]) / perGroup * h) : header.McusPerLine * h;
        int height = (int)Math.Min(groups * h * v / Math.Max(width, 1), int.MaxValue);
        if (width <= 0 || height <= 0 || width % h != 0 || height % v != 0)
        {
            throw new InvalidDataException("The CR2's sRAW frame does not fit its slices.");
        }

        double[] levels = [1, 1, 1];
        if (note?.Ifd.Ints(0x4001, 8192) is long[] color)
        {
            int at = color.Length switch
            {
                674 or 692 or 702 or 1227 or 1250 or 1251 or 1337 or 1338 or 1346 => 0x4E,
                1273 or 1275 => 0x62,
                1312 or 1313 or 1316 or 1506 => 0x7B,
                1560 or 1592 or 1353 or 1602 => 0x80,
                _ => -1,
            };
            if (at >= 0 && at + 4 <= color.Length && color.Skip(at).Take(4).All(c => c > 0))
            {
                levels = [color[at] / (double)color[at + 1], 1, color[at + 3] / (double)color[at + 1]];
            }
        }

        // The body decides the formula set and the hue: by model, model ID (maker-note tag 0x0010) and firmware (tag 0x0007).
        long id = note?.Ifd.Int(0x0010) ?? 0;
        string model = ifd0.Ascii(RawTags.Model) ?? "";
        bool setA = model.EndsWith("5D Mark II", StringComparison.OrdinalIgnoreCase) || model.EndsWith("EOS 7D", StringComparison.OrdinalIgnoreCase)
            || model.EndsWith("EOS 50D", StringComparison.OrdinalIgnoreCase) || model.EndsWith("1D Mark IV", StringComparison.OrdinalIgnoreCase)
            || model.EndsWith("EOS 60D", StringComparison.OrdinalIgnoreCase);
        int f = (h * v) - 1;
        int hue = (f + 1) * 4;
        long firmware = 0;
        if (note?.Ifd.Ascii(0x0007) is string text && Regex.Match(text, @"(\d+)\.(\d+)\.(\d+)") is { Success: true } version)
        {
            firmware = (long.Parse(version.Groups[1].Value, CultureInfo.InvariantCulture) * 1_000_000)
                + (long.Parse(version.Groups[2].Value, CultureInfo.InvariantCulture) * 1000) + long.Parse(version.Groups[3].Value, CultureInfo.InvariantCulture);
        }

        if (id >= 0x80000281 || (id == 0x80000218 && firmware > 1000006))
        {
            hue = f * 2;
        }

        RawSensor strip = Sensor(raw, RawCodec.CanonSraw, width, height, 15);
        RawFrame frame = new()
        {
            Sensor = new RawSensor
            {
                Ifd = raw,
                Width = width,
                Height = height,
                Samples = 3,
                Bits = 15,
                Photometric = DngParser.PhotometricLinearRaw,
                UnitWidth = width,
                UnitHeight = height,
                Offsets = strip.Offsets,
                Counts = strip.Counts,
                BigEndian = strip.BigEndian,
                Codec = RawCodec.CanonSraw,
                CodecData = new CanonSraw(slices, h, v, setA, hue, id is > 0 and < 0x80000218),
            },
        };
        RawCameraEntry? camera = RawCameraTable.Find(ifd0.Ascii(RawTags.Make) ?? "", model);
        double white = camera?.ReducedWhite ?? (camera is { White: > 0 } ? camera.White - camera.Black.Average() : 16383);
        frame.Black = [0, 0, 0];
        frame.White = [white, white, white];
        if (CanonAsShot(note) is double[] neutral)
        {
            frame.AsShotNeutral = [neutral[0] / levels[0], 1, neutral[2] / levels[2]];
        }

        frame.HasOwnLevels = true;
        frame.HasOwnCrop = true;
        frame.Active = new Rect(0, 0, width, height);
        frame.Crop = (0, 0, width, height);
        return frame;
    }

    private static RawFrame Cr2(RawTiff tiff, List<RawIfd> chain, List<RawIfd> all, MakerNote? note)
    {
        RawIfd raw = (chain.Count >= 4 ? chain[3] : null) ?? Largest(all, i => i.Int(RawTags.Compression) == 6)
            ?? throw new InvalidDataException("The CR2 has no raw IFD.");
        long[] slices = raw.Ints(0xC640, 3) ?? [];
        long start = tiff.Origin + (raw.Ints(RawTags.StripOffsets, 1)?[0] ?? 0);
        long size = raw.Ints(RawTags.StripByteCounts, 1)?[0] ?? 0;
        if (start <= 0 || size <= 0 || !tiff.Source.Ensure(start + Math.Min(size, 1 << 16)))
        {
            throw new InvalidDataException("The CR2's raw data lies outside the file.");
        }

        // The image size: the slices' total width, and the frame's values over it (CR2 spec, sections 3 and 4).
        LosslessJpeg header = LosslessJpeg.Open(tiff.Data, (int)start, (int)Math.Min(size, tiff.Length - start));
        int width = slices is { Length: 3 } && slices[1] > 0 ? (int)((slices[0] * slices[1]) + slices[2]) : header.Width * header.Components;
        int height = (int)Math.Min(header.SampleCount / Math.Max(width, 1), int.MaxValue);
        if (header.H0 != 1 || header.V0 != 1)
        {
            return CanonSraw(chain[0], raw, header, slices, note);
        }

        RawSensor sensor = Sensor(raw, RawCodec.Canon, width, height, 14, new CanonSlices(slices));
        RawFrame frame = new() { Sensor = sensor };
        frame.Cfa = Bayer("RGGB");
        if (note?.Ifd.Ints(0x00E0, 17) is { Length: >= 9 } info && info[1] > 0 && info[2] > 0)
        {
            // SensorInfo: sensor width and height, then the active area's left, top, right and bottom, inclusive.
            int left = (int)info[5], top = (int)info[6], right = (int)info[7], bottom = (int)info[8];
            if (right > left && bottom > top && right < info[1] && bottom < info[2])
            {
                frame.Active = new Rect(left, top, right - left + 1, bottom - top + 1);
                frame.Crop = (0, 0, frame.Active.Width, frame.Active.Height);
                frame.HasOwnCrop = true;
            }
        }

        frame.AsShotNeutral = CanonAsShot(note) ?? frame.AsShotNeutral;

        // Black is measured from the masked columns left of the active area when nothing else gives it.
        frame.MaskedArea = frame.Active.X >= 16 ? new Rect(4, frame.Active.Y, frame.Active.X - 8, frame.Active.Height) : null;
        _ = tiff;
        return frame;
    }
}

/// <summary>
/// A Canon sRAW or mRAW frame's decoding parameters (CR2 spec, sections 5 and 6): the slices, the luma sampling factors,
/// which of the two formula sets the body uses, the hue offset and whether luma carries the older bodies' 512 offset.
/// </summary>
internal sealed record CanonSraw(long[] Slices, int H, int V, bool SetA, int Hue, bool Older);

/// <summary>A maker note: its IFD (with its own byte order and offset base) and its bytes.</summary>
internal sealed class MakerNote(RawIfd ifd, byte[] bytes)
{
    public RawIfd Ifd { get; } = ifd;

    public byte[] Bytes { get; } = bytes;

    public RawIfd? Sub(int tag) => Ifd.Sub(tag);

    /// <summary>
    /// Finds the maker note (EXIF tag 0x927C) and opens its IFD by the layout each maker uses, as ExifTool's tag pages describe
    /// them: Nikon's own TIFF after <c>Nikon\0</c>, Pentax's <c>AOC\0</c> and byte order, Olympus's <c>OLYMPUS\0</c> and
    /// <c>OM SYSTEM</c> headers, and Canon's and Sony's plain IFDs.
    /// </summary>
    public static MakerNote? Read(RawTiff tiff, RawIfd? exif, RawKind kind)
    {
        if (exif is null || !exif.TryGet(RawTags.MakerNote, out RawEntry entry))
        {
            return null;
        }

        int at = (int)entry.ValueAt;
        byte[] bytes = exif.Bytes(entry).ToArray();
        ReadOnlySpan<byte> b = bytes;
        try
        {
            if (b.StartsWith("Nikon\0"u8) && b.Length > 18)
            {
                bool be = b[10] == 'M';
                RawTiff inner = new(tiff.Source, be, at + 10);
                uint first = be ? BinaryPrimitives.ReadUInt32BigEndian(b[14..]) : BinaryPrimitives.ReadUInt32LittleEndian(b[14..]);
                return inner.ReadIfd(first) is RawIfd ifd ? new MakerNote(ifd, bytes) : null;
            }

            if (b.StartsWith("PENTAX \0"u8) && b.Length > 12)
            {
                bool pbe = b[8] == 'M';
                RawTiff pentax = new(tiff.Source, pbe, at);
                return pentax.ReadIfd(10) is RawIfd pifd ? new MakerNote(pifd, bytes) : null;
            }

            if (b.StartsWith("AOC\0"u8) && b.Length > 6)
            {
                bool be = b[4] == 'M';
                RawTiff inner = new(tiff.Source, be, kind == RawKind.Pef ? 0 : at);
                long offset = kind == RawKind.Pef ? at + 6 : 6;
                return inner.ReadIfd(offset) is RawIfd ifd ? new MakerNote(ifd, bytes) : null;
            }

            if (b.StartsWith("OLYMPUS\0"u8) || b.StartsWith("OM SYSTEM\0"u8))
            {
                int header = b[0] == 'O' && b[1] == 'L' ? 8 : 12;
                bool be = b[header] == 'M';
                RawTiff inner = new(tiff.Source, be, at);
                return inner.ReadIfd(header + 4) is RawIfd ifd ? new MakerNote(ifd, bytes) : null;
            }

            if (b.StartsWith("SONY DSC \0\0\0"u8) || b.StartsWith("SONY CAM \0\0\0"u8))
            {
                return tiff.ReadIfd(at + 12 - tiff.Origin) is RawIfd ifd ? new MakerNote(ifd, bytes) : null;
            }

            // Canon (and anything else): an IFD at the start, its offsets counted from the file's TIFF header.
            return tiff.ReadIfd(at - tiff.Origin) is RawIfd plain ? new MakerNote(plain, bytes) : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

/// <summary>Nikon's metadata block (maker-note tag 0x0096): versions, initial predictors, curve and split row (NEF spec, section 3).</summary>
internal sealed record NikonMeta(byte[] Bytes, bool BigEndian);

/// <summary>Pentax's Huffman table (maker-note tag 0x0220, PEF spec, section 2).</summary>
internal sealed record PentaxTable(byte[] Bytes, bool BigEndian);

/// <summary>Canon's slice tag 0xC640: count, slice width, last slice width (CR2 spec, section 3).</summary>
internal sealed record CanonSlices(long[] Values);

/// <summary>Olympus's 14 decoder parameters (maker-note tags 0x0640 to 0x0653) and the sample depth (ORF spec, sections 4 and 5).</summary>
internal sealed record OlympusParams(ushort[] T, int Bits);

/// <summary>The uncompressed ORF layouts (ORF spec, sections 1 and 6).</summary>
internal enum OlympusPacking
{
    WithControl,
    Msb32,
    LeftAligned,
    Interlaced,
}

/// <summary>The Panasonic raw IFD's tags a decoder reads, and whether the file is a modern one with tag 0x0118 (RW2 spec, section 1).</summary>
internal sealed record PanasonicTags(RawIfd Ifd, bool Modern);
