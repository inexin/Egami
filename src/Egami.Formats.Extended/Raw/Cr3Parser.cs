using System.Buffers.Binary;

using Egami.Formats.Iso;

namespace Egami.Formats.Extended.Raw;

/// <summary>
/// Reads Canon's CR3 container (ISO/IEC 14496-12, brand <c>crx </c>) for its previews and metadata: the Canon box (a uuid box in
/// moov) holds four TIFF blocks, CMT1 (IFD0), CMT2 (Exif), CMT3 (the maker note) and CMT4 (GPS), and the thumbnail THMB; a
/// top-level uuid box holds the preview PRVW; the first track is a full-size JPEG, and the tracks with a CMP1 entry hold the
/// CRX-coded sensor data (CRX spec, section 2), which <see cref="CrxDecoder"/> decodes. Box layouts were read from the sample
/// files themselves and the CRX spec.
/// </summary>
internal static class Cr3Parser
{
    private const string Format = "CR3";

    /// <summary>The Canon box's uuid, 85c0b687-820f-11e0-8111-f4ce462b6a48.</summary>
    private static readonly Guid CanonBox = Uuid([0x85, 0xc0, 0xb6, 0x87, 0x82, 0x0f, 0x11, 0xe0, 0x81, 0x11, 0xf4, 0xce, 0x46, 0x2b, 0x6a, 0x48]);

    /// <summary>The preview box's uuid, eaf42b5e-1c98-4b88-b9fb-b7dc406e4d16.</summary>
    private static readonly Guid PreviewBox = Uuid([0xea, 0xf4, 0x2b, 0x5e, 0x1c, 0x98, 0x4b, 0x88, 0xb9, 0xfb, 0xb7, 0xdc, 0x40, 0x6e, 0x4d, 0x16]);

    /// <summary>Gets whether a header is a CR3's: an ftyp box whose major brand is <c>crx </c>.</summary>
    public static bool IsCr3(ReadOnlySpan<byte> header) => header.Length >= 12 && header[4..8].SequenceEqual("ftyp"u8) && header[8..12].SequenceEqual("crx "u8);

