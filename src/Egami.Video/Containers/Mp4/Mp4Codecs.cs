using System.Buffers.Binary;

using Egami.Colors;
using Egami.Formats.Iso;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Video.Containers.Mp4;

/// <summary>
/// Maps MP4 and QuickTime sample descriptions to track info with Matroska codec ids (V4.1 Design Q2): AV1, VP9, VP8, Motion JPEG,
/// Opus, FLAC, PCM and mov_text with their settings, H.264, HEVC, AAC and MP3 with the setup bytes Matroska stores for them, and
/// any other entry passed through whole (<see cref="CodecIds.QuickTimeVideo"/> and its kin), so a remux keeps it byte for byte.
/// </summary>
internal static class Mp4Codecs
{
    /// <summary>The size of a visual sample entry's fields before its boxes.</summary>
    public const int VisualFields = 78;

    /// <summary>The size of an audio sample entry's version 0 fields before its boxes.</summary>
    public const int AudioFields = 28;

    /// <summary>Builds the track info of a track, from its first sample description.</summary>
    public static TrackInfo Build(Mp4Track track, int number, bool quickTime, MediaTime? duration)
    {
        Rational timeBase = new(1, Math.Max(1, track.Timescale));
        Mp4SampleEntry? entry = track.Entries.Count > 0 ? track.Entries[0] : null;
        TrackKind kind = KindOf(track.Handler);
        Dictionary<string, string> tags = [];
        if (track.HandlerName is { } handler)
        {
            tags["HANDLER_NAME"] = handler;
        }

        if (entry is null)
        {
            return new TrackInfo { Number = number, Kind = kind, CodecId = PassThroughId(kind), TimeBase = timeBase, Duration = duration, Language = track.Language, Name = track.Name, Tags = tags, IsDefault = track.Enabled };
        }

        return kind switch
        {
            TrackKind.Video => Video(track, entry, number, timeBase, duration, tags),
            TrackKind.Audio => Audio(track, entry, number, timeBase, duration, tags, quickTime),
            _ => Other(track, entry, kind, number, timeBase, duration, tags),
        };
    }

    /// <summary>Gets the pass-through codec id for a kind of track.</summary>
    public static string PassThroughId(TrackKind kind) => kind switch
    {
        TrackKind.Video => CodecIds.QuickTimeVideo,
        TrackKind.Audio => CodecIds.QuickTimeAudio,
        _ => CodecIds.QuickTimeOther,
    };

    private static TrackKind KindOf(FourCC handler) =>
        handler == Mp4Track.Vide ? TrackKind.Video
        : handler == Mp4Track.Soun ? TrackKind.Audio
        : handler == Mp4Track.Subt || handler == Mp4Track.Text || handler == Mp4Track.Sbtl ? TrackKind.Subtitle
        : TrackKind.Other;

