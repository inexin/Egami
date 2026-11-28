using System.Buffers.Binary;
using System.Text;

using Egami.Metadata;
using Egami.Pixels;
using Egami.Video.Audio;
using Egami.Video.Audio.Codecs.Alac;

namespace Egami.Video.Containers.Mp4;

/// <summary>
/// The boxes a QuickTime movie writes in place of ISO MP4's (Apple's QuickTime File Format): the qt ftyp, mhlr and dhlr handlers
/// with counted names, gmhd for tracks that are neither video nor sound, alis data references, and QuickTime's video and sound
/// sample descriptions: jpeg, avc1, hvc1, 2vuy, v210 and raw with colr (nclc), fiel, pasp and clap, and sound descriptions of
/// version 0, 1 or 2 with a wave atom where QuickTime keeps a codec's setup, and a chan layout.
/// </summary>
internal static class MovSampleEntries
{
    /// <summary>Returns why a QuickTime movie cannot hold a track, or <see langword="null"/> when it can.</summary>
    public static string? Unsupported(TrackInfo track)
    {
        switch (track.CodecId)
        {
            case CodecIds.Avc or CodecIds.Hevc or CodecIds.MotionJpeg or CodecIds.Aac or CodecIds.Mp3 or CodecIds.MovText:
                return null;
            case CodecIds.Alac:
                return AlacSpecificConfig.Extract(track.CodecPrivate.Span).Length == 0 ? "an ALAC track needs its ALACSpecificConfig in CodecPrivate" : null;
            case CodecIds.PcmLittleEndian or CodecIds.PcmBigEndian:
                return track is AudioTrackInfo { BitsPerSample: 0 or 8 or 16 or 24 or 32 } ? null : "QuickTime holds integer PCM of 8, 16, 24 or 32 bits";
            case CodecIds.PcmFloat:
                return track is AudioTrackInfo { BitsPerSample: 0 or 32 or 64 } ? null : "QuickTime holds float PCM of 32 or 64 bits";
            case CodecIds.Uncompressed or CodecIds.VfwFourCC:
                return track is VideoTrackInfo video && MovRawVideo.For(video) is not null ? null
                    : "QuickTime holds uncompressed video as 2vuy (8-bit 4:2:2), v210 (10-bit 4:2:2) or raw RGB; write other layouts to Matroska (.mkv)";
            case CodecIds.QuickTimeVideo or CodecIds.QuickTimeAudio or CodecIds.QuickTimeOther:
                return track.CodecPrivate.Length >= 8 ? null : "a pass-through track needs its sample description in CodecPrivate";
            default:
                string instead = Mp4SampleEntries.Unsupported(track) is null ? "MP4 (.mp4) or Matroska (.mkv)" : "Matroska (.mkv)";
                return $"QuickTime (MOV) cannot hold codec '{track.CodecId}'; write it to {instead}";
        }
    }

    /// <summary>Writes the qt ftyp: QuickTime's major brand and the date-style minor version, with qt compatible.</summary>
    public static byte[] Ftyp()
    {
        BoxWriter w = new();
        w.Start("ftyp");
        w.Type("qt  ");
        w.U32(0x200);
        w.Type("qt  ");
        w.End();
        return w.ToArray();
    }

    /// <summary>Gets the component name of a media handler, by its subtype.</summary>
    public static string HandlerName(string handler) => handler switch
    {
        "vide" => "Video Media Handler",
        "soun" => "Sound Media Handler",
        "sbtl" => "Subtitle Media Handler",
        "text" => "Text Media Handler",
        "tmcd" => "Time Code Media Handler",
        _ => "Data Media Handler",
    };

    /// <summary>Writes a track's media handler (mhlr), named by its HANDLER_NAME tag or after its type; returns the handler type.</summary>
    public static string TrackHandler(BoxWriter w, TrackInfo track)
    {
        string handler = Mp4SampleEntries.HandlerOf(track);
        Hdlr(w, "mhlr", handler, track.Tags.GetValueOrDefault("HANDLER_NAME") ?? HandlerName(handler));
        return handler;
    }

    /// <summary>Writes a QuickTime handler reference: component type and subtype, then the name as a counted string.</summary>
    public static void Hdlr(BoxWriter w, string componentType, string subtype, string name)
    {
        w.StartFull("hdlr", 0, 0);
        w.Type(componentType);
        w.Type(subtype);
        w.Zeros(12); // manufacturer, flags and flags mask
        Pascal(w, name, 255);
        w.End();
    }

