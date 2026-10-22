using System.Buffers.Binary;
using System.Text;

using Egami.Colors;

namespace Egami.Video.Containers.Mp4;

/// <summary>
/// Writes the sample description of a track for ISO MP4 (V4.1 Design Q2 and Q11): av01, vp09, vp08, mp4v (Motion JPEG and MPEG-4
/// Part 2), avc1, hvc1, Opus, fLaC, mp4a (AAC and MP3), ipcm and fpcm, tx3g, and a pass-through track's own entry unchanged.
/// </summary>
internal static class Mp4SampleEntries
{
    /// <summary>Gets the handler type (hdlr) a track is written with.</summary>
    public static string HandlerOf(TrackInfo track) => track.Kind switch
    {
        TrackKind.Video => "vide",
        TrackKind.Audio => "soun",
        TrackKind.Subtitle => "sbtl",
        _ => track.CodecPrivate.Length >= 8 && track.CodecPrivate.Span[4..8].SequenceEqual("tmcd"u8) ? "tmcd" : "meta",
    };

    /// <summary>Returns why MP4 cannot hold a track, or <see langword="null"/> when it can.</summary>
    public static string? Unsupported(TrackInfo track) => track.CodecId switch
    {
        CodecIds.Av1 when track.CodecPrivate.IsEmpty => "an AV1 track needs its av1C configuration in CodecPrivate",
        CodecIds.Av1 or CodecIds.Vp9 or CodecIds.Vp8 or CodecIds.MotionJpeg or CodecIds.Avc or CodecIds.Hevc or CodecIds.Mpeg4Visual => null,
        CodecIds.Opus or CodecIds.Flac or CodecIds.Aac or CodecIds.Mp3 or CodecIds.Vorbis => null,
        CodecIds.PcmLittleEndian or CodecIds.PcmBigEndian or CodecIds.PcmFloat => null,
        CodecIds.MovText => null,
        CodecIds.QuickTimeVideo or CodecIds.QuickTimeAudio or CodecIds.QuickTimeOther when track.CodecPrivate.Length >= 8 => null,
        _ => $"MP4 cannot hold codec '{track.CodecId}'",
    };

    /// <summary>Writes the track's one sample description, inside stsd.</summary>
    public static void Write(BoxWriter w, TrackInfo track)
    {
        ReadOnlySpan<byte> priv = track.CodecPrivate.Span;
        switch (track.CodecId)
        {
            case CodecIds.QuickTimeVideo or CodecIds.QuickTimeAudio or CodecIds.QuickTimeOther:
                w.Bytes(priv);
                return;
            case CodecIds.MovText:
                w.Start("tx3g");
                if (priv.Length >= 8)
                {
                    w.Bytes(priv);
                }
                else
                {
                    DefaultTextEntry(w);
                }

                w.End();
                return;
        }

        if (track is VideoTrackInfo video)
        {
            string type = video.CodecId switch
            {
                CodecIds.Av1 => "av01",
                CodecIds.Vp9 => "vp09",
                CodecIds.Vp8 => "vp08",
                CodecIds.Avc => "avc1",
                CodecIds.Hevc => "hvc1",
                _ => "mp4v",
            };
            w.Start(type);
            VisualFields(w, video);
            switch (video.CodecId)
            {
                case CodecIds.Av1:
                    w.Start("av1C");
                    w.Bytes(priv);
                    w.End();
                    break;
                case CodecIds.Vp9 or CodecIds.Vp8:
                    Vpcc(w, video);
                    break;
                case CodecIds.Avc:
                    w.Start("avcC");
                    w.Bytes(priv);
                    w.End();
                    break;
                case CodecIds.Hevc:
                    w.Start("hvcC");
                    w.Bytes(priv);
                    w.End();
                    break;
                case CodecIds.MotionJpeg:
                    Esds(w, 0x6C, 0x04, []);
                    break;
                default:
                    Esds(w, 0x20, 0x04, priv);
                    break;
            }

            ColorBoxes(w, video);
            w.End();
            return;
        }

        AudioTrackInfo audio = (AudioTrackInfo)track;
        string entry = audio.CodecId switch
        {
            CodecIds.Opus => "Opus",
            CodecIds.Flac => "fLaC",
            CodecIds.Aac or CodecIds.Mp3 or CodecIds.Vorbis => "mp4a",
            CodecIds.PcmFloat => "fpcm",
            _ => "ipcm",
        };
        w.Start(entry);
        int rate = audio.CodecId == CodecIds.Opus ? 48000 : audio.SampleRate;
        int bits = audio.BitsPerSample > 0 ? audio.BitsPerSample : 16;
        bool wide = rate > ushort.MaxValue;
        w.Zeros(6);
        w.U16(1);
        w.U16(wide ? 1 : 0); // entry_version 1 holds rates above 65535 in an srat box
        w.Zeros(6);
        w.U16(audio.Channels);
        w.U16(bits);
        w.U16(0);
        w.U16(0);
        w.U32(wide ? 1u << 16 : (uint)rate << 16);
        switch (audio.CodecId)
        {
            case CodecIds.Opus:
                Dops(w, priv);
                break;
            case CodecIds.Flac:
                w.StartFull("dfLa", 0, 0);
                w.Bytes(priv.StartsWith("fLaC"u8) ? priv[4..] : priv);
                w.End();
                break;
            case CodecIds.Aac:
                Esds(w, 0x40, 0x05, priv);
                break;
            case CodecIds.Mp3:
                Esds(w, 0x6B, 0x05, []);
                break;
            case CodecIds.Vorbis:
                // Object type 0xDD, as FFmpeg writes Vorbis in MP4: the three headers, Xiph-laced, as the decoder specific info.
                Esds(w, 0xDD, 0x05, priv);
                break;
            default:
                // ISO/IEC 23003-5 pcmC: bit 0 of the format flags is little-endian.
                w.StartFull("pcmC", 0, 0);
                w.U8(audio.CodecId == CodecIds.PcmBigEndian ? 0 : 1);
                w.U8(bits);
                w.End();
                break;
        }

        if (entry is "ipcm" or "fpcm" && audio.Channels is >= 1 and <= 8)
        {
            // chnl (ISO/IEC 14496-12 12.2.4): a defined layout from ISO/IEC 23001-8, so readers need not guess one.
            w.StartFull("chnl", 0, 0);
            w.U8(1); // channel-structured
            w.U8(audio.Channels switch { 1 => 1, 2 => 2, 3 => 3, 4 => 4, 5 => 5, 6 => 6, 8 => 12, _ => 0 });
            if (audio.Channels == 7)
            {
                // No defined layout: front left, front right, centre, LFE, back left, back right, back centre.
                foreach (int speaker in (ReadOnlySpan<int>)[0, 1, 2, 3, 4, 5, 9])
                {
                    w.U8(speaker);
                }
            }
            else
            {
                w.U64(0); // no channels omitted
            }

            w.End();
        }

        if (wide)
        {
            w.StartFull("srat", 0, 0);
            w.U32((uint)rate);
            w.End();
        }

        w.End();
    }

