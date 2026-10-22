using Egami.Video.Audio.Codecs.Opus;

namespace Egami.Video.Audio.Codecs;

/// <summary>
/// Opus (RFC 6716 with the RFC 8251 updates), decode only: SILK, CELT and hybrid frames at every bandwidth, mono, stereo and
/// the multichannel mapping families 0, 1 and 255 (RFC 7845), with packet loss concealment and in-band FEC. Frames come out as
/// planar float at 48 kHz, Opus's own rate, in the canonical channel order; other rates go through a <see cref="Resampler"/>.
/// The decoder is translated from libopus 1.5.2. Ambisonic mappings (families 2 and 3) are not decoded.
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
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-opus"/>
    /// </example>
    public override IAudioDecoder CreateDecoder(AudioTrackInfo track, AudioDecoderOptions? options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(context);
        return new OpusDecoder(track, options ?? AudioDecoderOptions.Default, context);
    }
}