    /// <summary>
    /// Writes minf's media header, then the data handler (dhlr) and a data reference to this file (alis, self-contained), in
    /// QuickTime's order. Tracks that are neither video nor sound get a base media header (gmhd), with QuickTime's text box for text.
    /// </summary>
    public static void MediaHeaderAndData(BoxWriter w, string handler)
    {
        switch (handler)
        {
            case "vide":
                w.StartFull("vmhd", 0, 1);
                w.U16(0x40); // graphics mode: dither copy
                w.U16(0x8000);
                w.U16(0x8000);
                w.U16(0x8000);
                w.End();
                break;
            case "soun":
                w.StartFull("smhd", 0, 0);
                w.Zeros(4);
                w.End();
                break;
            default:
                w.Start("gmhd");
                w.StartFull("gmin", 0, 0);
                w.U16(0x40);
                w.U16(0x8000);
                w.U16(0x8000);
                w.U16(0x8000);
                w.U16(0); // balance
                w.U16(0);
                w.End();
                if (handler == "text")
                {
                    w.Start("text");
                    Mp4Muxer.Matrix(w, ImageOrientation.Normal, 0, 0);
                    w.End();
                }

                w.End();
                break;
        }

        Hdlr(w, "dhlr", "alis", "Alias Data Handler");
        w.Start("dinf");
        w.StartFull("dref", 0, 0);
        w.U32(1);
        w.StartFull("alis", 0, 1); // the media is in this file
        w.End();
        w.End();
        w.End();
    }

    /// <summary>Writes the track's one sample description, inside stsd.</summary>
    public static void Write(BoxWriter w, TrackInfo track)
    {
        switch (track.CodecId)
        {
            case CodecIds.QuickTimeVideo or CodecIds.QuickTimeAudio or CodecIds.QuickTimeOther or CodecIds.MovText:
                Mp4SampleEntries.Write(w, track);
                return;
        }

        if (track is VideoTrackInfo video)
        {
            if (MovRawVideo.SourceEntry(video) is { Length: > 0 } entry)
            {
                // An uncompressed track read from QuickTime in the layout it is written in: its own entry, byte for byte.
                w.Bytes(entry);
                return;
            }

            WriteVideo(w, video);
        }
        else
        {
            WriteAudio(w, (AudioTrackInfo)track);
        }
    }

    private static void WriteVideo(BoxWriter w, VideoTrackInfo video)
    {
        MovRawVideo? raw = MovRawVideo.For(video);
        (string type, string compressor) = video.CodecId switch
        {
            CodecIds.Avc => ("avc1", "H.264"),
            CodecIds.Hevc => ("hvc1", "HEVC"),
            CodecIds.MotionJpeg => MotionJpegType(video),
            _ => raw!.Type switch
            {
                "2vuy" => ("2vuy", "Component Y'CbCr 8-bit 4:2:2"),
                "v210" => ("v210", "Component Y'CbCr 10-bit 4:2:2"),
                _ => ("raw ", "None"),
            },
        };
        w.Start(type);
        w.Zeros(6);
        w.U16(1); // data reference index
        w.U16(0); // version
        w.U16(0); // revision level
        w.U32(0); // vendor
        w.U32(raw is null ? 0x200u : 0); // temporal quality: normal
        w.U32(raw is null ? 0x200u : 0x400u); // spatial quality: normal, or lossless
        w.U16(video.Width);
        w.U16(video.Height);
        w.U32(0x00480000); // 72 dpi
        w.U32(0x00480000);
        w.U32(0); // data size
        w.U16(1); // frames per sample
        byte[] name = Encoding.ASCII.GetBytes(compressor);
        w.U8(name.Length);
        w.Bytes(name);
        w.Zeros(31 - name.Length);
        w.U16(24); // depth
        w.U16(0xFFFF); // no color table
        switch (video.CodecId)
        {
            case CodecIds.Avc:
                w.Start("avcC");
                w.Bytes(video.CodecPrivate.Span);
                w.End();
                break;
            case CodecIds.Hevc:
                w.Start("hvcC");
                w.Bytes(video.CodecPrivate.Span);
                w.End();
                break;
        }

        if (video.Color is { } color && raw?.Type != "raw ")
        {
            // QuickTime's nclc has no range flag; a full-range track takes ISO's nclx, which QuickTime players read too.
            w.Start("colr");
            w.Type(color.FullRange ? "nclx" : "nclc");
            w.U16(color.Primaries);
            w.U16(color.TransferCharacteristics);
            w.U16(color.MatrixCoefficients);
            if (color.FullRange)
            {
                w.U8(0x80);
            }

            w.End();
        }

        if (raw is not null || video.CodecId == CodecIds.MotionJpeg || video.FieldOrder != FieldOrder.Progressive)
        {
            // fiel: one or two fields, and for two which is shown first and stored first (1 top, 6 bottom).
            w.Start("fiel");
            w.U8(video.FieldOrder == FieldOrder.Progressive ? 1 : 2);
            w.U8(video.FieldOrder switch { FieldOrder.TopFieldFirst => 1, FieldOrder.BottomFieldFirst => 6, _ => 0 });
            w.End();
        }

        Rational aspect = video.PixelAspectRatio.Numerator > 0 && video.PixelAspectRatio.Denominator > 0 ? video.PixelAspectRatio : new Rational(1, 1);
        w.Start("pasp");
        w.U32((uint)Math.Min(aspect.Numerator, uint.MaxValue));
        w.U32((uint)Math.Min(aspect.Denominator, uint.MaxValue));
        w.End();
        if (raw is not null && raw.Type != "raw ")
        {
            // clap: the whole picture is clean, centred.
            w.Start("clap");
            w.U32((uint)video.Width);
            w.U32(1);
            w.U32((uint)video.Height);
            w.U32(1);
            w.I32(0);
            w.U32(1);
            w.I32(0);
            w.U32(1);
            w.End();
        }

        Mp4SampleEntries.HdrBoxes(w, video);
        w.End();
    }

