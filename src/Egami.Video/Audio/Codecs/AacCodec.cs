using Egami.Video.Audio.Codecs.Aac;

namespace Egami.Video.Audio.Codecs;

/// <summary>
/// AAC (MPEG-4 Audio, ISO/IEC 14496-3): AAC LC decode and encode from 8 to 96 kHz, mono to 7.1 (channel configurations 1 to 7), with
/// block switching, TNS, mid/side and intensity stereo and perceptual noise substitution. Frames come out as planar float, 1024
/// samples each, in Egami.Video's channel order: a 5.1 stream as front left, front right, center, LFE, back left, back right.
/// HE-AAC (v1 and v2) decodes its AAC LC core only, without SBR or parametric stereo, at twice the core rate and with a
/// "aac-sbr-ignored" warning; it sounds band-limited. AAC Main, LTP, SSR, the low-delay and error-resilient types, coupling
/// channel elements and channel maps set by a program config element are not decoded. Read from .aac (ADTS) files and from AAC
/// tracks in MP4, Matroska, AVI and CAF. The encoder codes at an average bit rate or a variable-bit-rate quality
/// (<see cref="AacEncoderOptions"/>), with a psychoacoustic model, block switching, TNS, mid/side and intensity stereo and
/// noise substitution; the first packet's 1024 samples of priming go in the container's edit list or CodecDelay.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-aac-decode"/>
/// </example>
public sealed class AacCodec : AudioCodec
{
    private AacCodec()
    {
    }

    /// <summary>Gets the AAC codec.</summary>
    public static AacCodec Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "AAC";

    /// <inheritdoc/>
    public override string CodecId => CodecIds.Aac;

    /// <inheritdoc/>
    public override bool CanEncode => true;

    /// <inheritdoc/>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-aac-decode"/>
    /// </example>
    public override IAudioDecoder CreateDecoder(AudioTrackInfo track, AudioDecoderOptions? options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(context);
        return new AacDecoder(track, options ?? AudioDecoderOptions.Default, context);
    }

    /// <inheritdoc/>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-aac-encode"/>
    /// </example>
    public override IAudioEncoder CreateEncoder(AudioEncoderOptions options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        return new AacEncoder(options, context);
    }
}
