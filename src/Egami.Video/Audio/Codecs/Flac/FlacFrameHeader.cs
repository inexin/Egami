namespace Egami.Video.Audio.Codecs.Flac;

/// <summary>How a FLAC frame stores its channels (RFC 9639 section 9.1.3).</summary>
internal enum FlacChannels
{
    Independent,
    LeftSide,
    SideRight,
    MidSide,
}

/// <summary>A parsed FLAC frame header (RFC 9639 section 9.1).</summary>
internal readonly record struct FlacFrameHeader(
    bool VariableBlockSize,
    int BlockSize,
    int SampleRate,
    int Channels,
    FlacChannels Assignment,
    int BitsPerSample,
    long Number,
    int Length)
{
    /// <summary>
    /// Gets the first sample of the frame: the coded number itself for variable block sizes, else frame number × the nominal
    /// size. A nominal size of 0 (STREAMINFO's minimum and maximum differ) with the fixed bit is the old variable format of
    /// encoders before FLAC 1.2 (Flake), whose numbers are sample numbers too.
    /// </summary>
    public long FirstSample(int nominalBlockSize) => VariableBlockSize || nominalBlockSize == 0 ? Number : Number * nominalBlockSize;

    /// <summary>Gets the coded number the next frame must carry. See <see cref="FirstSample"/>.</summary>
    public long NextNumber(int nominalBlockSize) => VariableBlockSize || nominalBlockSize == 0 ? Number + BlockSize : Number + 1;

    /// <summary>
    /// Parses a frame header at the start of <paramref name="data"/>, checking its CRC-8. Fields that say "as STREAMINFO" take
    /// the stream's values. Returns null when the bytes are not a valid header.
    /// </summary>
    public static FlacFrameHeader? Parse(ReadOnlySpan<byte> data, FlacStreamInfo? info)
    {
        if (data.Length < 6 || data[0] != 0xFF || (data[1] & 0xFE) != 0xF8)
        {
            return null;
        }

        bool variable = (data[1] & 1) != 0;
        int blockCode = data[2] >> 4;
        int rateCode = data[2] & 0xF;
        int channelCode = data[3] >> 4;
        int sizeCode = (data[3] >> 1) & 7;
        if (blockCode == 0 || rateCode == 15 || channelCode > 10 || sizeCode == 3 || (data[3] & 1) != 0)
        {
            return null;
        }

        // The coded number: UTF-8-style, 1 to 7 bytes, up to 36 bits.
        int p = 4;
        int first = data[p++];
        int extra;
        long number;
        if (first < 0x80)
        {
            extra = 0;
            number = first;
        }
        else if (first is >= 0xC0 and < 0xE0)
        {
            extra = 1;
            number = first & 0x1F;
        }
        else if (first is >= 0xE0 and < 0xF0)
        {
            extra = 2;
            number = first & 0x0F;
        }
        else if (first is >= 0xF0 and < 0xF8)
        {
            extra = 3;
            number = first & 0x07;
        }
        else if (first is >= 0xF8 and < 0xFC)
        {
            extra = 4;
            number = first & 0x03;
        }
        else if (first is >= 0xFC and < 0xFE)
        {
            extra = 5;
            number = first & 0x01;
        }
        else if (first == 0xFE)
        {
            extra = 6;
            number = 0;
        }
        else
        {
            return null;
        }

        if (data.Length < p + extra + 5)
        {
            return null;
        }

        for (int i = 0; i < extra; i++)
        {
            int b = data[p++];
            if ((b & 0xC0) != 0x80)
            {
                return null;
            }

            number = (number << 6) | (uint)(b & 0x3F);
        }

        int blockSize = blockCode switch
        {
            1 => 192,
            >= 2 and <= 5 => 576 << (blockCode - 2),
            6 => data[p++] + 1,
            7 => ((data[p++] << 8) | data[p++]) + 1,
            _ => 256 << (blockCode - 8),
        };
        int rate;
        switch (rateCode)
        {
            case 0:
                if (info is null)
                {
                    return null;
                }

                rate = info.SampleRate;
                break;
            case 12:
                rate = data[p++] * 1000;
                break;
            case 13:
                rate = (data[p++] << 8) | data[p++];
                break;
            case 14:
                rate = ((data[p++] << 8) | data[p++]) * 10;
                break;
            default:
                rate = rateCode switch { 1 => 88200, 2 => 176400, 3 => 192000, 4 => 8000, 5 => 16000, 6 => 22050, 7 => 24000, 8 => 32000, 9 => 44100, 10 => 48000, _ => 96000 };
                break;
        }

        int bits;
        if (sizeCode == 0)
        {
            if (info is null)
            {
                return null;
            }

            bits = info.BitsPerSample;
        }
        else
        {
            bits = sizeCode switch { 1 => 8, 2 => 12, 4 => 16, 5 => 20, 6 => 24, _ => 32 };
        }

        if (p >= data.Length || FlacCrc.Crc8(data[..p]) != data[p])
        {
            return null;
        }

        (int channels, FlacChannels assignment) = channelCode switch
        {
            8 => (2, FlacChannels.LeftSide),
            9 => (2, FlacChannels.SideRight),
            10 => (2, FlacChannels.MidSide),
            _ => (channelCode + 1, FlacChannels.Independent),
        };
        return new FlacFrameHeader(variable, blockSize, rate, channels, assignment, bits, number, p + 1);
    }
}
