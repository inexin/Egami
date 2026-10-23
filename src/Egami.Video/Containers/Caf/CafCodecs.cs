using System.Buffers.Binary;

using Egami.Video.Audio;
using Egami.Video.Audio.Codecs.Opus;
using Egami.Video.Containers.Mp4;

namespace Egami.Video.Containers.Caf;

/// <summary>How a CAF track's samples are laid out, as the reader needs it.</summary>
/// <param name="CodecId">The codec id.</param>
/// <param name="CodecPrivate">The codec setup, in Matroska's form.</param>
/// <param name="Bits">The bits per sample, 0 for compressed codecs.</param>
/// <param name="Swap">The byte run to reverse in each sample (big-endian float), or 0.</param>
/// <param name="FramesPerPacket">The frames each packet decodes to, when every packet decodes to the same number; else 0.</param>
internal sealed record CafCodec(string CodecId, ReadOnlyMemory<byte> CodecPrivate, int Bits, int Swap, int FramesPerPacket);

/// <summary>Maps CAF format ids, flags and magic cookies to Egami.Video codec ids and back.</summary>
internal static class CafCodecs
{
    /// <summary>The size of ALAC's ALACSpecificConfig, Matroska's A_ALAC CodecPrivate.</summary>
    public const int AlacConfigSize = 24;

    /// <summary>Returns the codec of a desc chunk and its magic cookie (kuki), and the pre-skip for Opus.</summary>
    public static CafCodec Read(CafDescription desc, ReadOnlySpan<byte> cookie, long priming)
    {
        int channels = (int)desc.Channels;
        int frames = (int)Math.Min(desc.FramesPerPacket, int.MaxValue);
        switch (desc.FormatId)
        {
            case "lpcm":
                int width = channels > 0 && desc.BytesPerPacket > 0 ? (int)(desc.BytesPerPacket / desc.Channels) : ((int)desc.Bits + 7) / 8;
                int bits = width * 8;
                bool little = (desc.Flags & CafDescription.FlagLittleEndian) != 0;
                if ((desc.Flags & CafDescription.FlagFloat) != 0)
                {
                    return new CafCodec(CodecIds.PcmFloat, default, bits, little ? 0 : width, 1);
                }

                // An 8-bit sample is signed in CAF, as big-endian PCM's is in Egami.Video.
                return new CafCodec(little && bits > 8 ? CodecIds.PcmLittleEndian : CodecIds.PcmBigEndian, default, bits, 0, 1);
            case "ulaw":
                return new CafCodec(CodecIds.G711MuLaw, default, 8, 0, 1);
            case "alaw":
                return new CafCodec(CodecIds.G711ALaw, default, 8, 0, 1);
            case "alac":
                byte[] config = AlacConfig(cookie);
                return new CafCodec(CodecIds.Alac, config, 0, 0, config.Length == AlacConfigSize ? (int)BinaryPrimitives.ReadUInt32BigEndian(config) : frames);
            case "opus":
                return new CafCodec(CodecIds.Opus, OpusHeadFor(cookie, channels, priming, desc.SampleRate), 0, 0, frames);
            case "aac ":
                byte[] esds = [0, 0, 0, 0, .. cookie];
                (int objectType, ReadOnlyMemory<byte> specific) = cookie.IsEmpty ? (0x40, default) : Mp4Codecs.ReadEsds(esds);
                return objectType is 0x69 or 0x6B
                    ? new CafCodec(CodecIds.Mp3, default, 0, 0, frames)
                    : new CafCodec(CodecIds.Aac, specific, 0, 0, frames);
            case ".mp3":
                return new CafCodec(CodecIds.Mp3, default, 0, 0, frames);
            case ".mp2":
                return new CafCodec(CodecIds.Mp2, default, 0, 0, frames);
            case ".mp1":
                return new CafCodec(CodecIds.Mp1, default, 0, 0, frames);
            case "ac-3":
                return new CafCodec(CodecIds.Ac3, default, 0, 0, frames);
            case "ec-3":
                return new CafCodec(CodecIds.EAc3, default, 0, 0, frames);
            case "flac":
                return new CafCodec(CodecIds.Flac, FlacPrivate(cookie), 0, 0, frames);
            default:
                return new CafCodec("X_CAF/" + desc.FormatId.TrimEnd(), cookie.ToArray(), 0, 0, frames);
        }
    }

