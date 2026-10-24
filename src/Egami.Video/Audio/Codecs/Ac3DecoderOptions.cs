namespace Egami.Video.Audio.Codecs;

/// <summary>How the AC-3 decoder treats the stream's dynamic range control and channels.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-ac3-decode"/>
/// </example>
public sealed record Ac3DecoderOptions
{
    /// <summary>Gets the default options: the stream's dynamic range control in full and its own channels.</summary>
    public static Ac3DecoderOptions Default { get; } = new();

    /// <summary>
    /// Gets how much of the dynamic range control the stream carries (its dynrng gain words) to apply: 1, the default, applies it
    /// in full as the standard asks of a decoder; 0 ignores it for the full dynamic range; values between scale each gain in
    /// decibels, so 0.5 applies half the compression. Values above 1 compress more.
    /// </summary>
    public float DynamicRange { get; init; } = 1;

    /// <summary>
    /// Gets the layout to mix down to, or <see langword="null"/> for the stream's own. The mix follows ATSC A/52's LoRo downmix
    /// with the stream's center and surround mix levels: channels the layout has pass through, the center and surrounds fold into
    /// left and right (or into the center of a mono layout), and all levels are scaled together so the mix cannot clip. The LFE
    /// is kept only when the layout has one. A layout with more speakers than the stream gets silence in the others.
    /// </summary>
    public ChannelLayout? Downmix { get; init; }
}
