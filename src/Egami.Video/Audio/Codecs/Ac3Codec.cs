using Egami.Video.Audio.Codecs.Ac3;

namespace Egami.Video.Audio.Codecs;

/// <summary>
/// AC-3 (Dolby Digital, ATSC A/52:2018), decode only: every channel mode from 1/0 to 3/2 and dual mono, with or without the LFE,
/// at 32, 44.1 and 48 kHz and 32 to 640 kbit/s, with coupling, rematrixing, block switching and both frame CRCs. Frames come
/// out as planar float, 1536 samples each, in Egami.Video's channel order: a 5.1 stream as front left, front right, center,
/// LFE, side left, side right. The stream's dynamic range control is applied in full unless
/// <see cref="Ac3DecoderOptions.DynamicRange"/> says otherwise; dialogue normalization is not applied. Read from .ac3 files and
/// from AC-3 tracks in MP4, Matroska, AVI and CAF. E-AC-3 (<see cref="CodecIds.EAc3"/>) is not decoded.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-ac3-decode"/>
/// </example>
public sealed class Ac3Codec : AudioCodec
{
    private Ac3Codec()
    {
    }

    /// <summary>Gets the AC-3 codec.</summary>
    public static Ac3Codec Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "AC-3";

    /// <inheritdoc/>
    public override string CodecId => CodecIds.Ac3;

    /// <inheritdoc/>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-ac3-decode"/>
    /// </example>
    public override IAudioDecoder CreateDecoder(AudioTrackInfo track, AudioDecoderOptions? options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(context);
        return new Ac3Decoder(track, options ?? AudioDecoderOptions.Default, context);
    }
}
