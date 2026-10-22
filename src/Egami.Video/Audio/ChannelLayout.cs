using System.Globalization;
using System.Numerics;

namespace Egami.Video.Audio;

/// <summary>
/// Which speaker each channel feeds: a set of <see cref="Audio.Speakers"/> whose channels come in the order of their bits
/// (WAVE_FORMAT_EXTENSIBLE order, the one canonical order every decoder hands out), plus any channels with no position, such
/// as ambisonic or unknown ones, after them. Decoders of codecs with another order (Vorbis and Opus put center second) remap
/// their channels to this one.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Audio.cs" region="audio-layouts"/>
/// </example>
public readonly record struct ChannelLayout
{
    /// <summary>Creates a layout of the given speakers, one channel each.</summary>
    /// <param name="speakers">The speakers.</param>
    public ChannelLayout(Speakers speakers)
        : this(speakers, BitOperations.PopCount((uint)speakers))
    {
    }

    /// <summary>Creates a layout of the given speakers followed by channels with no position.</summary>
    /// <param name="speakers">The speakers.</param>
    /// <param name="channelCount">The number of channels, at least one per speaker.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channelCount"/> is smaller than the number of speakers or above 255.</exception>
    public ChannelLayout(Speakers speakers, int channelCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channelCount, BitOperations.PopCount((uint)speakers));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channelCount, MaxChannels);
        Speakers = speakers;
        ChannelCount = channelCount;
    }

    /// <summary>Gets the most channels a layout holds: 255.</summary>
    public const int MaxChannels = 255;

    /// <summary>Gets the speakers with a channel, in channel order.</summary>
    public Speakers Speakers { get; }

    /// <summary>Gets the number of channels.</summary>
    public int ChannelCount { get; }

    /// <summary>Gets whether every channel has a speaker position.</summary>
    public bool IsPositioned => ChannelCount == BitOperations.PopCount((uint)Speakers);

    /// <summary>Gets a single front center channel.</summary>
    public static ChannelLayout Mono => new(Speakers.FrontCenter);

    /// <summary>Gets front left and right.</summary>
    public static ChannelLayout Stereo => new(Speakers.FrontLeft | Speakers.FrontRight);

    /// <summary>Gets 2.1: front left and right, and LFE.</summary>
    public static ChannelLayout Surround21 => new(Speakers.FrontLeft | Speakers.FrontRight | Speakers.LowFrequency);

    /// <summary>Gets 3.0: front left, right and center.</summary>
    public static ChannelLayout Surround30 => new(Speakers.FrontLeft | Speakers.FrontRight | Speakers.FrontCenter);

    /// <summary>Gets quad: front left and right, back left and right.</summary>
    public static ChannelLayout Quad => new(Speakers.FrontLeft | Speakers.FrontRight | Speakers.BackLeft | Speakers.BackRight);

    /// <summary>Gets 5.0: front left, right and center, back left and right.</summary>
    public static ChannelLayout Surround50 => new(Speakers.FrontLeft | Speakers.FrontRight | Speakers.FrontCenter | Speakers.BackLeft | Speakers.BackRight);

    /// <summary>Gets 5.1: front left, right and center, LFE, back left and right (WAV's and FLAC's 6-channel order).</summary>
    public static ChannelLayout Surround51 => new(Surround50.Speakers | Speakers.LowFrequency);

    /// <summary>Gets 5.1 with side instead of back surrounds.</summary>
    public static ChannelLayout Surround51Side => new(Speakers.FrontLeft | Speakers.FrontRight | Speakers.FrontCenter | Speakers.LowFrequency | Speakers.SideLeft | Speakers.SideRight);

    /// <summary>Gets 6.1: front left, right and center, LFE, back center, side left and right (FLAC's 7-channel order).</summary>
    public static ChannelLayout Surround61 => new(Surround51Side.Speakers | Speakers.BackCenter);

    /// <summary>Gets 7.1: front left, right and center, LFE, back left and right, side left and right.</summary>
    public static ChannelLayout Surround71 => new(Surround51.Speakers | Speakers.SideLeft | Speakers.SideRight);

    /// <summary>Gets 7.1.4: 7.1 with top front left and right and top back left and right.</summary>
    public static ChannelLayout Surround714 => new(Surround71.Speakers | Speakers.TopFrontLeft | Speakers.TopFrontRight | Speakers.TopBackLeft | Speakers.TopBackRight);

    /// <summary>Returns a layout of channels with no speaker positions.</summary>
    /// <param name="channelCount">The number of channels, 1 to 255.</param>
    /// <returns>The layout.</returns>
    public static ChannelLayout Unpositioned(int channelCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channelCount, 1);
        return new ChannelLayout(Speakers.None, channelCount);
    }

    /// <summary>
    /// Returns the layout WAV and FLAC assume for a channel count when the file names none: mono, stereo, 3.0, quad, 5.0, 5.1,
    /// 6.1 and 7.1 for 1 to 8 channels (RFC 9639's defaults), and unpositioned channels above that.
    /// </summary>
    /// <param name="channelCount">The number of channels, 1 to 255.</param>
    /// <returns>The layout.</returns>
    public static ChannelLayout ForChannelCount(int channelCount) => channelCount switch
    {
        1 => Mono,
        2 => Stereo,
        3 => Surround30,
        4 => Quad,
        5 => Surround50,
        6 => Surround51,
        7 => Surround61,
        8 => Surround71,
        _ => Unpositioned(channelCount),
    };

    /// <summary>Returns the speaker a channel feeds, or <see cref="Speakers.None"/> for a channel with no position.</summary>
    /// <param name="channel">The channel index.</param>
    /// <returns>The speaker.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> is not a channel of the layout.</exception>
    public Speakers GetSpeaker(int channel)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(channel, ChannelCount);
        uint bits = (uint)Speakers;
        for (int i = 0; i < channel && bits != 0; i++)
        {
            bits &= bits - 1;
        }

        return (Speakers)(bits & (0 - bits));
    }

    /// <summary>Returns the channel that feeds a speaker, or -1 when the layout has none.</summary>
    /// <param name="speaker">One speaker.</param>
    /// <returns>The channel index, or -1.</returns>
    public int IndexOf(Speakers speaker)
    {
        uint bit = (uint)speaker;
        return bit == 0 || (bit & (bit - 1)) != 0 || ((uint)Speakers & bit) == 0
            ? -1
            : BitOperations.PopCount((uint)Speakers & (bit - 1));
    }

    /// <summary>Returns the layout's name, such as "stereo", "5.1" or "4 channels".</summary>
    /// <returns>The name.</returns>
    public override string ToString()
    {
        if (!IsPositioned)
        {
            return ChannelCount.ToString(CultureInfo.InvariantCulture) + " channels";
        }

        string? name = Speakers switch
        {
            var s when s == Mono.Speakers => "mono",
            var s when s == Stereo.Speakers => "stereo",
            var s when s == Surround21.Speakers => "2.1",
            var s when s == Surround30.Speakers => "3.0",
            var s when s == Quad.Speakers => "quad",
            var s when s == Surround50.Speakers => "5.0",
            var s when s == Surround51.Speakers => "5.1",
            var s when s == Surround51Side.Speakers => "5.1(side)",
            var s when s == Surround61.Speakers => "6.1",
            var s when s == Surround71.Speakers => "7.1",
            var s when s == Surround714.Speakers => "7.1.4",
            _ => null,
        };
        return name ?? $"{ChannelCount.ToString(CultureInfo.InvariantCulture)} channels ({Speakers})";
    }
}
