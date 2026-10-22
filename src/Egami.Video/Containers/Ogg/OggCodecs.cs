using System.Buffers.Binary;

using Egami.Video.Audio.Codecs.Flac;
using Egami.Video.Audio.Codecs.Opus;

namespace Egami.Video.Containers.Ogg;

/// <summary>What an Ogg logical stream holds, from its first packet, and how its granule positions count.</summary>
internal enum OggCodec
{
    Unknown,
    Opus,
    Vorbis,
    Flac,
}

/// <summary>
/// The Ogg mappings of Opus (RFC 7845), Vorbis (Vorbis I specification, section 4 and appendix A) and FLAC (the FLAC-in-Ogg
/// mapping): how many header packets each stream starts with, what they hold, and how long an audio packet is.
/// </summary>
internal static class OggCodecs
{
    public static OggCodec Identify(ReadOnlySpan<byte> first) =>
        first.StartsWith("OpusHead"u8) ? OggCodec.Opus
        : first.Length >= 7 && first[0] == 1 && first[1..7].SequenceEqual("vorbis"u8) ? OggCodec.Vorbis
        : first.Length >= 13 && first[0] == 0x7F && first[1..5].SequenceEqual("FLAC"u8) ? OggCodec.Flac
        : OggCodec.Unknown;

    /// <summary>The number of header packets, the first included: 2 for Opus, 3 for Vorbis, 1 plus the count in FLAC's first packet.</summary>
    public static int HeaderCount(OggCodec codec, ReadOnlySpan<byte> first) => codec switch
    {
        OggCodec.Opus => 2,
        OggCodec.Vorbis => 3,
        OggCodec.Flac => 1 + BinaryPrimitives.ReadUInt16BigEndian(first[7..]),
        _ => 1,
    };