    /// <summary>
    /// Returns the desc fields a track is written with (bytes and frames per packet are 0 for compressed codecs, which the
    /// packet table describes) and its magic cookie, or null when CAF cannot hold the codec.
    /// </summary>
    public static (CafDescription Desc, byte[]? Cookie, int FramesPerPacket)? Write(AudioTrackInfo track)
    {
        uint channels = (uint)track.Channels;
        double rate = track.SampleRate;
        switch (track.CodecId)
        {
            case CodecIds.PcmLittleEndian or CodecIds.PcmBigEndian or CodecIds.PcmFloat:
                bool isFloat = track.CodecId == CodecIds.PcmFloat;
                int bits = track.BitsPerSample > 0 ? track.BitsPerSample : isFloat ? 32 : 16;
                if (isFloat ? bits is not (32 or 64) : bits is not (8 or 16 or 24 or 32))
                {
                    return null;
                }

                uint flags = (isFloat ? CafDescription.FlagFloat : 0) | (track.CodecId != CodecIds.PcmBigEndian && bits > 8 ? CafDescription.FlagLittleEndian : 0);
                return (new CafDescription(rate, "lpcm", flags, channels * (uint)(bits / 8), 1, channels, (uint)bits), null, 1);
            case CodecIds.G711MuLaw or CodecIds.G711ALaw:
                return (new CafDescription(rate, track.CodecId == CodecIds.G711MuLaw ? "ulaw" : "alaw", 0, channels, 1, channels, 8), null, 1);
            case CodecIds.Alac:
                ReadOnlySpan<byte> config = AlacConfig(track.CodecPrivate.Span);
                if (config.Length != AlacConfigSize)
                {
                    return null;
                }

                int frameLength = (int)BinaryPrimitives.ReadUInt32BigEndian(config);
                uint alacFlags = config[5] switch { 16 => 1u, 20 => 2u, 24 => 3u, 32 => 4u, _ => 0u };
                return (new CafDescription(rate, "alac", alacFlags, 0, (uint)frameLength, channels, 0), config.ToArray(), frameLength);
            case CodecIds.Opus:
                OpusHead? head = OpusHead.Read(track.CodecPrivate.Span);
                if (head is null)
                {
                    return null;
                }

                // Mono and stereo need no cookie; the channel mapping of more channels goes in one, as OpusHead.
                return (new CafDescription(48000, "opus", 0, 0, 0, channels, 0), head.MappingFamily == 0 ? null : head.Write(), 0);
            case CodecIds.Aac when !track.CodecPrivate.IsEmpty:
                return (new CafDescription(rate, "aac ", 0, 0, 1024, channels, 0), EsDescriptor(0x40, track.CodecPrivate.Span), 1024);
            case CodecIds.Mp3 or CodecIds.Mp2 or CodecIds.Mp1:
                string id = track.CodecId switch { CodecIds.Mp3 => ".mp3", CodecIds.Mp2 => ".mp2", _ => ".mp1" };
                int mpegFrames = track.CodecId == CodecIds.Mp1 ? 384 : track.CodecId == CodecIds.Mp3 && track.SampleRate < 32000 ? 576 : 1152;
                return (new CafDescription(rate, id, 0, 0, (uint)mpegFrames, channels, 0), null, mpegFrames);
            case CodecIds.Ac3:
                return (new CafDescription(rate, "ac-3", 0, 0, 1536, channels, 0), null, 1536);
            case CodecIds.EAc3:
                // Frames of fewer than six blocks hold fewer samples; the packet table then gives each packet's own count.
                return (new CafDescription(rate, "ec-3", 0, 0, 1536, channels, 0), null, 1536);
            case CodecIds.Flac:
                return (new CafDescription(rate, "flac", 0, 0, 0, channels, 0), FlacCookie(track.CodecPrivate.Span), 0);
            default:
                return null;
        }
    }

    /// <summary>
    /// Takes ALACSpecificConfig out of a cookie in any of its forms: the bare 24 bytes, or wrapped in QuickTime's frma and alac
    /// atoms (which FFmpeg writes), with a channel layout atom and terminator after it.
    /// </summary>
    public static byte[] AlacConfig(ReadOnlySpan<byte> cookie)
    {
        if (cookie.Length >= 12 && cookie[4..8].SequenceEqual("frma"u8))
        {
            cookie = cookie[12..];
        }

        if (cookie.Length >= 12 + AlacConfigSize && cookie[4..8].SequenceEqual("alac"u8))
        {
            cookie = cookie[12..];
        }

        return cookie.Length >= AlacConfigSize ? cookie[..AlacConfigSize].ToArray() : [];
    }

