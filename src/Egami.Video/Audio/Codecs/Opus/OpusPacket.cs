// Translated from libopus 1.5.2 src/opus.c (opus_packet_parse_impl, parse_size, opus_packet_get_samples_per_frame) and
// src/opus_decoder.c (opus_packet_get_bandwidth, opus_packet_get_nb_frames, opus_packet_get_nb_samples).
using System.Buffers.Binary;

namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>Reads an Opus packet's TOC byte and frame packing (RFC 6716 section 3).</summary>
internal static class OpusPacket
{
    public const int InvalidPacket = -4;
    public const int BadArgument = -1;
    public const int BufferTooSmall = -2;
    public const int InternalError = -3;

    /// <summary>Gets the samples per frame at <paramref name="rate"/>.</summary>
    public static int SamplesPerFrame(byte toc, int rate)
    {
        if ((toc & 0x80) != 0)
        {
            return (rate << ((toc >> 3) & 3)) / 400;
        }

        if ((toc & 0x60) == 0x60)
        {
            return (toc & 0x08) != 0 ? rate / 50 : rate / 100;
        }

        int size = (toc >> 3) & 3;
        return size == 3 ? rate * 60 / 1000 : (rate << size) / 100;
    }

    public static OpusBandwidth Bandwidth(byte toc)
    {
        if ((toc & 0x80) != 0)
        {
            OpusBandwidth b = OpusBandwidth.Mediumband + ((toc >> 5) & 3);
            return b == OpusBandwidth.Mediumband ? OpusBandwidth.Narrowband : b;
        }

        return (toc & 0x60) == 0x60
            ? (toc & 0x10) != 0 ? OpusBandwidth.Fullband : OpusBandwidth.Superwideband
            : OpusBandwidth.Narrowband + ((toc >> 5) & 3);
    }

    public static int Channels(byte toc) => (toc & 4) != 0 ? 2 : 1;

    public static int FrameCount(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 1)
        {
            return BadArgument;
        }

        int count = packet[0] & 3;
        return count == 0 ? 1 : count != 3 ? 2 : packet.Length < 2 ? InvalidPacket : packet[1] & 0x3F;
    }

    /// <summary>Gets the samples a packet holds at <paramref name="rate"/>, or a negative error.</summary>
    public static int SampleCount(ReadOnlySpan<byte> packet, int rate)
    {
        int count = FrameCount(packet);
        if (count < 0)
        {
            return count;
        }

        int samples = count * SamplesPerFrame(packet[0], rate);
        return samples * 25 > rate * 3 ? InvalidPacket : samples;
    }

    /// <summary>
    /// Splits a packet into its frames: <paramref name="offsets"/> and <paramref name="sizes"/> (48 entries) receive where each
    /// starts and how long it is. Returns the frame count or a negative error; <paramref name="packetLength"/> receives the bytes
    /// the packet takes, padding included (for self-delimited packets in multistream).
    /// </summary>
    public static int Parse(ReadOnlySpan<byte> data, bool selfDelimited, out byte toc, Span<int> offsets, Span<short> sizes, out int payloadOffset, out int packetLength)
    {
        toc = 0;
        payloadOffset = 0;
        packetLength = 0;
        int len = data.Length;
        if (len == 0)
        {
            return InvalidPacket;
        }

        int framesize = SamplesPerFrame(data[0], 48000);
        bool cbr = false;
        int p = 0;
        toc = data[p++];
        len--;
        int lastSize = len;
        int count;
        int pad = 0;
        int bytes;
        switch (toc & 3)
        {
            case 0:
                count = 1;
                break;
            case 1:
                count = 2;
                cbr = true;
                if (!selfDelimited)
                {
                    if ((len & 1) != 0)
                    {
                        return InvalidPacket;
                    }

                    lastSize = len / 2;
                    sizes[0] = (short)lastSize;
                }

                break;
            case 2:
                count = 2;
                bytes = ParseSize(data[p..], len, out sizes[0]);
                len -= bytes;
                if (sizes[0] < 0 || sizes[0] > len)
                {
                    return InvalidPacket;
                }

                p += bytes;
                lastSize = len - sizes[0];
                break;
            default:
                if (len < 1)
                {
                    return InvalidPacket;
                }

                byte ch = data[p++];
                count = ch & 0x3F;
                if (count <= 0 || framesize * count > 5760)
                {
                    return InvalidPacket;
                }

                len--;
                if ((ch & 0x40) != 0)
                {
                    int padByte;
                    do
                    {
                        if (len <= 0)
                        {
                            return InvalidPacket;
                        }

                        padByte = data[p++];
                        len--;
                        int tmp = padByte == 255 ? 254 : padByte;
                        len -= tmp;
                        pad += tmp;
                    }
                    while (padByte == 255);
                }

                if (len < 0)
                {
                    return InvalidPacket;
                }

                cbr = (ch & 0x80) == 0;
                if (!cbr)
                {
                    lastSize = len;
                    for (int i = 0; i < count - 1; i++)
                    {
                        bytes = ParseSize(data[p..], len, out sizes[i]);
                        len -= bytes;
                        if (sizes[i] < 0 || sizes[i] > len)
                        {
                            return InvalidPacket;
                        }

                        p += bytes;
                        lastSize -= bytes + sizes[i];
                    }

                    if (lastSize < 0)
                    {
                        return InvalidPacket;
                    }
                }
                else if (!selfDelimited)
                {
                    lastSize = len / count;
                    if (lastSize * count != len)
                    {
                        return InvalidPacket;
                    }

                    for (int i = 0; i < count - 1; i++)
                    {
                        sizes[i] = (short)lastSize;
                    }
                }

                break;
        }

        if (selfDelimited)
        {
            bytes = ParseSize(data[p..], len, out sizes[count - 1]);
            len -= bytes;
            if (sizes[count - 1] < 0 || sizes[count - 1] > len)
            {
                return InvalidPacket;
            }

            p += bytes;
            if (cbr)
            {
                if (sizes[count - 1] * count > len)
                {
                    return InvalidPacket;
                }

                for (int i = 0; i < count - 1; i++)
                {
                    sizes[i] = sizes[count - 1];
                }
            }
            else if (bytes + sizes[count - 1] > lastSize)
            {
                return InvalidPacket;
            }
        }
        else
        {
            if (lastSize > 1275)
            {
                return InvalidPacket;
            }

            sizes[count - 1] = (short)lastSize;
        }

        payloadOffset = p;
        for (int i = 0; i < count; i++)
        {
            offsets[i] = p;
            p += sizes[i];
        }

        packetLength = pad + p;
        return count;
    }

    private static int ParseSize(ReadOnlySpan<byte> data, int len, out short size)
    {
        if (len < 1)
        {
            size = -1;
            return -1;
        }

        if (data[0] < 252)
        {
            size = data[0];
            return 1;
        }

        if (len < 2)
        {
            size = -1;
            return -1;
        }

        size = (short)((4 * data[1]) + data[0]);
        return 2;
    }
}