    private static VideoTrackInfo Video(Mp4Track track, Mp4SampleEntry entry, int number, Rational timeBase, MediaTime? duration, Dictionary<string, string> tags)
    {
        byte[] box = entry.Box;
        int width = box.Length >= 8 + 28 ? BinaryPrimitives.ReadUInt16BigEndian(box.AsSpan(8 + 24)) : 0;
        int height = box.Length >= 8 + 28 ? BinaryPrimitives.ReadUInt16BigEndian(box.AsSpan(8 + 26)) : 0;
        string type = entry.Type.ToString();
        string codecId = CodecIds.QuickTimeVideo;
        ReadOnlyMemory<byte> codecPrivate = box;
        ColorCodePoints? color = null;
        HdrMetadata? hdr = null;
        Rational aspect = new(1, 1);
        FieldOrder fields = FieldOrder.Progressive;
        int bitDepth = 0;
        ChromaFormat? chroma = null;
        ColorCodePoints? configColor = null;

        foreach ((FourCC childType, ReadOnlyMemory<byte> content) in Children(box, VisualFields))
        {
            ReadOnlySpan<byte> c = content.Span;
            switch (childType.ToString())
            {
                case "av1C" when type == "av01":
                    codecId = CodecIds.Av1;
                    codecPrivate = content.ToArray();
                    if (c.Length >= 3)
                    {
                        bool high = (c[2] & 0x40) != 0, twelve = (c[2] & 0x20) != 0;
                        bitDepth = high ? (twelve ? 12 : 10) : 8;
                        bool mono = (c[2] & 0x10) != 0, x = (c[2] & 0x08) != 0, y = (c[2] & 0x04) != 0;
                        chroma = mono ? ChromaFormat.Yuv400 : x && y ? ChromaFormat.Yuv420 : x ? ChromaFormat.Yuv422 : ChromaFormat.Yuv444;
                    }

                    break;
                case "vpcC" when type is "vp09" or "vp08":
                    codecId = type == "vp09" ? CodecIds.Vp9 : CodecIds.Vp8;
                    codecPrivate = type == "vp09" ? VpcToMatroska(c) : ReadOnlyMemory<byte>.Empty;
                    if (c.Length >= 12)
                    {
                        bitDepth = c[6] >> 4;
                        chroma = ((c[6] >> 1) & 7) switch { 0 or 1 => ChromaFormat.Yuv420, 2 => ChromaFormat.Yuv422, 3 => ChromaFormat.Yuv444, _ => null };
                        configColor = new ColorCodePoints(c[7], c[8], c[9], (c[6] & 1) != 0);
                    }

                    break;
                case "avcC" when type is "avc1" or "avc3":
                    codecId = CodecIds.Avc;
                    codecPrivate = content.ToArray();
                    break;
                case "hvcC" when type is "hvc1" or "hev1":
                    codecId = CodecIds.Hevc;
                    codecPrivate = content.ToArray();
                    break;
                case "esds" when type == "mp4v":
                    (int objectType, ReadOnlyMemory<byte> specific) = ReadEsds(c);
                    if (objectType == 0x6C)
                    {
                        codecId = CodecIds.MotionJpeg;
                        codecPrivate = ReadOnlyMemory<byte>.Empty;
                    }
                    else if (objectType == 0x20)
                    {
                        codecId = CodecIds.Mpeg4Visual;
                        codecPrivate = specific;
                    }

                    break;
                case "colr" when c.Length >= 10 && (c[..4].SequenceEqual("nclx"u8) || c[..4].SequenceEqual("nclc"u8)):
                    bool full = c[..4].SequenceEqual("nclx"u8) && c.Length >= 11 && (c[10] & 0x80) != 0;
                    color = new ColorCodePoints((byte)Math.Min((int)BinaryPrimitives.ReadUInt16BigEndian(c[4..]), 255),
                        (byte)Math.Min((int)BinaryPrimitives.ReadUInt16BigEndian(c[6..]), 255), (byte)Math.Min((int)BinaryPrimitives.ReadUInt16BigEndian(c[8..]), 255), full);
                    break;
                case "pasp" when c.Length >= 8:
                    uint h = BinaryPrimitives.ReadUInt32BigEndian(c), v = BinaryPrimitives.ReadUInt32BigEndian(c[4..]);
                    aspect = h > 0 && v > 0 ? new Rational(h, v) : aspect;
                    break;
                case "fiel" when c.Length >= 2 && c[0] == 2:
                    fields = c[1] is 1 or 9 ? FieldOrder.TopFieldFirst : c[1] is 6 or 14 ? FieldOrder.BottomFieldFirst : fields;
                    break;
                case "mdcv" when c.Length >= 24:
                    hdr = Mastering(c, hdr);
                    break;
                case "SmDm" when c.Length >= 28:
                    hdr = SmDm(c[4..], hdr);
                    break;
                case "clli" when c.Length >= 4:
                    hdr = (hdr ?? new HdrMetadata()) with { MaxContentLightLevel = BinaryPrimitives.ReadUInt16BigEndian(c), MaxFrameAverageLightLevel = BinaryPrimitives.ReadUInt16BigEndian(c[2..]) };
                    break;
                case "CoLL" when c.Length >= 8:
                    hdr = (hdr ?? new HdrMetadata()) with { MaxContentLightLevel = BinaryPrimitives.ReadUInt16BigEndian(c[4..]), MaxFrameAverageLightLevel = BinaryPrimitives.ReadUInt16BigEndian(c[6..]) };
                    break;
            }
        }

        if (type is "jpeg" or "mjpa" or "dmb1" or "AVDJ")
        {
            codecId = CodecIds.MotionJpeg;
            codecPrivate = ReadOnlyMemory<byte>.Empty;
        }
        else if (type == "vp08" && codecId == CodecIds.QuickTimeVideo)
        {
            codecId = CodecIds.Vp8;
            codecPrivate = ReadOnlyMemory<byte>.Empty;
        }

        color ??= configColor is { } cc && !(cc.Primaries == 2 && cc.TransferCharacteristics == 2 && cc.MatrixCoefficients == 2 && !cc.FullRange) ? cc : null;
        return new VideoTrackInfo
        {
            Number = number,
            Kind = TrackKind.Video,
            CodecId = codecId,
            CodecPrivate = codecPrivate,
            TimeBase = timeBase,
            Duration = duration,
            Language = track.Language,
            Name = track.Name,
            Tags = tags,
            IsDefault = track.Enabled,
            Width = width,
            Height = height,
            FrameRate = FrameRate(track),
            PixelAspectRatio = aspect,
            Color = color,
            FieldOrder = fields,
            BitDepth = bitDepth,
            ChromaFormat = chroma,
            Hdr = hdr,
            Orientation = OrientationOf(track.Matrix),
        };
    }