    /// <summary>
    /// Gets the type and compressor name of a Motion JPEG entry: a track read from QuickTime keeps its own (mjpa, dmb1, AVDJ),
    /// anything else is Photo JPEG.
    /// </summary>
    private static (string Type, string Compressor) MotionJpegType(VideoTrackInfo video)
    {
        ReadOnlySpan<byte> entry = video.QuickTimeEntry.Span;
        string type = entry.Length >= 8 ? Encoding.Latin1.GetString(entry[4..8]) : "";
        if (type is not ("mjpa" or "dmb1" or "AVDJ"))
        {
            return ("jpeg", "Photo - JPEG");
        }

        // The compressor name: a Pascal string of up to 31 characters at byte 42 of the fields.
        string name = entry.Length >= 8 + Mp4Codecs.VisualFields ? Encoding.Latin1.GetString(entry.Slice(8 + 43, Math.Min((int)entry[8 + 42], 31))) : "";
        return (type, name);
    }

    private static void WriteAudio(BoxWriter w, AudioTrackInfo audio)
    {
        ReadOnlySpan<byte> priv = audio.CodecPrivate.Span;
        int channels = Math.Max(1, audio.Channels);
        int rate = audio.SampleRate;
        bool pcm = audio.CodecId is CodecIds.PcmLittleEndian or CodecIds.PcmBigEndian or CodecIds.PcmFloat;
        bool floating = audio.CodecId == CodecIds.PcmFloat;
        bool little = audio.CodecId is CodecIds.PcmLittleEndian or CodecIds.PcmFloat;
        int bits = audio.BitsPerSample > 0 ? audio.BitsPerSample : floating ? 32 : 16;
        int bytes = bits / 8;

        // Version 2 for rates above what 16.16 holds; else version 1 where QuickTime wants the packet sizes or a wave atom.
        string type = audio.CodecId switch
        {
            CodecIds.Aac or CodecIds.Mp3 => "mp4a",
            CodecIds.Alac => "alac",
            _ when rate > ushort.MaxValue => "lpcm",
            _ when floating => bits == 64 ? "fl64" : "fl32",
            _ => bits switch { 8 => "raw ", 24 => "in24", 32 => "in32", _ => little ? "sowt" : "twos" },
        };
        int version = rate > ushort.MaxValue ? 2 : type is "raw " or "sowt" or "twos" ? 0 : 1;
        int framesPerPacket = audio.CodecId switch
        {
            CodecIds.Aac => 1024,
            CodecIds.Mp3 => 1152,
            CodecIds.Alac => (int)Math.Min(AlacSpecificConfig.Read(priv)?.FrameLength ?? 4096, int.MaxValue),
            _ => 1,
        };

        w.Start(type);
        w.Zeros(6);
        w.U16(1); // data reference index
        w.U16(version);
        w.U16(0); // revision level
        w.U32(0); // vendor
        if (version == 2)
        {
            w.U16(3);
            w.U16(16);
            w.U16(0xFFFE);
            w.U16(0);
            w.U32(0x10000);
            w.U32(72); // size of the version 2 fields
            w.U64((ulong)BitConverter.DoubleToInt64Bits(rate));
            w.U32((uint)channels);
            w.U32(0x7F000000);
            w.U32(pcm ? (uint)bits : 0);

            // kAudioFormatFlagIsFloat 1, IsBigEndian 2, IsSignedInteger 4, IsPacked 8.
            w.U32(!pcm ? 0u : floating ? 1u | 8 : (little ? 0u : 2u) | (bits > 8 ? 4u : 0) | 8);
            w.U32(pcm ? (uint)(bytes * channels) : 0);
            w.U32((uint)framesPerPacket);
        }
        else
        {
            w.U16(channels);
            w.U16(type == "raw " ? 8 : 16);
            w.U16(pcm ? 0 : 0xFFFE); // compression id: -2 for a compressed format
            w.U16(0); // packet size
            w.U32((uint)rate << 16);
            if (version == 1)
            {
                w.U32((uint)framesPerPacket);
                w.U32(pcm ? (uint)bytes : 0); // bytes per packet: per channel for PCM
                w.U32(pcm ? (uint)(bytes * channels) : 0); // bytes per frame
                w.U32(bits == 8 ? 1u : 2u); // bytes per sample, as QuickTime defines it: 2 for anything wider than 8 bits
            }
        }

        if (type is "mp4a" or "alac" or "in24" or "in32" or "fl32" or "fl64")
        {
            // wave: the format again (frma), the codec's setup or the byte order, and a terminator.
            w.Start("wave");
            w.Start("frma");
            w.Type(type);
            w.End();
            switch (audio.CodecId)
            {
                case CodecIds.Aac or CodecIds.Mp3:
                    w.Start("mp4a");
                    w.U32(0);
                    w.End();
                    Mp4SampleEntries.Esds(w, audio.CodecId == CodecIds.Aac ? 0x40 : 0x6B, 0x05, audio.CodecId == CodecIds.Aac ? priv : []);
                    break;
                case CodecIds.Alac:
                    w.StartFull("alac", 0, 0);
                    w.Bytes(AlacSpecificConfig.Extract(priv));
                    w.End();
                    break;
                default:
                    w.Start("enda");
                    w.U16(little ? 1 : 0);
                    w.End();
                    break;
            }

            w.U32(8); // terminator atom
            w.U32(0);
            w.End();
        }

        if (ChannelLayoutBody(audio) is { } layout)
        {
            w.StartFull("chan", 0, 0);
            w.Bytes(layout);
            w.End();
        }

        w.End();
    }