/// <summary>The Ogg Opus identification header, OpusHead (RFC 7845 section 5.1), as Matroska's CodecPrivate and MP4's dOps carry it.</summary>
internal sealed record OpusHead(int Version, int Channels, int PreSkip, int InputSampleRate, int OutputGain, int MappingFamily, int StreamCount, int CoupledCount, byte[] Mapping)
{
    public static OpusHead? Read(ReadOnlySpan<byte> b)
    {
        if (b.Length < 19 || !b.StartsWith("OpusHead"u8) || (b[8] & 0xF0) != 0)
        {
            return null;
        }

        int channels = b[9];
        int family = b[18];
        if (channels == 0)
        {
            return null;
        }

        if (family == 0)
        {
            if (channels > 2)
            {
                return null;
            }

            return new OpusHead(b[8], channels, BinaryPrimitives.ReadUInt16LittleEndian(b[10..]), (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(b[12..]), int.MaxValue),
                BinaryPrimitives.ReadInt16LittleEndian(b[16..]), 0, 1, channels - 1, channels == 1 ? [0] : [0, 1]);
        }

        if (b.Length < 21 + channels)
        {
            return null;
        }

        int streams = b[19];
        int coupled = b[20];
        if (streams == 0 || coupled > streams || streams + coupled > 255)
        {
            return null;
        }

        return new OpusHead(b[8], channels, BinaryPrimitives.ReadUInt16LittleEndian(b[10..]), (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(b[12..]), int.MaxValue),
            BinaryPrimitives.ReadInt16LittleEndian(b[16..]), family, streams, coupled, b.Slice(21, channels).ToArray());
    }

    public byte[] Write()
    {
        bool table = MappingFamily != 0;
        byte[] b = new byte[19 + (table ? 2 + Channels : 0)];
        "OpusHead"u8.CopyTo(b);
        b[8] = 1;
        b[9] = (byte)Channels;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), (ushort)PreSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), (uint)InputSampleRate);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(16), (short)OutputGain);
        b[18] = (byte)MappingFamily;
        if (table)
        {
            b[19] = (byte)StreamCount;
            b[20] = (byte)CoupledCount;
            Mapping.CopyTo(b, 21);
        }

        return b;
    }
}
