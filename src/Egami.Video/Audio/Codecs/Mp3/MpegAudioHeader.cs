namespace Egami.Video.Audio.Codecs.Mp3;

/// <summary>
/// The 4-byte header of an MPEG audio frame (ISO/IEC 11172-3 section 2.4.2.3, 13818-3 section 2.4.2.3 and the MPEG-2.5
/// extension): version, layer, bit rate, sample rate, padding, channel mode and whether a CRC-16 follows. The layout of the
/// bit fields follows minimp3's header macros (CC0).
/// </summary>
internal readonly struct MpegAudioHeader
{
    /// <summary>The header's size in bytes.</summary>
    public const int Size = 4;

    /// <summary>The largest free-format frame accepted, as minimp3 (more than the standard allows).</summary>
    public const int MaxFreeFormatBytes = 2304;

    /// <summary>The shortest free-format frame taken as real: a header, side information and some main data.</summary>
    public const int MinFreeFormatBytes = 48;

    // Half the bit rates in kbit/s, indexed [MPEG-1][layer - 1][index], from the standards' tables.
    private static ReadOnlySpan<byte> HalfRates =>
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 40, 48, 56, 64, 72, 80,
        0, 4, 8, 12, 16, 20, 24, 28, 32, 40, 48, 56, 64, 72, 80,
        0, 16, 24, 28, 32, 40, 48, 56, 64, 72, 80, 88, 96, 112, 128,
        0, 16, 20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160,
        0, 16, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192,
        0, 16, 32, 48, 64, 80, 96, 112, 128, 144, 160, 176, 192, 208, 224,
    ];

    private readonly uint _bits;

    public MpegAudioHeader(ReadOnlySpan<byte> h) => _bits = ((uint)h[0] << 24) | ((uint)h[1] << 16) | ((uint)h[2] << 8) | h[3];

    private MpegAudioHeader(uint bits) => _bits = bits;

    public byte B1 => (byte)(_bits >> 16);

    public byte B2 => (byte)(_bits >> 8);

    public byte B3 => (byte)_bits;

    /// <summary>Gets whether this is MPEG-1 (else MPEG-2 or MPEG-2.5).</summary>
    public bool IsMpeg1 => (B1 & 0x8) != 0;

    /// <summary>Gets whether this is MPEG-2.5, the half-rate extension of MPEG-2.</summary>
    public bool IsMpeg25 => (B1 & 0x10) == 0;

    /// <summary>Gets the layer, 1 to 3.</summary>
    public int Layer => 4 - ((B1 >> 1) & 3);

    /// <summary>Gets whether a CRC-16 follows the header.</summary>
    public bool HasCrc => (B1 & 1) == 0;

    public int BitrateIndex => B2 >> 4;

    /// <summary>Gets whether the frame uses a free-format bit rate (index 0), whose length comes from the distance to the next header.</summary>
    public bool IsFreeFormat => (B2 & 0xF0) == 0;

    public int SampleRateIndex => (B2 >> 2) & 3;

    /// <summary>Gets the sample rate index over all nine rates: 0 to 2 for MPEG-1 down to 6 to 8 for MPEG-2.5, as minimp3 numbers them.</summary>
    public int RateTableIndex => SampleRateIndex + ((((B1 >> 3) & 1) + ((B1 >> 4) & 1)) * 3);

    public bool HasPadding => (B2 & 2) != 0;

    /// <summary>Gets the channel mode: 0 stereo, 1 joint stereo, 2 dual channel, 3 mono.</summary>
    public int Mode => (B3 >> 6) & 3;

    public int ModeExtension => (B3 >> 4) & 3;

    public bool IsMono => (B3 & 0xC0) == 0xC0;

    /// <summary>Gets whether joint stereo uses mid/side coding (Layer III).</summary>
    public bool IsMidSide => (B3 & 0xE0) == 0x60;

    /// <summary>Gets whether joint stereo uses intensity stereo (Layer III).</summary>
    public bool IsIntensityStereo => (B3 & 0x10) != 0;

    public int Channels => IsMono ? 1 : 2;

    /// <summary>Gets the bit rate in kbit/s, or 0 for free format.</summary>
    public int BitrateKbps => 2 * HalfRates[((IsMpeg1 ? 1 : 0) * 45) + ((((B1 >> 1) & 3) - 1) * 15) + BitrateIndex];

    /// <summary>Gets the sample rate in hertz.</summary>
    public int SampleRate => (SampleRateIndex switch { 0 => 44100, 1 => 48000, _ => 32000 }) >> (IsMpeg1 ? 0 : 1) >> (IsMpeg25 ? 1 : 0);

    /// <summary>Gets the samples per channel the frame decodes to: 384 for Layer I, 576 for MPEG-2 and 2.5 Layer III, else 1152.</summary>
    public int SamplesPerFrame => Layer == 1 ? 384 : (B1 & 14) == 2 ? 576 : 1152;

    /// <summary>Gets the padding slot's size in bytes when the padding bit is set.</summary>
    public int PaddingBytes => HasPadding ? (Layer == 1 ? 4 : 1) : 0;

    /// <summary>Gets the length of the side information that follows the header (and CRC) in a Layer III frame.</summary>
    public int SideInfoBytes => IsMpeg1 ? (IsMono ? 17 : 32) : (IsMono ? 9 : 17);

    /// <summary>Gets whether the bytes are a valid header: sync, a defined layer, bit rate and sample rate.</summary>
    public static bool IsValid(ReadOnlySpan<byte> h) =>
        h.Length >= Size && h[0] == 0xFF && ((h[1] & 0xF0) == 0xF0 || (h[1] & 0xFE) == 0xE2)
        && ((h[1] >> 1) & 3) != 0 && (h[2] >> 4) != 15 && ((h[2] >> 2) & 3) != 3;

    /// <summary>
    /// Gets whether <paramref name="next"/> is a valid header of the same stream: same version, layer and sample rate, and both
    /// free format or both not. The CRC and channel mode may differ between frames.
    /// </summary>
    public static bool Matches(ReadOnlySpan<byte> h, ReadOnlySpan<byte> next) =>
        IsValid(next) && ((h[1] ^ next[1]) & 0xFE) == 0 && ((h[2] ^ next[2]) & 0x0C) == 0 && (((h[2] & 0xF0) == 0) == ((next[2] & 0xF0) == 0));

    /// <summary>Returns the frame's length in bytes without padding, or <paramref name="freeFormatBytes"/> for a free-format frame.</summary>
    public int FrameBytes(int freeFormatBytes)
    {
        int bytes = SamplesPerFrame * BitrateKbps * 125 / SampleRate;
        if (Layer == 1)
        {
            bytes &= ~3;
        }

        return bytes != 0 ? bytes : freeFormatBytes >= MinFreeFormatBytes ? freeFormatBytes : 0;
    }

    /// <summary>Returns the whole frame's length in bytes with padding; 0 for a free-format frame of unknown length.</summary>
    public int TotalBytes(int freeFormatBytes)
    {
        int bytes = FrameBytes(freeFormatBytes);
        return bytes == 0 ? 0 : bytes + PaddingBytes;
    }

    /// <summary>Returns a header with these fields, for writers and tests.</summary>
    public static MpegAudioHeader Create(ReadOnlySpan<byte> bytes) => new(bytes);

    /// <summary>Writes the header's four bytes.</summary>
    public void Write(Span<byte> destination)
    {
        destination[0] = (byte)(_bits >> 24);
        destination[1] = B1;
        destination[2] = B2;
        destination[3] = B3;
    }

    /// <summary>Returns the header with another bit rate index and padding bit, keeping everything else.</summary>
    public MpegAudioHeader WithBitrate(int index, bool padding) =>
        new((_bits & 0xFFFF0DFFu) | ((uint)index << 12) | (padding ? 0x200u : 0));

    public override string ToString() =>
        $"MPEG-{(IsMpeg1 ? "1" : IsMpeg25 ? "2.5" : "2")} Layer {Layer}, {BitrateKbps} kbit/s, {SampleRate} Hz, {Channels} ch";
}
