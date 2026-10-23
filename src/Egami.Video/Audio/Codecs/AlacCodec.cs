using Egami.Video.Audio.Codecs.Alac;

namespace Egami.Video.Audio.Codecs;

/// <summary>
/// Apple Lossless (ALAC), translated from Apple's Apache-2.0 reference codec: decodes 16, 20, 24 and 32-bit streams of 1 to 8
/// channels into planar integer frames; encodes them with packets byte for byte as Apple's encoder makes them, at level 1
/// (normal, the default) or 0 (Apple's fast mode), 4096 samples per packet by default. Read and written in MP4 and M4A,
/// Matroska and CAF; <see cref="TrackInfo.CodecPrivate"/> holds the 24-byte ALACSpecificConfig.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-alac"/>
/// </example>
public sealed class AlacCodec : AudioCodec
{
    private AlacCodec()
    {
    }

    /// <summary>Gets the ALAC codec.</summary>
    public static AlacCodec Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "ALAC";

    /// <inheritdoc/>
    public override string CodecId => CodecIds.Alac;

    /// <inheritdoc/>
    public override bool CanEncode => true;

    /// <inheritdoc/>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-codecs"/>
    /// </example>
    public override IAudioDecoder CreateDecoder(AudioTrackInfo track, AudioDecoderOptions? options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(context);
        return new AlacDecoder(track, options ?? AudioDecoderOptions.Default, context);
    }

    /// <inheritdoc/>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-alac"/>
    /// </example>
    public override IAudioEncoder CreateEncoder(AudioEncoderOptions options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        return new AlacEncoder(options, context);
    }
}
