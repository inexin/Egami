using System.Buffers.Binary;

using Egami.Formats.Iso;

namespace Egami.Formats.Extended.Raw;

/// <summary>
/// Reads Canon's CR3 container (ISO/IEC 14496-12, brand <c>crx </c>) for its previews and metadata: the Canon box (a uuid box in
/// moov) holds four TIFF blocks, CMT1 (IFD0), CMT2 (Exif), CMT3 (the maker note) and CMT4 (GPS), and the thumbnail THMB; a
/// top-level uuid box holds the preview PRVW; the first track is a full-size JPEG, and the tracks with a CMP1 entry hold the
/// CRX-coded sensor data, which Egami does not decode. Box layouts were read from the sample files themselves.
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
        RawTiff? cmt1 = null, cmt2 = null, cmt4 = null;
        byte[]? makerNote = null;
        RawPreviewSource? thumbnail = null, preview = null, fullJpeg = null;
        (int Width, int Height, long Offset, long Size)? raw = null;
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
                        if (track.Jpeg)
                        {
                            fullJpeg ??= Sized(source, track.Offset, track.Size, track.Width, track.Height);
                        }
                        else if (raw is null || (long)track.Width * track.Height > (long)raw.Value.Width * raw.Value.Height)
                        {
                            raw = (track.Width, track.Height, track.Offset, track.Size);
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

        RawFrame frame = new()
        {
            Sensor = new RawSensor
            {
                Ifd = ifd0,
                Width = raw.Value.Width,
                Height = raw.Value.Height,
                Bits = 14,
                Photometric = DngParser.PhotometricCfa,
                UnitWidth = raw.Value.Width,
                UnitHeight = raw.Value.Height,
                Offsets = [raw.Value.Offset],
                Counts = [raw.Value.Size],
                Codec = RawCodec.CanonCrx,
            },
        };
        frame.Cfa = MakerParser.Bayer("RGGB");
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

        frame.Active = new Rect(0, 0, raw.Value.Width, raw.Value.Height);
        frame.Crop = (0, 0, raw.Value.Width, raw.Value.Height);
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

    /// <summary>Reads one track: its first sample entry (a CRAW entry with a JPEG or a CMP1 box), size, and first sample's place.</summary>
    private static (bool Jpeg, int Width, int Height, long Offset, long Size)? Track(IsoBoxReader trak)
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

    private static (bool Jpeg, int Width, int Height, long Offset, long Size)? Samples(IsoBoxReader stbl)
    {
        bool? jpeg = null;
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
                        if (entries.Next(out IsoBox entry) && entry.Type == Code("CRAW"))
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

        return jpeg is bool isJpeg && width > 0 && height > 0 && offset > 0 && size > 0 ? (isJpeg, width, height, offset, size) : null;
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