    private static void VisualFields(BoxWriter w, VideoTrackInfo video)
    {
        w.Zeros(6);
        w.U16(1); // data reference index
        w.Zeros(16);
        w.U16(video.Width);
        w.U16(video.Height);
        w.U32(0x00480000); // 72 dpi
        w.U32(0x00480000);
        w.U32(0);
        w.U16(1); // frames per sample
        w.Zeros(32); // compressor name
        w.U16(0x18);
        w.U16(0xFFFF);
    }

    private static void Vpcc(BoxWriter w, VideoTrackInfo video)
    {
        // Profile, level, bit depth and chroma come from Matroska's VP9 CodecPrivate features when present.
        int profile = 0, level = 10, depth = video.BitDepth > 0 ? video.BitDepth : 8;
        int chroma = video.ChromaFormat switch { Pixels.ChromaFormat.Yuv422 => 2, Pixels.ChromaFormat.Yuv444 => 3, _ => 1 };
        ReadOnlySpan<byte> features = video.CodecPrivate.Span;
        for (int at = 0; at + 3 <= features.Length; at += 2 + features[at + 1])
        {
            if (features[at + 1] != 1)
            {
                continue;
            }

            byte value = features[at + 2];
            switch (features[at])
            {
                case 1: profile = value; break;
                case 2: level = value; break;
                case 3: depth = value; break;
                case 4: chroma = value; break;
            }
        }

        ColorCodePoints color = video.Color ?? new ColorCodePoints(2, 2, 2, false);
        w.StartFull("vpcC", 1, 0);
        w.U8(profile);
        w.U8(level);
        w.U8((depth << 4) | (chroma << 1) | (color.FullRange ? 1 : 0));
        w.U8(color.Primaries);
        w.U8(color.TransferCharacteristics);
        w.U8(color.MatrixCoefficients);
        w.U16(0);
        w.End();
    }