    /// <summary>Builds an MPEG-4 ES descriptor (ISO/IEC 14496-1) holding a decoder config with the given specific info: AAC's cookie in CAF.</summary>
    public static byte[] EsDescriptor(int objectType, ReadOnlySpan<byte> specific)
    {
        using MemoryStream d = new();
        int decoderConfig = 13 + 2 + specific.Length;
        int es = 3 + 2 + decoderConfig + 3;
        d.WriteByte(3);
        d.WriteByte((byte)es);
        d.Write([0, 0, 0]); // ES_ID and flags
        d.WriteByte(4);
        d.WriteByte((byte)decoderConfig);
        d.WriteByte((byte)objectType);
        d.WriteByte(0x15); // audio stream, upstream 0, reserved 1
        d.Write(new byte[11]); // buffer size, max and average bit rate
        d.WriteByte(5);
        d.WriteByte((byte)specific.Length);
        d.Write(specific);
        d.Write([6, 1, 2]); // SLConfigDescriptor, predefined MP4
        return d.ToArray();
    }

    /// <summary>The OpusHead for a CAF Opus track: the cookie when it holds one, else one made from the channel count and priming.</summary>
    private static byte[] OpusHeadFor(ReadOnlySpan<byte> cookie, int channels, long priming, double rate)
    {
        if (OpusHead.Read(cookie) is { } head)
        {
            return head.Write();
        }

        int preSkip = (int)Math.Clamp(priming, 0, ushort.MaxValue);
        int inputRate = (int)Math.Clamp(rate, 0, int.MaxValue);
        if (channels is < 1 or > 8)
        {
            return [];
        }

        if (channels <= 2)
        {
            return new OpusHead(1, channels, preSkip, inputRate, 0, 0, 1, channels - 1, channels == 1 ? [0] : [0, 1]).Write();
        }

        (int streams, int coupled, byte[] mapping) = OpusMultistreamEncoder.VorbisMappings[channels - 1];
        return new OpusHead(1, channels, preSkip, inputRate, 0, 1, streams, coupled, mapping).Write();
    }

    /// <summary>FLAC's CodecPrivate ("fLaC" and metadata blocks) from a cookie: a dfLa box, its body, or the metadata blocks with or without "fLaC".</summary>
    private static byte[] FlacPrivate(ReadOnlySpan<byte> cookie)
    {
        if (cookie.Length >= 12 && cookie[4..8].SequenceEqual("dfLa"u8))
        {
            cookie = cookie[12..];
        }
        else if (cookie.Length >= 4 + 38 && cookie[..4].SequenceEqual((ReadOnlySpan<byte>)[0, 0, 0, 0]) && (cookie[4] & 0x7F) == 0)
        {
            // A dfLa body: version and flags, then the blocks.
            cookie = cookie[4..];
        }

        if (cookie.StartsWith("fLaC"u8))
        {
            return cookie.ToArray();
        }

        return cookie.Length >= 38 && (cookie[0] & 0x7F) == 0 ? [.. "fLaC"u8, .. cookie] : [];
    }

    /// <summary>The cookie written for FLAC: a dfLa box (as in MP4) with the metadata blocks of the CodecPrivate.</summary>
    private static byte[]? FlacCookie(ReadOnlySpan<byte> codecPrivate)
    {
        if (!codecPrivate.StartsWith("fLaC"u8) || codecPrivate.Length < 4 + 38)
        {
            return null;
        }

        ReadOnlySpan<byte> blocks = codecPrivate[4..];
        byte[] box = new byte[12 + blocks.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        "dfLa"u8.CopyTo(box.AsSpan(4));
        blocks.CopyTo(box.AsSpan(12));

        // Only STREAMINFO goes in, marked as the last block, as MP4's dfLa does it when written by Egami.Video.
        int length = 4 + 34;
        if (blocks.Length > length)
        {
            box = box[..(12 + length)];
            BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        }

        box[12] |= 0x80;
        return box;
    }
}