    /// <summary>The AudioChannelLayout for chan: the track's positioned layout as a bitmap, else mono's or stereo's tag, else none.</summary>
    private static byte[]? ChannelLayoutBody(AudioTrackInfo audio)
    {
        if (audio.Layout is { } layout && CoreAudioLayout.Write(layout) is { } body)
        {
            return body;
        }

        if (audio.Channels is not (1 or 2))
        {
            return null;
        }

        byte[] tag = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(tag, audio.Channels == 1 ? (100u << 16) | 1 : (101u << 16) | 2);
        return tag;
    }

    /// <summary>Writes a counted (Pascal) string of at most <paramref name="max"/> bytes, cut at a whole UTF-8 character.</summary>
    private static void Pascal(BoxWriter w, string text, int max)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        int length = Math.Min(bytes.Length, max);
        while (length > 0 && length < bytes.Length && (bytes[length] & 0xC0) == 0x80)
        {
            length--;
        }

        w.U8(length);
        w.Bytes(bytes.AsSpan(0, length));
    }
}

/// <summary>
/// How a QuickTime movie stores an uncompressed track, and the repacking from Egami.Video's layouts: 2vuy (Cb Y0 Cr Y1, 8-bit
/// 4:2:2) from UYVY, YUY2 or planar 4:2:2; v210 (10-bit 4:2:2) from v210 or planar 10-bit 4:2:2; raw (24-bit R, G, B, top
/// down) from raw, AVI's BI_RGB or planar G, B and R (the identity matrix).
/// </summary>
internal sealed class MovRawVideo
{
    private readonly Source _source;

    private MovRawVideo(string type, Source source, int width, int height)
    {
        Type = type;
        _source = source;
        Width = width;
        Height = height;
    }