    private static void ColorBoxes(BoxWriter w, VideoTrackInfo video)
    {
        if (video.Color is { } color)
        {
            w.Start("colr");
            w.Type("nclx");
            w.U16(color.Primaries);
            w.U16(color.TransferCharacteristics);
            w.U16(color.MatrixCoefficients);
            w.U8(color.FullRange ? 0x80 : 0);
            w.End();
        }

        if (video.PixelAspectRatio != new Rational(1, 1) && video.PixelAspectRatio.Numerator > 0)
        {
            w.Start("pasp");
            w.U32((uint)Math.Min(video.PixelAspectRatio.Numerator, uint.MaxValue));
            w.U32((uint)Math.Min(video.PixelAspectRatio.Denominator, uint.MaxValue));
            w.End();
        }

        if (video.Hdr is { } hdr)
        {
            if (hdr.MaxContentLightLevel is not null || hdr.MaxFrameAverageLightLevel is not null)
            {
                w.Start("clli");
                w.U16((int)Math.Clamp(hdr.MaxContentLightLevel ?? 0, 0, 65535));
                w.U16((int)Math.Clamp(hdr.MaxFrameAverageLightLevel ?? 0, 0, 65535));
                w.End();
            }

            if (hdr.MasteringPrimaries is { } p)
            {
                // G, B, R, white in 0.00002 units; luminance in 0.0001 cd/m2.
                static int C(double v) => (int)Math.Clamp(Math.Round(v / 0.00002), 0, 65535);
                w.Start("mdcv");
                foreach (Chromaticity c in (ReadOnlySpan<Chromaticity>)[p.Green, p.Blue, p.Red, p.White])
                {
                    w.U16(C(c.X));
                    w.U16(C(c.Y));
                }

                w.U32((uint)Math.Clamp(Math.Round((hdr.MasteringMaxLuminance ?? 0) / 0.0001), 0, uint.MaxValue));
                w.U32((uint)Math.Clamp(Math.Round((hdr.MasteringMinLuminance ?? 0) / 0.0001), 0, uint.MaxValue));
                w.End();
            }
        }
    }

    /// <summary>Writes an esds box: an ES descriptor with a decoder config (object type, stream type) and its specific info.</summary>
    private static void Esds(BoxWriter w, int objectType, int streamType, ReadOnlySpan<byte> specific)
    {
        int decSpecific = specific.Length > 0 ? 1 + LengthSize(specific.Length) + specific.Length : 0;
        int decConfig = 13 + decSpecific;
        int es = 3 + 1 + LengthSize(decConfig) + decConfig + 3;
        w.StartFull("esds", 0, 0);
        w.U8(3);
        Length(w, es);
        w.U16(0); // ES_ID
        w.U8(0);
        w.U8(4);
        Length(w, decConfig);
        w.U8(objectType);
        w.U8((streamType << 2) | 1);
        w.U24(0); // buffer size
        w.U32(0); // max bitrate
        w.U32(0); // average bitrate
        if (specific.Length > 0)
        {
            w.U8(5);
            Length(w, specific.Length);
            w.Bytes(specific);
        }

        w.U8(6); // SLConfigDescriptor, predefined 2
        Length(w, 1);
        w.U8(2);
        w.End();
    }

    private static int LengthSize(int length) => length < 0x80 ? 1 : length < 0x4000 ? 2 : length < 0x200000 ? 3 : 4;

    private static void Length(BoxWriter w, int length)
    {
        for (int shift = 7 * (LengthSize(length) - 1); shift > 0; shift -= 7)
        {
            w.U8(0x80 | ((length >> shift) & 0x7F));
        }

        w.U8(length & 0x7F);
    }

    /// <summary>Writes dOps (Opus in ISO BMFF, big-endian) from Matroska's OpusHead (little-endian).</summary>
    private static void Dops(BoxWriter w, ReadOnlySpan<byte> head)
    {
        w.Start("dOps");
        if (head.Length >= 19 && head.StartsWith("OpusHead"u8))
        {
            w.U8(0);
            w.U8(head[9]);
            w.U16(BinaryPrimitives.ReadUInt16LittleEndian(head[10..]));
            w.U32(BinaryPrimitives.ReadUInt32LittleEndian(head[12..]));
            w.U16(BinaryPrimitives.ReadInt16LittleEndian(head[16..]));
            w.U8(head[18]);
            if (head[18] != 0)
            {
                w.Bytes(head[19..]);
            }
        }
        else
        {
            // No OpusHead: stereo, no pre-skip, 48 kHz, family 0.
            w.U8(0);
            w.U8(2);
            w.U16(0);
            w.U32(48000);
            w.U16(0);
            w.U8(0);
        }

        w.End();
    }

    /// <summary>A text sample entry with no styles set: white 18-pixel text in font 1, Serif.</summary>
    private static void DefaultTextEntry(BoxWriter w)
    {
        w.Zeros(6);
        w.U16(1);
        w.U32(0); // display flags
        w.U8(1); // horizontal justification: centered
        w.U8(0xFF); // vertical: bottom
        w.U32(0); // background
        w.Zeros(8); // text box
        w.U16(0);
        w.U16(0);
        w.U16(1); // font id
        w.U8(0); // face
        w.U8(18); // size
        w.U32(0xFFFFFFFF); // color
        w.Start("ftab");
        w.U16(1);
        w.U16(1);
        byte[] name = Encoding.ASCII.GetBytes("Serif");
        w.U8(name.Length);
        w.Bytes(name);
        w.End();
    }
}