    private static AudioTrackInfo Audio(Mp4Track track, Mp4SampleEntry entry, int number, Rational timeBase, MediaTime? duration, Dictionary<string, string> tags, bool quickTime)
    {
        byte[] box = entry.Box;
        string type = entry.Type.ToString();
        BoxFields f = new(box.AsSpan(8), new IsoBox(entry.Type, 0, 8, box.Length));
        f.Skip(8);
        int version = f.U16();
        f.Skip(6);
        int channels = f.U16();
        int bits = f.U16();
        f.Skip(4);
        int rate = (int)(f.U32() >> 16);
        int fields = AudioFields;
        bool floatFlag = false, bigEndian = type == "twos", signed = true;
        if (quickTime && version == 1)
        {
            fields += 16;
        }
        else if (quickTime && version == 2 && box.Length >= 8 + AudioFields + 36)
        {
            // Version 2: the real rate and channels, and LPCM flags (kAudioFormatFlagIsFloat 1, IsBigEndian 2, IsSignedInteger 4).
            f.Skip(4);
            rate = (int)Math.Clamp(BitConverter.Int64BitsToDouble((long)f.U64()), 0, int.MaxValue);
            channels = (int)Math.Min(f.U32(), int.MaxValue);
            f.Skip(4);
            bits = (int)Math.Min(f.U32(), 64);
            uint flags = f.U32();
            floatFlag = (flags & 1) != 0;
            bigEndian = (flags & 2) != 0;
            signed = (flags & 4) != 0;
            fields += 36;
        }

        string codecId = CodecIds.QuickTimeAudio;
        ReadOnlyMemory<byte> codecPrivate = box;
        MediaTime? delay = null;
        foreach ((FourCC childType, ReadOnlyMemory<byte> content) in Children(box, fields))
        {
            ReadOnlySpan<byte> c = content.Span;
            switch (childType.ToString())
            {
                case "dOps" when type == "Opus" && c.Length >= 11:
                    codecId = CodecIds.Opus;
                    codecPrivate = OpusHead(c);
                    channels = c[1];
                    int preSkip = BinaryPrimitives.ReadUInt16BigEndian(c[2..]);
                    delay = new MediaTime(preSkip, new Rational(1, 48000));
                    rate = 48000;
                    break;
                case "dfLa" when type == "fLaC" && c.Length >= 4:
                    codecId = CodecIds.Flac;
                    codecPrivate = (byte[])[.. "fLaC"u8, .. c[4..]];
                    if (c.Length >= 4 + 4 + 18)
                    {
                        // STREAMINFO: 20 bits of sample rate, 3 of channels minus one, 5 of bits minus one.
                        ReadOnlySpan<byte> info = c[8..];
                        rate = info[10] << 12 | info[11] << 4 | info[12] >> 4;
                        channels = ((info[12] >> 1) & 7) + 1;
                        bits = (((info[12] & 1) << 4) | (info[13] >> 4)) + 1;
                    }

                    break;
                case "esds" when type == "mp4a":
                    (int objectType, ReadOnlyMemory<byte> specific) = ReadEsds(c);
                    (codecId, codecPrivate) = objectType switch
                    {
                        0x40 or 0x66 or 0x67 or 0x68 => (CodecIds.Aac, specific),
                        0x69 or 0x6B => (CodecIds.Mp3, ReadOnlyMemory<byte>.Empty),
                        0xDD => (CodecIds.Vorbis, specific),
                        _ => (codecId, codecPrivate),
                    };
                    break;
                case "pcmC" when type is "ipcm" or "fpcm" && c.Length >= 6:
                    bigEndian = (c[4] & 1) == 0;
                    bits = c[5];
                    break;
                case "enda" when c.Length >= 2:
                    bigEndian = BinaryPrimitives.ReadUInt16BigEndian(c) == 0;
                    break;
                case "srat" when c.Length >= 8:
                    rate = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(c[4..]), int.MaxValue);
                    break;
            }
        }

