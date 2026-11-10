namespace Egami.Video.Audio.Codecs.Aac;

/// <summary>
/// The header of an ADTS frame (ISO/IEC 13818-7 6.2 and ISO/IEC 14496-3 1.A.2.2): a 12-bit sync word, the MPEG version, whether a
/// CRC follows, the profile (the object type minus one), the sampling rate index, the channel configuration, the frame length
/// in bytes with the header, the buffer fullness and the number of raw data blocks minus one.
/// </summary>
internal readonly record struct AdtsHeader(
    bool IsMpeg2,
    bool HasCrc,
    int Profile,
    int RateIndex,
    int ChannelConfiguration,
    int FrameBytes,
    int BufferFullness,
    int Blocks)
{
    /// <summary>The bytes of the fixed and variable header, without the CRC.</summary>
    public const int Size = 7;

    /// <summary>The largest frame the 13-bit length field holds.</summary>
    public const int MaxFrameBytes = (1 << 13) - 1;

    /// <summary>The buffer fullness that marks a variable bit rate stream.</summary>
    public const int VariableRate = 0x7FF;

    /// <summary>Gets the bytes before the first raw data block: the header, and with a CRC its check word (and for several blocks their positions).</summary>
    public int HeaderBytes => Size + (HasCrc ? 2 * Blocks : 0);

    /// <summary>Gets the AudioSpecificConfig object type.</summary>
    public int ObjectType => Profile + 1;

    /// <summary>Gets the sampling rate.</summary>
    public int SampleRate => AacConfig.Rates[RateIndex];

    /// <summary>Gets the samples per channel the frame decodes to: 1024 per raw data block.</summary>
    public int Samples => 1024 * Blocks;

    /// <summary>Reads the header at the start of <paramref name="data"/>; returns false when it is not a valid one.</summary>
    public static bool TryRead(ReadOnlySpan<byte> data, out AdtsHeader header)
    {
        header = default;

        // The sync word, then layer 00, which also sets ADTS apart from MPEG audio frames (whose layer is never 00).
        if (data.Length < Size || data[0] != 0xFF || (data[1] & 0xF6) != 0xF0)
        {
            return false;
        }

        int rateIndex = (data[2] >> 2) & 0xF;
        int frameBytes = ((data[3] & 3) << 11) | (data[4] << 3) | (data[5] >> 5);
        bool hasCrc = (data[1] & 1) == 0;
        int blocks = (data[6] & 3) + 1;
        header = new AdtsHeader(
            IsMpeg2: (data[1] & 8) != 0,
            HasCrc: hasCrc,
            Profile: data[2] >> 6,
            RateIndex: rateIndex,
            ChannelConfiguration: ((data[2] & 1) << 2) | (data[3] >> 6),
            FrameBytes: frameBytes,
            BufferFullness: ((data[5] & 0x1F) << 6) | (data[6] >> 2),
            Blocks: blocks);
        return rateIndex < AacConfig.Rates.Length && frameBytes > header.HeaderBytes;
    }

    /// <summary>Gets the AudioSpecificConfig of the frame's stream: the profile's object type, the rate and the channel configuration.</summary>
    public byte[] ToAudioSpecificConfig() => AacConfig.Create(ObjectType, RateIndex, ChannelConfiguration);

    /// <summary>Writes an MPEG-4 header with no CRC and one raw data block, for a frame of <paramref name="frameBytes"/> bytes in all.</summary>
    public static void Write(Span<byte> destination, int profile, int rateIndex, int channelConfiguration, int frameBytes)
    {
        destination[0] = 0xFF;
        destination[1] = 0xF1;
        destination[2] = (byte)((profile << 6) | (rateIndex << 2) | (channelConfiguration >> 2));
        destination[3] = (byte)(((channelConfiguration & 3) << 6) | (frameBytes >> 11));
        destination[4] = (byte)(frameBytes >> 3);
        destination[5] = (byte)(((frameBytes & 7) << 5) | (VariableRate >> 6));
        destination[6] = (byte)((VariableRate & 0x3F) << 2);
    }

    /// <summary>Tells whether another header belongs to the same stream: the same version, profile, rate and channels.</summary>
    public bool Matches(in AdtsHeader other) =>
        other.IsMpeg2 == IsMpeg2 && other.Profile == Profile && other.RateIndex == RateIndex && other.ChannelConfiguration == ChannelConfiguration;
}