    /// <summary>Builds the track from the header packets. Returns null when they are broken.</summary>
    public static TrackInfo? Track(OggCodec codec, int number, List<byte[]> headers, Dictionary<string, string> tags)
    {
        switch (codec)
        {
            case OggCodec.Opus:
                {
                    if (OpusHead.Read(headers[0]) is not { } head)
                    {
                        return null;
                    }

                    if (headers.Count > 1 && headers[1].AsSpan().StartsWith("OpusTags"u8))
                    {
                        FlacMetadata.ReadComments(headers[1].AsSpan(8), tags);
                    }

                    return new AudioTrackInfo
                    {
                        Number = number,
                        Kind = TrackKind.Audio,
                        CodecId = CodecIds.Opus,
                        CodecPrivate = headers[0],
                        TimeBase = new Rational(1, 48000),
                        SampleRate = 48000,
                        Channels = head.Channels,
                        CodecDelay = new MediaTime(head.PreSkip, new Rational(1, 48000)),
                        SeekPreRoll = new MediaTime(80, new Rational(1, 1000)),
                        Tags = tags,
                    };
                }

            case OggCodec.Vorbis:
                {
                    ReadOnlySpan<byte> id = headers[0];
                    if (id.Length < 30)
                    {
                        return null;
                    }

                    int channels = id[11];
                    int rate = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(id[12..]), int.MaxValue);
                    if (channels == 0 || rate == 0)
                    {
                        return null;
                    }

                    if (headers.Count > 1 && headers[1].Length > 7 && headers[1][0] == 3)
                    {
                        FlacMetadata.ReadComments(headers[1].AsSpan(7), tags);
                    }

                    return new AudioTrackInfo
                    {
                        Number = number,
                        Kind = TrackKind.Audio,
                        CodecId = CodecIds.Vorbis,
                        CodecPrivate = XiphLace(headers),
                        TimeBase = new Rational(1, rate),
                        SampleRate = rate,
                        Channels = channels,
                        Tags = tags,
                    };
                }

            case OggCodec.Flac:
                {
                    ReadOnlySpan<byte> first = headers[0];
                    if (first.Length < 13 + 4 + FlacStreamInfo.Size || !first[9..13].SequenceEqual("fLaC"u8) || FlacStreamInfo.Read(first[17..]) is not { } info)
                    {
                        return null;
                    }

                    List<FlacBlock> blocks = [new(FlacBlock.StreamInfo, first.Slice(17, FlacStreamInfo.Size).ToArray())];
                    foreach (byte[] header in headers.Skip(1))
                    {
                        if (header.Length >= 4)
                        {
                            int type = header[0] & 0x7F;
                            if (type == FlacBlock.VorbisComment)
                            {
                                FlacMetadata.ReadComments(header.AsSpan(4), tags);
                            }
                        }
                    }

                    return new AudioTrackInfo
                    {
                        Number = number,
                        Kind = TrackKind.Audio,
                        CodecId = CodecIds.Flac,
                        CodecPrivate = FlacMetadata.WriteBlocks(blocks),
                        TimeBase = new Rational(1, info.SampleRate),
                        SampleRate = info.SampleRate,
                        Channels = info.Channels,
                        BitsPerSample = info.BitsPerSample,
                        Layout = FlacMetadata.LayoutFromTags(tags, info.Channels),
                        Tags = tags,
                    };
                }

            default:
                return null;
        }
    }

    /// <summary>The samples an audio packet holds, in the track's time base, or 0 when the mapping cannot tell from the packet alone.</summary>
    public static long Duration(OggCodec codec, ReadOnlySpan<byte> packet, FlacStreamInfo? flac) => codec switch
    {
        OggCodec.Opus => packet.Length > 0 ? Math.Max(0, OpusPacket.SampleCount(packet, 48000)) : 0,
        OggCodec.Flac => FlacFrameHeader.Parse(packet, flac) is { } h ? h.BlockSize : 0,
        _ => 0,
    };

    /// <summary>The header packets an Ogg writer starts a stream with, from a track as any demuxer describes it.</summary>
    public static (OggCodec Codec, List<byte[]> Headers) Headers(TrackInfo track, IReadOnlyDictionary<string, string> tags)
    {
        switch (track.CodecId)
        {
            case CodecIds.Opus:
                {
                    OpusHead head = OpusHead.Read(track.CodecPrivate.Span)
                        ?? (track is AudioTrackInfo { Channels: 1 or 2 } a
                            ? new OpusHead(1, a.Channels, (int)(track.CodecDelay?.Rescale(new Rational(1, 48000)).Ticks ?? 312), 48000, 0, 0, 1, a.Channels - 1, a.Channels == 1 ? [0] : [0, 1])
                            : throw new NotSupportedException("The Opus track has no OpusHead in its codec private data."));
                    byte[] comments = FlacMetadata.WriteComments(Audio.Codecs.FlacCodec.Vendor, tags);
                    return (OggCodec.Opus, [head.Write(), [.. "OpusTags"u8, .. comments]]);
                }

            case CodecIds.Vorbis:
                {
                    List<byte[]> headers = XiphUnlace(track.CodecPrivate.Span) ?? throw new NotSupportedException("The Vorbis track's codec private data does not hold its three headers.");
                    return (OggCodec.Vorbis, headers);
                }

            case CodecIds.Flac:
                {
                    List<FlacBlock> blocks = FlacMetadata.ReadBlocks(track.CodecPrivate.Span);
                    byte[] info = blocks.FirstOrDefault(b => b.Type == FlacBlock.StreamInfo).Body ?? throw new NotSupportedException("The FLAC track has no STREAMINFO.");
                    byte[] comments = FlacMetadata.WriteComments(Audio.Codecs.FlacCodec.Vendor, tags);
                    byte[] first = new byte[13 + 4 + FlacStreamInfo.Size];
                    first[0] = 0x7F;
                    "FLAC"u8.CopyTo(first.AsSpan(1));
                    first[5] = 1;
                    first[6] = 0;
                    BinaryPrimitives.WriteUInt16BigEndian(first.AsSpan(7), 1);
                    "fLaC"u8.CopyTo(first.AsSpan(9));
                    first[13] = FlacBlock.StreamInfo;
                    first[14] = 0;
                    first[15] = 0;
                    first[16] = FlacStreamInfo.Size;
                    info.AsSpan(0, FlacStreamInfo.Size).CopyTo(first.AsSpan(17));
                    byte[] comment = new byte[4 + comments.Length];
                    comment[0] = 0x80 | FlacBlock.VorbisComment;
                    comment[1] = (byte)(comments.Length >> 16);
                    comment[2] = (byte)(comments.Length >> 8);
                    comment[3] = (byte)comments.Length;
                    comments.CopyTo(comment, 4);
                    return (OggCodec.Flac, [first, comment]);
                }

            default:
                throw new NotSupportedException($"Ogg holds Opus, Vorbis and FLAC; not '{track.CodecId}'.");
        }
    }

    /// <summary>Xiph lacing of header packets, as Matroska's CodecPrivate stores Vorbis's three headers.</summary>
    public static byte[] XiphLace(List<byte[]> packets)
    {
        using MemoryStream s = new();
        s.WriteByte((byte)(packets.Count - 1));
        for (int i = 0; i < packets.Count - 1; i++)
        {
            int length = packets[i].Length;
            while (length >= 255)
            {
                s.WriteByte(255);
                length -= 255;
            }

            s.WriteByte((byte)length);
        }

        foreach (byte[] p in packets)
        {
            s.Write(p);
        }

        return s.ToArray();
    }

    public static List<byte[]>? XiphUnlace(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1)
        {
            return null;
        }

        int count = data[0] + 1;
        int p = 1;
        List<int> lengths = [];
        for (int i = 0; i < count - 1; i++)
        {
            int length = 0;
            while (p < data.Length && data[p] == 255)
            {
                length += 255;
                p++;
            }

            if (p >= data.Length)
            {
                return null;
            }

            length += data[p++];
            lengths.Add(length);
        }

        lengths.Add(data.Length - p - lengths.Sum());
        if (lengths[^1] < 0)
        {
            return null;
        }

        List<byte[]> packets = [];
        foreach (int length in lengths)
        {
            packets.Add(data.Slice(p, length).ToArray());
            p += length;
        }

        return packets;
    }

    /// <summary>The TITLE tag of a stream, as a chained link's chapter title, or null.</summary>
    public static string? Title(IReadOnlyDictionary<string, string> tags) => tags.TryGetValue("TITLE", out string? title) ? title : null;
}
