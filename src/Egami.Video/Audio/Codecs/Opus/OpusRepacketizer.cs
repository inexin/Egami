// Translated from libopus 1.5.2 src/repacketizer.c (without packet extensions) and encode_size of src/opus.c.
namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>
/// Builds Opus packets from frames (RFC 6716 section 3.2): one frame is code 0, two are code 1 or 2, more or padded packets code
/// 3. The encoder uses it to pack several 20 ms frames into one packet, to pad CBR packets and to join multistream packets.
/// </summary>
internal sealed class OpusRepacketizer
{
    private const int MaxFrames = 48;

    private readonly int[] _offsets = new int[MaxFrames];
    private readonly short[] _lengths = new short[MaxFrames];
    private readonly int[] _parseOffsets = new int[MaxFrames];
    private readonly short[] _parseSizes = new short[MaxFrames];
    private byte[] _data = new byte[1276 * 6];
    private int _used;
    private byte _toc;
    private int _frameSize;

    /// <summary>Gets the frames added so far.</summary>
    public int FrameCount { get; private set; }

    /// <summary>opus_repacketizer_init: forgets every frame.</summary>
    public void Init()
    {
        FrameCount = 0;
        _used = 0;
    }

    /// <summary>
    /// opus_repacketizer_cat: adds the frames of <paramref name="packet"/>, which must have the same mode, bandwidth, frame size and
    /// channel count as the ones before; returns 0 or <see cref="OpusPacket.InvalidPacket"/>. The frames are copied.
    /// </summary>
    public int Cat(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 1)
        {
            return OpusPacket.InvalidPacket;
        }

        if (FrameCount == 0)
        {
            _toc = packet[0];
            _frameSize = OpusPacket.SamplesPerFrame(packet[0], 8000);
        }
        else if ((_toc & 0xFC) != (packet[0] & 0xFC))
        {
            return OpusPacket.InvalidPacket;
        }

        int count = OpusPacket.FrameCount(packet);
        // Check the 120 ms maximum packet size.
        if (count < 1 || (count + FrameCount) * _frameSize > 960)
        {
            return OpusPacket.InvalidPacket;
        }

        int ret = OpusPacket.Parse(packet, false, out _, _parseOffsets, _parseSizes, out _, out _);
        if (ret < 1)
        {
            return ret;
        }

        for (int i = 0; i < ret; i++)
        {
            int len = _parseSizes[i];
            if (_used + len > _data.Length)
            {
                Array.Resize(ref _data, Math.Max(_data.Length * 2, _used + len));
            }

            packet.Slice(_parseOffsets[i], len).CopyTo(_data.AsSpan(_used));
            _offsets[FrameCount] = _used;
            _lengths[FrameCount] = (short)len;
            _used += len;
            FrameCount++;
        }