    /// <summary>Reads a CR3's boxes up to its media data; returns the frame and the CMT1 TIFF, which holds the file's metadata.</summary>
    public static (RawFrame Frame, RawTiff Tiff) Parse(RawSource source)
    {
        RawTiff? cmt1 = null, cmt2 = null, cmt3 = null, cmt4 = null;
        byte[]? makerNote = null;
        RawPreviewSource? thumbnail = null, preview = null, fullJpeg = null;
        TrackInfo? raw = null, ctmd = null;
        long position = 0;
        while (source.Ensure(position + 8))
        {
            source.Ensure(position + IsoBoxReader.MaxHeaderSize);
            int available = (int)Math.Min(IsoBoxReader.MaxHeaderSize, source.Length - position);
            IsoBox box = IsoBoxReader.ReadHeader(source.Data.AsSpan((int)position, available), position, long.MaxValue, Format);
            if (box.Type == Code("mdat") || box.End > int.MaxValue)
            {
                break;
            }

            if (box.Type == Code("moov"))
            {
                Need(source, box.End);
                IsoBoxReader moov = new(source.Data.AsSpan((int)box.ContentStart, (int)box.ContentLength), Format, box.ContentStart);
                while (moov.Next(out IsoBox child))
                {
                    if (child.Type == Code("uuid") && child.UserType == CanonBox)
                    {
                        IsoBoxReader canon = moov.Children(child);
                        while (canon.Next(out IsoBox item))
                        {
                            switch (Name(item.Type))
                            {
                                case "CMT1":
                                    cmt1 = Tiff(source, item);
                                    break;
                                case "CMT2":
                                    cmt2 = Tiff(source, item);
                                    break;
                                case "CMT3":
                                    makerNote = canon.Content(item).ToArray();
                                    cmt3 = Tiff(source, item);
                                    break;
                                case "CMT4":
                                    cmt4 = Tiff(source, item);
                                    break;
                                case "THMB":
                                    thumbnail = Jpeg(source, item);
                                    break;
                            }
                        }
                    }
                    else if (child.Type == Code("trak") && Track(moov.Children(child)) is { } track)
                    {
                        if (track.Ctmd)
                        {
                            ctmd ??= track;
                        }
                        else if (track.Jpeg)
                        {
                            fullJpeg ??= Sized(source, track.Offset, track.Size, track.Width, track.Height);
                        }
                        else if (raw is null || track.Weight > raw.Weight)
                        {
                            // The largest raw track (CRX spec, section 2); of equal ones, as in a Dual Pixel file, the first.
                            raw = track;
                        }
                    }
                }
            }
            else if (box.Type == Code("uuid") && box.UserType == PreviewBox)
            {
                // Eight bytes, then a PRVW box.
                long at = box.ContentStart + 8;
                if (source.Ensure(at + IsoBoxReader.MaxHeaderSize) && at < box.End)
                {
                    IsoBox prvw = IsoBoxReader.ReadHeader(source.Data.AsSpan((int)at, IsoBoxReader.MaxHeaderSize), at, box.End, Format);
                    if (prvw.Type == Code("PRVW"))
                    {
                        preview = Jpeg(source, prvw);
                    }
                }
            }

            position = box.End;
        }

        if (Ifd0(cmt1) is not RawIfd ifd0)
        {
            throw new InvalidDataException("The CR3 has no CMT1 metadata block.");
        }

        if (raw is null)
        {
            throw new InvalidDataException("The CR3 has no raw image track.");
        }

        CrxHeader crx = raw.Crx!;
        int width = crx.FrameWidth > 0 ? crx.FrameWidth : raw.Width, height = crx.FrameHeight > 0 ? crx.FrameHeight : raw.Height;
        RawFrame frame = new()
        {
            Sensor = new RawSensor
            {
                Ifd = ifd0,
                Width = width,
                Height = height,
                Bits = Math.Clamp(crx.OutputBits, 1, 16),
                Photometric = DngParser.PhotometricCfa,
                UnitWidth = width,
                UnitHeight = height,
                // Sensor offsets count from the metadata TIFF's origin, as every format's do.
                Offsets = [raw.Offset - cmt1!.Origin],
                Counts = [raw.Size],
                Codec = RawCodec.CanonCrx,
                CodecData = crx,
            },
        };
        frame.Cfa = MakerParser.Bayer(crx.Pattern);
        frame.MetadataIfd = ifd0;
        frame.ExifIfd = Ifd0(cmt2);
        frame.GpsIfd = Ifd0(cmt4);
        frame.Make = ifd0.Ascii(RawTags.Make) ?? "";
        frame.Model = ifd0.Ascii(RawTags.Model) ?? "";
        frame.Orientation = (int)ifd0.Int(RawTags.Orientation, 1);
        RawIfd? exif = frame.ExifIfd;
        double? Exif(int tag) => exif?.Has(tag) == true ? exif.Double(tag) : null;
        frame.Iso = Exif(RawTags.IsoSpeed);
        frame.ExposureTime = Exif(RawTags.ExposureTime);
        frame.FNumber = Exif(RawTags.FNumber);
        frame.FocalLength = Exif(RawTags.FocalLength);
        frame.LensModel = exif?.Ascii(RawTags.LensModel);
        frame.MakerNote = makerNote;

        frame.Active = new Rect(0, 0, width, height);
        frame.Crop = (0, 0, width, height);
        if (raw.Areas is int[] a)
        {
            // IAD1's areas are inclusive (CRX spec, section 2.2); the crop counts from the active area, as DNG's does.
            Rect active = new(a[12], a[13], a[14] - a[12] + 1, a[15] - a[13] + 1);
            if (active.Width > 0 && active.Height > 0 && active.X + active.Width <= width && active.Y + active.Height <= height)
            {
                frame.Active = active;
                frame.Crop = (0, 0, active.Width, active.Height);
                frame.HasOwnCrop = true;
                int cw = a[2] - a[0] + 1, ch = a[3] - a[1] + 1;
                if (cw > 0 && ch > 0 && a[0] >= active.X && a[1] >= active.Y && a[0] + cw <= active.X + active.Width && a[1] + ch <= active.Y + active.Height)
                {
                    frame.Crop = (a[0] - active.X, a[1] - active.Y, cw, ch);
                }
            }
        }

        // ColorData is in the timed metadata's maker note on the bodies sampled, not in CMT3 (CRX spec, section 2.1).
        MakerNote? note = TimedMakerNote(source, ctmd);
        if (note?.Ifd.Has(0x4001) != true && Ifd0(cmt3) is RawIfd noteIfd)
        {
            note = new MakerNote(noteIfd, makerNote ?? []);
        }

        Levels(frame, note, crx.OutputBits, crx.CfaLayout);
        frame.AsShotNeutral = MakerParser.CanonAsShot(note) ?? frame.AsShotNeutral;
        MakerParser.CameraTable(frame);
        if (frame.HasOwnLevels && !OlderColorData(note) && RawCameraTable.Find(frame.Make, frame.Model) is { White: > 0 } camera)
        {
            // Adobe's white level for the newer bodies (the R6's 16384, the 1D X Mark III's 16050) is not ColorData's specular
            // white (14008 on both); the table's, measured from Adobe's DNG, keeps develops and DNGs alike. The older bodies'
            // follows the file's specular white (measured: 11892 and 14338 on two M50 files).
            frame.White = [camera.White];
        }
        else if (frame.HasOwnLevels && RawCameraTable.Find(frame.Make, frame.Model) is { White: > 0 } older && frame.White[0] != older.White)
        {
            // An older body whose file's white is not the table's (a C-RAW M50: 14338 against the lossless file's 11892) gets
            // Adobe's baseline exposure raised by their ratio, measured: 0.28 EV against the table's 0.02.
            frame.BaselineExposure += Math.Log2(frame.White[0] / older.White);
        }
        foreach (RawPreviewSource? p in new[] { fullJpeg, preview, thumbnail })
        {
            if (p is not null)
            {
                frame.Previews.Add(p);
            }
        }

        return (frame, cmt1!);
    }