        switch (type)
        {
            case "sowt":
            case "twos" or "raw " or "in24" or "in32" or "ipcm" or "NONE":
                codecId = bigEndian && bits > 8 ? CodecIds.PcmBigEndian : CodecIds.PcmLittleEndian;
                bits = type switch { "in24" => 24, "in32" => 32, "raw " => 8, _ => bits };
                codecPrivate = ReadOnlyMemory<byte>.Empty;
                break;
            case "lpcm":
                codecId = floatFlag ? CodecIds.PcmFloat : bigEndian && bits > 8 ? CodecIds.PcmBigEndian : CodecIds.PcmLittleEndian;
                codecPrivate = ReadOnlyMemory<byte>.Empty;
                _ = signed;
                break;
            case "ulaw" or "alaw":
                codecId = type == "ulaw" ? CodecIds.G711MuLaw : CodecIds.G711ALaw;
                bits = 8;
                codecPrivate = ReadOnlyMemory<byte>.Empty;
                break;
            case "fl32" or "fl64" or "fpcm":
                codecId = bigEndian && type != "fpcm" ? codecId : CodecIds.PcmFloat;
                bits = type switch { "fl32" => 32, "fl64" => 64, _ => bits };
                codecPrivate = codecId == CodecIds.PcmFloat ? ReadOnlyMemory<byte>.Empty : codecPrivate;
                break;
        }