    private enum Source
    {
        Uyvy,
        Yuyv,
        Planar422,
        V210,
        Planar422Ten,
        BgrBottomUp,
        BgrTopDown,
        PlanarGbr,
        Rgb,
    }

    /// <summary>Gets the sample description type: 2vuy, v210 or "raw ".</summary>
    public string Type { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Gets whether packets are written as they come.</summary>
    public bool IsCopy => _source is Source.Uyvy or Source.V210 or Source.Rgb;

    /// <summary>Gets the bytes a QuickTime frame takes.</summary>
    public int FrameSize => checked(Type switch
    {
        "2vuy" => ((Width + 1) / 2) * 4 * Height,
        "v210" => ((Width + 47) / 48) * 128 * Height,
        _ => Width * 3 * Height,
    });

    /// <summary>Gets how QuickTime stores a track, or <see langword="null"/> when it has no layout for it.</summary>
    public static MovRawVideo? For(VideoTrackInfo track)
    {
        if (track.Width <= 0 || track.Height <= 0)
        {
            return null;
        }

        Source? source = null;
        if (track.CodecId == CodecIds.Uncompressed && !track.HasAlpha)
        {
            source = (track.FourCC, track.ChromaFormat, track.BitDepth) switch
            {
                ("UYVY" or "2vuy", _, _) => Source.Uyvy,
                ("YUY2" or "YUYV", _, _) => Source.Yuyv,
                ("v210", _, _) => Source.V210,
                ("raw ", _, _) => Source.Rgb,
                ("Y42B", _, _) or (null, ChromaFormat.Yuv422, 8) => Source.Planar422,
                (null, ChromaFormat.Yuv422, 10) => Source.Planar422Ten,
                ("444P", _, _) or (null, ChromaFormat.Yuv444, 8) when track.Color is { MatrixCoefficients: 0 } => Source.PlanarGbr,
                _ => null,
            };
        }
        else if (track.CodecId == CodecIds.VfwFourCC && track.CodecPrivate.Length >= 20)
        {
            ReadOnlySpan<byte> header = track.CodecPrivate.Span;
            if (BinaryPrimitives.ReadUInt32LittleEndian(header[16..]) == 0 && BinaryPrimitives.ReadUInt16LittleEndian(header[14..]) == 24)
            {
                source = BinaryPrimitives.ReadInt32LittleEndian(header[8..]) > 0 ? Source.BgrBottomUp : Source.BgrTopDown;
            }
        }

        return source switch
        {
            null => null,
            Source.Uyvy or Source.Yuyv or Source.Planar422 => new("2vuy", source.Value, track.Width, track.Height),
            Source.V210 or Source.Planar422Ten => new("v210", source.Value, track.Width, track.Height),
            _ => new("raw ", source.Value, track.Width, track.Height),
        };
    }

    /// <summary>
    /// Gets the QuickTime entry an uncompressed track was read from, when its packets are still in that entry's layout and size
    /// (a remux), else empty: such a track is written with its own entry.
    /// </summary>
    public static ReadOnlySpan<byte> SourceEntry(VideoTrackInfo track)
    {
        ReadOnlySpan<byte> entry = track.QuickTimeEntry.Span;
        if (track.CodecId != CodecIds.Uncompressed || entry.Length < 8 + Mp4Codecs.VisualFields || BinaryPrimitives.ReadUInt32BigEndian(entry) != entry.Length
            || For(track) is not { IsCopy: true } raw || Encoding.Latin1.GetString(entry[4..8]) != raw.Type
            || BinaryPrimitives.ReadUInt16BigEndian(entry[(8 + 24)..]) != track.Width || BinaryPrimitives.ReadUInt16BigEndian(entry[(8 + 26)..]) != track.Height)
        {
            return [];
        }

        return entry;
    }

    /// <summary>Repacks one frame into <paramref name="target"/>, <see cref="FrameSize"/> bytes.</summary>
    /// <exception cref="ArgumentException">The packet is smaller than a frame of the track's layout.</exception>
    public void Convert(ReadOnlySpan<byte> frame, Span<byte> target)
    {
        int w = Width, h = Height, half = (w + 1) / 2;
        switch (_source)
        {
            case Source.Uyvy or Source.V210 or Source.Rgb:
                Need(frame, FrameSize);
                frame[..FrameSize].CopyTo(target);
                break;
            case Source.Yuyv:
                Need(frame, FrameSize);
                for (int i = 0; i + 1 < FrameSize; i += 2)
                {
                    target[i] = frame[i + 1];
                    target[i + 1] = frame[i];
                }

                break;
            case Source.Planar422:
                {
                    Need(frame, (w * h) + (2 * half * h));
                    ReadOnlySpan<byte> y = frame[..(w * h)], u = frame.Slice(w * h, half * h), v = frame.Slice((w * h) + (half * h), half * h);
                    for (int row = 0; row < h; row++)
                    {
                        Span<byte> o = target.Slice(row * half * 4, half * 4);
                        for (int x = 0; x < half; x++)
                        {
                            o[x * 4] = u[(row * half) + x];
                            o[(x * 4) + 1] = y[(row * w) + (2 * x)];
                            o[(x * 4) + 2] = v[(row * half) + x];
                            o[(x * 4) + 3] = (2 * x) + 1 < w ? y[(row * w) + (2 * x) + 1] : y[(row * w) + (2 * x)];
                        }
                    }

                    break;
                }

            case Source.Planar422Ten:
                Need(frame, ((w * h) + (2 * half * h)) * 2);
                PackV210(frame, target, w, h, half);
                break;
            case Source.BgrBottomUp or Source.BgrTopDown:
                {
                    int stride = ((w * 3) + 3) / 4 * 4;
                    Need(frame, stride * h);
                    for (int row = 0; row < h; row++)
                    {
                        ReadOnlySpan<byte> s = frame.Slice((_source == Source.BgrTopDown ? row : h - 1 - row) * stride, w * 3);
                        Span<byte> o = target.Slice(row * w * 3, w * 3);
                        for (int x = 0; x < w * 3; x += 3)
                        {
                            o[x] = s[x + 2];
                            o[x + 1] = s[x + 1];
                            o[x + 2] = s[x];
                        }
                    }

                    break;
                }

            default:
                {
                    // Planar G, B, R.
                    Need(frame, 3 * w * h);
                    ReadOnlySpan<byte> g = frame[..(w * h)], b = frame.Slice(w * h, w * h), r = frame.Slice(2 * w * h, w * h);
                    for (int i = 0; i < w * h; i++)
                    {
                        target[i * 3] = r[i];
                        target[(i * 3) + 1] = g[i];
                        target[(i * 3) + 2] = b[i];
                    }

                    break;
                }
        }
    }

    // v210: six pixels in four little-endian 32-bit words of three 10-bit samples, Cb0 Y0 Cr0 | Y1 Cb2 Y2 | Cr2 Y3 Cb4 | Y4 Cr4 Y5,
    // rows padded to 48 pixels (128 bytes). The planar source holds little-endian 16-bit samples, Y then Cb then Cr.
    private static void PackV210(ReadOnlySpan<byte> frame, Span<byte> target, int w, int h, int half)
    {
        int stride = (w + 47) / 48 * 128;
        target[..(stride * h)].Clear();
        int uAt = w * h * 2, vAt = uAt + (half * h * 2);
        Span<uint> s = stackalloc uint[12];
        for (int row = 0; row < h; row++)
        {
            Span<byte> o = target.Slice(row * stride, stride);
            for (int group = 0; group * 6 < w; group++)
            {
                for (int i = 0; i < 3; i++)
                {
                    int x = (group * 6) + (i * 2), c = (group * 3) + i;
                    s[i * 4] = c < half ? Sample(frame, uAt + (((row * half) + c) * 2)) : 0;
                    s[(i * 4) + 2] = c < half ? Sample(frame, vAt + (((row * half) + c) * 2)) : 0;
                    s[(i * 4) + 1] = x < w ? Sample(frame, ((row * w) + x) * 2) : 0;
                    s[(i * 4) + 3] = x + 1 < w ? Sample(frame, ((row * w) + x + 1) * 2) : 0;
                }

                for (int word = 0; word < 4; word++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(o[((group * 16) + (word * 4))..], s[word * 3] | (s[(word * 3) + 1] << 10) | (s[(word * 3) + 2] << 20));
                }
            }
        }
    }

    private static uint Sample(ReadOnlySpan<byte> frame, int at) => BinaryPrimitives.ReadUInt16LittleEndian(frame[at..]) & 0x3FFu;

    private void Need(ReadOnlySpan<byte> frame, int size)
    {
        if (frame.Length < size)
        {
            throw new ArgumentException($"The packet holds {frame.Length} bytes; a {Width}x{Height} frame needs {size}.", nameof(frame));
        }
    }
}