    /// <summary>Reads a CMT block's first IFD, or <see langword="null"/>.</summary>
    private static RawIfd? Ifd0(RawTiff? tiff)
    {
        return tiff is not null && RawTiff.TryHeader(tiff.Data.AsSpan(tiff.Origin, (int)Math.Min(16, tiff.Length - tiff.Origin)), out _, out _, out uint first)
            ? tiff.ReadIfd(first)
            : null;
    }

    /// <summary>
    /// Black and white levels from ColorData (maker-note tag 0x4001, CRX spec, section 11), at the indices its length gives: the
    /// per-channel black (or the fallback block when that averages 0) and the specular white, both scaled down for data of fewer
    /// than 14 bits when they were written for 14.
    /// </summary>
    private static void Levels(RawFrame frame, MakerNote? note, int bits, int layout)
    {
        if (note?.Ifd.Ints(0x4001, 8192) is not long[] color)
        {
            return;
        }

        int subVersion = color.Length > 0 ? (short)color[0] : 0;
        (int black, int white, int fallback) = color.Length switch
        {
            1816 or 1820 or 1824 => (0x318, 0x31D, 0x149),
            1770 or 2024 or 3656 => (0x326, 0x32B, 0x157),
            3778 when subVersion == 65 => (0x290, 0x295, 0x17F),
            3973 or 3778 => (0x27C, 0x281, 0x16B),
            4528 => (0x290, 0x295, 0x17F),
            _ => (-1, -1, -1),
        };
        if (black < 0 || black + 4 > color.Length || white >= color.Length || fallback + 4 > color.Length || color[white] <= 0)
        {
            return;
        }

        double[] levels = [.. color.Skip(black).Take(4).Select(v => (double)v)];
        if (levels.Average() == 0)
        {
            levels = [.. color.Skip(fallback).Take(4).Select(v => (double)v)];
        }

        double specular = color[white];
        if (bits < 14 && specular > (1 << bits) - 1)
        {
            double scale = 1 << (14 - bits);
            levels = [.. levels.Select(v => v / scale)];
            specular /= scale;
        }

        // The four values are R, G1, G2, B, the planes' order: laid out on the CFA's 2 × 2 cell by the plane each cell holds,
        // counted from the active area's corner as DNG's BlackLevel repeat pattern is.
        int ox = frame.Active.X & 1, oy = frame.Active.Y & 1;
        frame.BlackRows = frame.BlackColumns = 2;
        frame.Black = [.. Enumerable.Range(0, 4).Select(cell => levels[((((cell >> 1) ^ oy) << 1) | ((cell & 1) ^ ox)) ^ layout])];
        frame.White = [specular];
        frame.HasOwnLevels = true;
    }