        bool compressed = codecId is CodecIds.Opus or CodecIds.Aac or CodecIds.Mp3 or CodecIds.Vorbis or CodecIds.QuickTimeAudio;
        return new AudioTrackInfo
        {
            Number = number,
            Kind = TrackKind.Audio,
            CodecId = codecId,
            CodecPrivate = codecPrivate,
            TimeBase = timeBase,
            Duration = duration,
            Language = track.Language,
            Name = track.Name,
            Tags = tags,
            IsDefault = track.Enabled,
            CodecDelay = delay,
            SeekPreRoll = codecId == CodecIds.Opus ? new MediaTime(80, new Rational(1, 1000)) : null,
            SampleRate = rate,
            Channels = channels,
            BitsPerSample = compressed ? 0 : bits,
        };
    }

    private static TrackInfo Other(Mp4Track track, Mp4SampleEntry entry, TrackKind kind, int number, Rational timeBase, MediaTime? duration, Dictionary<string, string> tags)
    {
        string type = entry.Type.ToString();
        bool text = type is "tx3g" or "wvtt" && kind == TrackKind.Subtitle;
        return new TrackInfo
        {
            Number = number,
            Kind = kind,
            CodecId = !text ? PassThroughId(kind) : type == "wvtt" ? CodecIds.Mp4WebVtt : CodecIds.MovText,
            CodecPrivate = text ? entry.Box.AsMemory(8) : entry.Box,
            TimeBase = timeBase,
            Duration = duration,
            Language = track.Language,
            Name = track.Name,
            Tags = tags,
            IsDefault = track.Enabled,
        };
    }

    /// <summary>Gets the nominal frame rate when every sample has the same duration.</summary>
    private static Rational? FrameRate(Mp4Track track)
    {
        (uint Count, uint Delta)[] times = track.Table.Times;
        if (times.Length == 0 || track.Timescale == 0)
        {
            return null;
        }

        // The last sample's duration is often a guess; it does not decide.
        uint delta = times[0].Delta;
        for (int i = 1; i < times.Length; i++)
        {
            if (times[i].Delta != delta && !(i == times.Length - 1 && times[i].Count == 1))
            {
                return null;
            }
        }

        return delta == 0 ? null : new Rational(track.Timescale, delta);
    }

    /// <summary>
    /// Maps a tkhd matrix to the EXIF orientation that shows the coded picture upright: a rotation by a multiple of 90 degrees,
    /// mirrored or not. Any other matrix (scaling aside) is reported as <see cref="ImageOrientation.Normal"/>.
    /// </summary>
    internal static ImageOrientation OrientationOf(int[] m)
    {
        int a = Math.Sign(m[0]), b = Math.Sign(m[1]), c = Math.Sign(m[3]), d = Math.Sign(m[4]);
        return (a, b, c, d) switch
        {
            (1, 0, 0, 1) => ImageOrientation.Normal,
            (-1, 0, 0, 1) => ImageOrientation.FlipHorizontal,
            (-1, 0, 0, -1) => ImageOrientation.Rotate180,
            (1, 0, 0, -1) => ImageOrientation.FlipVertical,
            (0, 1, 1, 0) => ImageOrientation.Transpose,
            (0, 1, -1, 0) => ImageOrientation.Rotate90,
            (0, -1, -1, 0) => ImageOrientation.Transverse,
            (0, -1, 1, 0) => ImageOrientation.Rotate270,
            _ => ImageOrientation.Normal,
        };
    }

    /// <summary>Lists the boxes after a sample entry's fixed fields, stopping quietly at bytes that are not boxes (QuickTime pads some entries).</summary>
    internal static List<(FourCC Type, ReadOnlyMemory<byte> Content)> Children(byte[] entry, int fields)
    {
        List<(FourCC, ReadOnlyMemory<byte>)> boxes = [];
        int start = 8 + fields;
        if (start > entry.Length)
        {
            return boxes;
        }

        try
        {
            IsoBoxReader reader = new(entry.AsSpan(start), "MP4", start);
            while (reader.Next(out IsoBox box))
            {
                boxes.Add((box.Type, entry.AsMemory((int)box.ContentStart, (int)box.ContentLength)));
            }
        }
        catch (CorruptDataException)
        {
            // A trailing 4-byte zero terminator or padding: keep what was read.
        }

        return boxes;
    }

    /// <summary>Reads an esds box (ISO/IEC 14496-1 descriptors): the object type and the decoder specific info.</summary>
    internal static (int ObjectType, ReadOnlyMemory<byte> Specific) ReadEsds(ReadOnlySpan<byte> esds)
    {
        int at = 4; // version and flags
        int objectType = -1;
        byte[] specific = [];
        while (at < esds.Length)
        {
            int tag = esds[at++];
            int length = 0;
            for (int i = 0; i < 4 && at < esds.Length; i++)
            {
                byte b = esds[at++];
                length = (length << 7) | (b & 0x7F);
                if ((b & 0x80) == 0)
                {
                    break;
                }
            }

            int end = Math.Min(esds.Length, at + length);
            switch (tag)
            {
                case 3: // ES_Descriptor: ES_ID, flags, then optional fields, then nested descriptors
                    if (at + 3 > esds.Length)
                    {
                        return (objectType, specific);
                    }

                    int flags = esds[at + 2];
                    at += 3;
                    at += (flags & 0x80) != 0 ? 2 : 0;
                    if ((flags & 0x40) != 0 && at < esds.Length)
                    {
                        at += 1 + esds[at];
                    }

                    at += (flags & 0x20) != 0 ? 2 : 0;
                    continue;
                case 4: // DecoderConfigDescriptor: object type, then 12 bytes, then nested descriptors
                    if (at < esds.Length)
                    {
                        objectType = esds[at];
                    }

                    at += 13;
                    continue;
                case 5:
                    specific = esds[at..end].ToArray();
                    break;
            }

            at = end;
        }

        return (objectType, specific);
    }

    /// <summary>Builds Matroska's VP9 CodecPrivate (profile, level, bit depth and chroma subsampling features) from a vpcC box.</summary>
    internal static byte[] VpcToMatroska(ReadOnlySpan<byte> vpcc)
    {
        if (vpcc.Length < 7)
        {
            return [];
        }

        int chroma = (vpcc[6] >> 1) & 7;
        return [1, 1, vpcc[4], 2, 1, vpcc[5], 3, 1, (byte)(vpcc[6] >> 4), 4, 1, (byte)chroma];
    }

    /// <summary>Builds Matroska's OpusHead (RFC 7845, little-endian) from an Opus-in-ISOBMFF dOps box (big-endian).</summary>
    internal static byte[] OpusHead(ReadOnlySpan<byte> dops)
    {
        byte[] head = new byte[19 + (dops.Length > 11 && dops[10] != 0 ? dops.Length - 11 : 0)];
        "OpusHead"u8.CopyTo(head);
        head[8] = 1;
        head[9] = dops[1];
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), BinaryPrimitives.ReadUInt16BigEndian(dops[2..]));
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), BinaryPrimitives.ReadUInt32BigEndian(dops[4..]));
        BinaryPrimitives.WriteInt16LittleEndian(head.AsSpan(16), BinaryPrimitives.ReadInt16BigEndian(dops[8..]));
        head[18] = dops[10];
        if (head.Length > 19)
        {
            dops[11..].CopyTo(head.AsSpan(19));
        }

        return head;
    }

    private static HdrMetadata Mastering(ReadOnlySpan<byte> c, HdrMetadata? hdr)
    {
        // Display primaries G, B, R then the white point, x and y in 0.00002 units; luminance in 0.0001 cd/m2.
        static Chromaticity At(ReadOnlySpan<byte> s, int i) => new(BinaryPrimitives.ReadUInt16BigEndian(s[(4 * i)..]) * 0.00002, BinaryPrimitives.ReadUInt16BigEndian(s[((4 * i) + 2)..]) * 0.00002);
        return (hdr ?? new HdrMetadata()) with
        {
            MasteringPrimaries = new ColorPrimaries(At(c, 2), At(c, 0), At(c, 1), At(c, 3)),
            MasteringMaxLuminance = BinaryPrimitives.ReadUInt32BigEndian(c[16..]) * 0.0001,
            MasteringMinLuminance = BinaryPrimitives.ReadUInt32BigEndian(c[20..]) * 0.0001,
        };
    }

    private static HdrMetadata SmDm(ReadOnlySpan<byte> c, HdrMetadata? hdr)
    {
        // VP codec ISO binding: R, G, B and white x and y in 0.16 fixed point; luminance in 24.8 and 18.14.
        static Chromaticity At(ReadOnlySpan<byte> s, int i) => new(BinaryPrimitives.ReadUInt16BigEndian(s[(4 * i)..]) / 65536.0, BinaryPrimitives.ReadUInt16BigEndian(s[((4 * i) + 2)..]) / 65536.0);
        return (hdr ?? new HdrMetadata()) with
        {
            MasteringPrimaries = new ColorPrimaries(At(c, 0), At(c, 1), At(c, 2), At(c, 3)),
            MasteringMaxLuminance = BinaryPrimitives.ReadUInt32BigEndian(c[16..]) / 256.0,
            MasteringMinLuminance = BinaryPrimitives.ReadUInt32BigEndian(c[20..]) / 16384.0,
        };
    }
}