        return 0;
    }

    /// <summary>
    /// opus_repacketizer_out_range_impl: writes frames [<paramref name="begin"/>, <paramref name="end"/>) as one packet into
    /// <paramref name="data"/>, at most <paramref name="maxLength"/> bytes, self-delimited (RFC 6716 appendix B) when asked, and
    /// padded to exactly <paramref name="maxLength"/> bytes when <paramref name="pad"/> is set. Returns the length or a negative error.
    /// </summary>
    public int Out(int begin, int end, Span<byte> data, int maxLength, bool selfDelimited = false, bool pad = false)
    {
        if (begin < 0 || begin >= end || end > FrameCount)
        {
            return OpusPacket.BadArgument;
        }

        int count = end - begin;
        ReadOnlySpan<short> len = _lengths.AsSpan(begin, count);
        int totSize = selfDelimited ? 1 + (len[count - 1] >= 252 ? 1 : 0) : 0;
        int ptr = 0;
        if (count == 1)
        {
            // Code 0.
            totSize += len[0] + 1;
            if (totSize > maxLength)
            {
                return OpusPacket.BufferTooSmall;
            }

            data[ptr++] = (byte)(_toc & 0xFC);
        }
        else if (count == 2)
        {
            if (len[1] == len[0])
            {
                // Code 1.
                totSize += (2 * len[0]) + 1;
                if (totSize > maxLength)
                {
                    return OpusPacket.BufferTooSmall;
                }

                data[ptr++] = (byte)((_toc & 0xFC) | 0x1);
            }
            else
            {
                // Code 2.
                totSize += len[0] + len[1] + 2 + (len[0] >= 252 ? 1 : 0);
                if (totSize > maxLength)
                {
                    return OpusPacket.BufferTooSmall;
                }

                data[ptr++] = (byte)((_toc & 0xFC) | 0x2);
                ptr += EncodeSize(len[0], data[ptr..]);
            }
        }

        if (count > 2 || (pad && totSize < maxLength))
        {
            // Code 3. Restart the process for the padding case.
            ptr = 0;
            totSize = selfDelimited ? 1 + (len[count - 1] >= 252 ? 1 : 0) : 0;
            bool vbr = false;
            for (int i = 1; i < count; i++)
            {
                if (len[i] != len[0])
                {
                    vbr = true;
                    break;
                }
            }

            if (vbr)
            {
                totSize += 2;
                for (int i = 0; i < count - 1; i++)
                {
                    totSize += 1 + (len[i] >= 252 ? 1 : 0) + len[i];
                }

                totSize += len[count - 1];
                if (totSize > maxLength)
                {
                    return OpusPacket.BufferTooSmall;
                }

                data[ptr++] = (byte)((_toc & 0xFC) | 0x3);
                data[ptr++] = (byte)(count | 0x80);
            }
            else
            {
                totSize += (count * len[0]) + 2;
                if (totSize > maxLength)
                {
                    return OpusPacket.BufferTooSmall;
                }

                data[ptr++] = (byte)((_toc & 0xFC) | 0x3);
                data[ptr++] = (byte)count;
            }

            int padAmount = pad ? maxLength - totSize : 0;
            if (padAmount != 0)
            {
                data[1] |= 0x40;
                int nb255s = (padAmount - 1) / 255;
                if (totSize + nb255s + 1 > maxLength)
                {
                    return OpusPacket.BufferTooSmall;
                }

                for (int i = 0; i < nb255s; i++)
                {
                    data[ptr++] = 255;
                }

                data[ptr++] = (byte)(padAmount - (255 * nb255s) - 1);
                totSize += padAmount;
            }

            if (vbr)
            {
                for (int i = 0; i < count - 1; i++)
                {
                    ptr += EncodeSize(len[i], data[ptr..]);
                }
            }
        }

        if (selfDelimited)
        {
            ptr += EncodeSize(len[count - 1], data[ptr..]);
        }

        // Copy the actual data.
        for (int i = 0; i < count; i++)
        {
            _data.AsSpan(_offsets[begin + i], len[i]).CopyTo(data[ptr..]);
            ptr += len[i];
        }

        if (pad)
        {
            // Fill padding with zeros.
            data[ptr..maxLength].Clear();
        }

        return totSize;
    }

    /// <summary>opus_packet_pad: pads a packet in place to <paramref name="newLength"/> bytes; returns 0 or a negative error.</summary>
    public int Pad(Span<byte> data, int length, int newLength)
    {
        if (length < 1)
        {
            return OpusPacket.BadArgument;
        }

        if (length == newLength)
        {
            return 0;
        }

        if (length > newLength)
        {
            return OpusPacket.BadArgument;
        }

        Init();
        int ret = Cat(data[..length]);
        if (ret != 0)
        {
            return ret;
        }

        ret = Out(0, FrameCount, data, newLength, false, true);
        return ret > 0 ? 0 : ret;
    }

    /// <summary>opus_packet_unpad: removes the padding of a packet in place; returns its new length or a negative error.</summary>
    public int Unpad(Span<byte> data, int length)
    {
        if (length < 1)
        {
            return OpusPacket.BadArgument;
        }

        Init();
        int ret = Cat(data[..length]);
        if (ret < 0)
        {
            return ret;
        }

        return Out(0, FrameCount, data, length);
    }

    /// <summary>encode_size: one byte below 252, else two.</summary>
    public static int EncodeSize(int size, Span<byte> data)
    {
        if (size < 252)
        {
            data[0] = (byte)size;
            return 1;
        }

        data[0] = (byte)(252 + (size & 0x3));
        data[1] = (byte)((size - data[0]) >> 2);
        return 2;
    }
}