    /// <summary>
    /// Reads the maker note of the timed metadata track's first sample (CRX spec, section 2.1): little-endian records of a u32
    /// size and a u16 type; a type 8 record holds, from its byte 12, (u32 length, u32 tag) entries, and tag 0x927C is a Canon
    /// maker note TIFF with its own header.
    /// </summary>
    private static MakerNote? TimedMakerNote(RawSource source, TrackInfo? ctmd)
    {
        if (ctmd is null || ctmd.Size > 1 << 20 || ctmd.Offset + ctmd.Size > int.MaxValue || !source.Ensure(ctmd.Offset + ctmd.Size))
        {
            return null;
        }

        int start = (int)ctmd.Offset, end = (int)(ctmd.Offset + ctmd.Size);
        ReadOnlySpan<byte> data = source.Data;
        for (int at = start; at + 12 <= end;)
        {
            int size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[at..]), int.MaxValue);
            int type = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 4)..]);
            if (size < 12 || size > end - at)
            {
                break;
            }

            for (int e = at + 12; type == 8 && e + 8 <= at + size;)
            {
                int length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[e..]), int.MaxValue);
                if (length < 8 || length > at + size - e)
                {
                    break;
                }

                if (BinaryPrimitives.ReadUInt32LittleEndian(data[(e + 4)..]) == RawTags.MakerNote
                    && RawTiff.TryHeader(data.Slice(e + 8, length - 8), out bool bigEndian, out ushort magic, out uint first) && magic == 42)
                {
                    RawTiff tiff = new(source, bigEndian, e + 8);
                    return tiff.ReadIfd(first) is RawIfd ifd ? new MakerNote(ifd, data.Slice(e + 8, length - 8).ToArray()) : null;
                }

                e += length;
            }

            at += size;
        }

        return null;
    }

    /// <summary>Gets whether ColorData is of the 2018-2019 bodies' versions (1816, 1820 or 1824 values: M50, R, RP, 250D and others).</summary>
    private static bool OlderColorData(MakerNote? note) => note?.Ifd.TryGet(0x4001, out RawEntry e) == true && e.Count is 1816 or 1820 or 1824;

    /// <summary>A track's first sample entry and first sample.</summary>
    private sealed record TrackInfo(bool Jpeg, int Width, int Height, long Offset, long Size, CrxHeader? Crx, int[]? Areas, bool Ctmd = false)
    {
        /// <summary>Gets the raw track's size measure (CRX spec, section 2): bits times width times height.</summary>
        public long Weight => Crx is null ? (long)Width * Height : (long)Crx.Bits * Crx.FrameWidth * Crx.FrameHeight;
    }

    /// <summary>Reads one track: its first sample entry (a CRAW entry with a JPEG or a CMP1 box), size, and first sample's place.</summary>
    private static TrackInfo? Track(IsoBoxReader trak)
    {
        while (trak.Next(out IsoBox mdia))
        {
            if (mdia.Type != Code("mdia"))
            {
                continue;
            }

            IsoBoxReader inMdia = trak.Children(mdia);
            while (inMdia.Next(out IsoBox minf))
            {
                if (minf.Type != Code("minf"))
                {
                    continue;
                }

                IsoBoxReader inMinf = inMdia.Children(minf);
                while (inMinf.Next(out IsoBox stbl))
                {
                    if (stbl.Type == Code("stbl"))
                    {
                        return Samples(inMinf.Children(stbl));
                    }
                }
            }
        }

        return null;
    }

    private static TrackInfo? Samples(IsoBoxReader stbl)
    {
        bool? jpeg = null, ctmd = null;
        CrxHeader? crx = null;
        int[]? areas = null;
        int width = 0, height = 0;
        long offset = -1, size = -1;
        while (stbl.Next(out IsoBox box))
        {
            ReadOnlySpan<byte> c = stbl.Content(box);
            switch (Name(box.Type))
            {
                case "stsd":
                    {
                        IsoBoxReader entries = stbl.Children(box, 8);
                        if (entries.Next(out IsoBox entry) && entry.Type == Code("CTMD"))
                        {
                            ctmd = true;
                        }
                        else if (entry.Type == Code("CRAW"))
                        {
                            // A visual sample entry (ISO/IEC 14496-12, 12.1.3): width and height after 24 bytes, children after 78.
                            ReadOnlySpan<byte> e = entries.Content(entry);
                            if (e.Length >= 82)
                            {
                                width = BinaryPrimitives.ReadUInt16BigEndian(e[24..]);
                                height = BinaryPrimitives.ReadUInt16BigEndian(e[26..]);
                                IsoBoxReader inner = entries.Children(entry, 82);
                                while (inner.Next(out IsoBox kind))
                                {
                                    if (kind.Type == Code("JPEG"))
                                    {
                                        jpeg = true;
                                    }
                                    else if (kind.Type == Code("CMP1"))
                                    {
                                        jpeg ??= false;
                                        crx = CrxHeader.Read(inner.Content(kind));
                                    }
                                    else if (kind.Type == Code("CDI1"))
                                    {
                                        areas = Areas(inner.Content(kind));
                                    }
                                }
                            }
                        }

                        break;
                    }

                case "stsz" when c.Length >= 12:
                    {
                        uint fixedSize = BinaryPrimitives.ReadUInt32BigEndian(c[4..]);
                        size = fixedSize != 0 ? fixedSize : c.Length >= 16 && BinaryPrimitives.ReadUInt32BigEndian(c[8..]) > 0 ? BinaryPrimitives.ReadUInt32BigEndian(c[12..]) : -1;
                        break;
                    }

                case "co64" when c.Length >= 16 && BinaryPrimitives.ReadUInt32BigEndian(c[4..]) > 0:
                    offset = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(c[8..]), long.MaxValue);
                    break;
                case "stco" when c.Length >= 12 && BinaryPrimitives.ReadUInt32BigEndian(c[4..]) > 0:
                    offset = BinaryPrimitives.ReadUInt32BigEndian(c[8..]);
                    break;
            }
        }

        if (ctmd == true)
        {
            return offset > 0 && size > 0 ? new TrackInfo(false, 0, 0, offset, size, null, null, Ctmd: true) : null;
        }

        return jpeg is bool isJpeg && width > 0 && height > 0 && offset > 0 && size > 0 && (isJpeg || crx is not null)
            ? new TrackInfo(isJpeg, width, height, offset, size, crx, areas)
            : null;
    }

    /// <summary>
    /// Reads CDI1's IAD1 box (CRX spec, section 2.2) of a full-size track: the crop, the left and top optical black and the
    /// active area, each as left, top, right and bottom; or <see langword="null"/> for the small track's shorter IAD1.
    /// </summary>
    private static int[]? Areas(ReadOnlySpan<byte> cdi1)
    {
        if (cdi1.Length < 4 + 0x38 || BinaryPrimitives.ReadUInt32BigEndian(cdi1[4..]) != 0x38 || !cdi1[8..12].SequenceEqual("IAD1"u8))
        {
            return null;
        }

        ReadOnlySpan<byte> c = cdi1.Slice(12, 48);
        int[] a = new int[16];
        for (int i = 0; i < 16; i++)
        {
            a[i] = BinaryPrimitives.ReadUInt16BigEndian(c[(16 + (2 * i))..]);
        }

        return a;
    }

    /// <summary>A CMT block: a TIFF whose offsets count from the block's contents.</summary>
    private static RawTiff? Tiff(RawSource source, IsoBox box)
    {
        ReadOnlySpan<byte> content = source.Data.AsSpan((int)box.ContentStart, (int)box.ContentLength);
        return RawTiff.TryHeader(content, out bool bigEndian, out ushort magic, out _) && magic == 42 ? new RawTiff(source, bigEndian, (int)box.ContentStart) : null;
    }

    /// <summary>
    /// A THMB or PRVW box: a small header, then the JPEG, found by its start-of-image marker within the first 32 bytes and
    /// running to the box's end. Its size is the JPEG's own.
    /// </summary>
    private static RawPreviewSource? Jpeg(RawSource source, IsoBox box)
    {
        if (!source.Ensure(box.ContentStart + 32) || box.ContentLength < 32)
        {
            return null;
        }

        int soi = source.Data.AsSpan((int)box.ContentStart, 32).IndexOf([(byte)0xFF, (byte)0xD8, (byte)0xFF]);
        return soi < 0 ? null : Sized(source, box.ContentStart + soi, box.End - box.ContentStart - soi, 0, 0);
    }

    /// <summary>A JPEG preview, its size read from its frame header (the first 64 KB are read for it), else the one given.</summary>
    private static RawPreviewSource? Sized(RawSource source, long offset, long length, int width, int height)
    {
        if (offset <= 0 || length <= 0 || offset + length > int.MaxValue)
        {
            return null;
        }

        source.Ensure(offset + Math.Min(length, 1 << 16));
        if (offset < source.Length)
        {
            (_, int w, int h) = RafParser.Jpeg(source, (int)offset, (int)Math.Min(length, source.Length - offset));
            (width, height) = w > 0 && h > 0 ? (w, h) : (width, height);
        }

        return width > 0 && height > 0 ? new RawPreviewSource(width, height, true, offset, length, null) : null;
    }

    private static void Need(RawSource source, long end)
    {
        if (!source.Ensure(end))
        {
            throw new InvalidDataException("The CR3's movie box runs past the end of the file.");
        }
    }

    private static FourCC Code(string text) => FourCC.Parse(text);

    private static string Name(FourCC code) => code.ToString();

    private static Guid Uuid(byte[] b) => new(b, bigEndian: true);
}
