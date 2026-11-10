namespace Egami.Video.Audio.Codecs.Aac;

/// <summary>
/// The fields of an MPEG-4 AudioSpecificConfig that containers need (ISO/IEC 14496-3 1.6.2.1): the audio object type, the
/// sampling rate and the channel configuration. For explicitly signalled SBR (object type 5) and parametric stereo (29) the
/// core object type and rate are given too, with the rate the SBR tool outputs.
/// </summary>
internal readonly record struct AacConfig(int ObjectType, int SampleRate, int ChannelConfiguration, int CoreObjectType, int CoreSampleRate)
{
    /// <summary>The object type of AAC LC.</summary>
    public const int LowComplexity = 2;

    /// <summary>The object type of SBR (HE-AAC v1), signalled explicitly.</summary>
    public const int Sbr = 5;

    /// <summary>The object type of parametric stereo (HE-AAC v2), signalled explicitly.</summary>
    public const int ParametricStereo = 29;

    /// <summary>Gets the sampling rates by sampling_frequency_index; 13 and 14 are reserved, 15 means the rate follows in 24 bits.</summary>
    public static ReadOnlySpan<int> Rates => [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    /// <summary>Gets the channels each channel_configuration 1 to 7 holds; 0 means a program config element says.</summary>
    private static ReadOnlySpan<byte> ConfigurationChannels => [0, 1, 2, 3, 4, 5, 6, 8];

    /// <summary>Gets the channels the configuration holds, or 0 when a program config element gives them.</summary>
    public int Channels => ChannelConfiguration is > 0 and < 8 ? ConfigurationChannels[ChannelConfiguration] : 0;

    /// <summary>
    /// Gets the speaker layout of a channel configuration in Egami.Video's (WAV's) order, or <see langword="null"/> for 0 (a
    /// program config element) and the reserved values. Surround pairs are back speakers, and configuration 7 is 7.1 with back
    /// and side pairs, as FFmpeg 7 writes and reads it.
    /// </summary>
    public static ChannelLayout? LayoutOf(int channelConfiguration) => channelConfiguration switch
    {
        1 => ChannelLayout.Mono,
        2 => ChannelLayout.Stereo,
        3 => ChannelLayout.Surround30,
        4 => new ChannelLayout(Speakers.FrontLeft | Speakers.FrontRight | Speakers.FrontCenter | Speakers.BackCenter),
        5 => ChannelLayout.Surround50,
        6 => ChannelLayout.Surround51,
        7 => ChannelLayout.Surround71,
        _ => null,
    };

    /// <summary>Gets the sampling_frequency_index of a rate, or -1 when the rate has none and must be written out in full.</summary>
    public static int RateIndex(int rate) => Rates.IndexOf(rate);

    /// <summary>Reads the start of an AudioSpecificConfig; returns false when it is too short or a rate index is reserved.</summary>
    public static bool TryRead(ReadOnlySpan<byte> data, out AacConfig config)
    {
        config = default;
        BitCursor bits = new(data);
        if (!ReadObjectType(ref bits, out int type) || !ReadRate(ref bits, out int rate) || !bits.TryRead(4, out int channels))
        {
            return false;
        }

        int coreType = type, coreRate = rate, outRate = rate;
        if (type is Sbr or ParametricStereo)
        {
            // The rate after SBR, then the object type of the core it extends.
            if (!ReadRate(ref bits, out outRate) || !ReadObjectType(ref bits, out coreType))
            {
                return false;
            }
        }

        config = new AacConfig(type, outRate, channels, coreType, coreRate);
        return true;
    }

    /// <summary>Writes the two-byte AudioSpecificConfig of an AAC object with an indexed rate and no extensions.</summary>
    public static byte[] Create(int objectType, int rateIndex, int channelConfiguration)
    {
        // GASpecificConfig after the three fields is all zero: 1024-sample frames, no core coder delay, no extension.
        int value = (objectType << 11) | (rateIndex << 7) | (channelConfiguration << 3);
        return [(byte)(value >> 8), (byte)value];
    }

    private static bool ReadObjectType(ref BitCursor bits, out int type)
    {
        if (!bits.TryRead(5, out type))
        {
            return false;
        }

        if (type == 31)
        {
            if (!bits.TryRead(6, out int extended))
            {
                return false;
            }

            type = 32 + extended;
        }

        return true;
    }

    private static bool ReadRate(ref BitCursor bits, out int rate)
    {
        rate = 0;
        if (!bits.TryRead(4, out int index))
        {
            return false;
        }

        if (index == 15)
        {
            return bits.TryRead(24, out rate) && rate > 0;
        }

        if (index >= Rates.Length)
        {
            return false;
        }

        rate = Rates[index];
        return true;
    }

    /// <summary>Reads big-endian bit fields of up to 24 bits.</summary>
    private ref struct BitCursor(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _bit;

        public bool TryRead(int count, out int value)
        {
            value = 0;
            if (_bit + count > _data.Length * 8)
            {
                return false;
            }

            for (int i = 0; i < count; i++, _bit++)
            {
                value = (value << 1) | ((_data[_bit >> 3] >> (7 - (_bit & 7))) & 1);
            }

            return true;
        }
    }
}
