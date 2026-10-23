using Egami.Video.Audio.Codecs.Opus;

namespace Egami.Video.Audio.Codecs;

/// <summary>
/// Opus (RFC 6716 with the RFC 8251 updates). Decodes SILK, CELT and hybrid frames at every bandwidth, mono, stereo and
/// the multichannel mapping families 0, 1 and 255 (RFC 7845), with packet loss concealment and in-band FEC. Frames come out as
/// planar float at 48 kHz, Opus's own rate, in the canonical channel order; other rates go through a <see cref="Resampler"/>.
/// Encodes 1 to 8 channels at 8, 12, 16, 24 or 48 kHz (mapping family 0 for mono and stereo, 1 above) in every mode, with the
/// settings of <see cref="OpusEncoderOptions"/>; the default is 64 kbit/s per channel, variable bitrate, 20 ms frames. Both
/// are translated from libopus 1.5.2 and match its output exactly. Ambisonic mappings (families 2 and 3) are not supported.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-opus"/>
/// </example>
public sealed class OpusCodec : AudioCodec
{
    private OpusCodec()
    {
    }

    /// <summary>Gets the Opus codec.</summary>
    public static OpusCodec Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "Opus";

    /// <inheritdoc/>
    public override string CodecId => CodecIds.Opus;

    /// <inheritdoc/>
    public override bool CanEncode => true;

    /// <inheritdoc/>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-opus"/>
    /// </example>
    public override IAudioDecoder CreateDecoder(AudioTrackInfo track, AudioDecoderOptions? options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(context);
        return new OpusDecoder(track, options ?? AudioDecoderOptions.Default, context);
    }

    /// <inheritdoc/>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-opus-encode"/>
    /// </example>
    public override IAudioEncoder CreateEncoder(AudioEncoderOptions options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        return new OpusTrackEncoder(options, context);
    }
}
